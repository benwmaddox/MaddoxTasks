using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaddoxTasks.Worker;

public sealed class WorkerHost
{
    private readonly string configPath;
    private readonly string journalPath;
    private readonly ConfigState config;
    private readonly Journal journal;
    private readonly IClock clock;
    private readonly IProcessRunner processes;
    private readonly IGitHubClient github;
    private readonly IRollingLog log;
    private readonly ConcurrentQueue<WorkItem> followups = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly object journalGate = new();
    private readonly object queueGate = new();
    private readonly object wakeGate = new();
    private readonly SemaphoreSlim wakeScheduler = new(0, 1);
    private readonly SemaphoreSlim renderLock = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrencyGate capacity;
    private readonly ResearchAdmission researchAdmission = new();
    private volatile bool paused;
    private volatile string? configError;
    private DateTime nextTickUtc;
    private SchedulerWakeReason pendingWakeReasons;

    public WorkerHost(string configPath, string? stateDirectory = null, IClock? clock = null, IProcessRunner? processes = null, IRollingLog? log = null, IGitHubClient? github = null)
    {
        this.configPath = Path.GetFullPath(configPath);
        this.clock = clock ?? new SystemClock();
        var state = stateDirectory ?? AppContext.BaseDirectory;
        this.log = log ?? new RollingLog(Path.Combine(state, "logs"), this.clock);
        config = new ConfigState(WorkerConfig.Load(this.configPath));
        capacity = new ConcurrencyGate(() => config.Current.MaxConcurrentCodexProcesses);
        journalPath = Path.Combine(state, "worker-journal.json");
        journal = Journal.Load(journalPath);
        this.processes = processes ?? new ProcessRunner(ChildProcessContainmentFactory.Create(OperatingSystem.IsWindows()), this.log);
        this.github = github ?? new GitHubClient(this.processes, () => config.Current, this.log);
        this.log.Write("info", "worker.initialized", new { jobs = journal.Jobs.Count });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        var ct = linked.Token;
        Directory.CreateDirectory(config.Current.WorktreeRoot);
        await PreflightAsync(ct);
        foreach (var job in RecoveryPlanner.JobsToRequeue(journal)) Enqueue(job, RecoveryPlanner.ModeFor(job));
        var background = new[] { WatchFilesAsync(ct), ReadKeysAsync(ct), MonitorAsync(ct) };
        var cadence = new ClaimCadence(clock.UtcNow);
        var wakeReasons = SchedulerWakeReason.Timer;
        _ = TakeSchedulerWakeReasons();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = clock.UtcNow;
                var refillRequested = wakeReasons.HasFlag(SchedulerWakeReason.Manual)
                    || wakeReasons.HasFlag(SchedulerWakeReason.Followup)
                    || wakeReasons.HasFlag(SchedulerWakeReason.CapacityChanged)
                    || wakeReasons.HasFlag(SchedulerWakeReason.ConfigurationChanged);
                if (refillRequested) cadence.RequestImmediateRefill(now);
                var automaticDue = cadence.IsDue(now, config.Current);
                var followup = !followups.IsEmpty;
                if (automaticDue || followup)
                {
                    var outcome = await TickAsync(ct, automaticDue, allowResearch: true);
                    now = clock.UtcNow;
                    if (automaticDue) cadence.CompleteTick(outcome, now);
                }
                nextTickUtc = cadence.NextTickUtc(config.Current);
                await RenderAsync();
                wakeReasons = await WaitForTickAsync(nextTickUtc - clock.UtcNow, ct);
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(background); } catch (OperationCanceledException) { }
            if (processes is IDisposable disposable) disposable.Dispose();
        }
    }

    public void RequestStop() => stop.Cancel();

    internal async Task<FreshClaimOutcome> TickAsync(CancellationToken ct, bool allowFreshClaim = true, bool allowResearch = true)
    {
        log.Write("info", "scheduler.tick", new { active = capacity.Active, queued = followups.Count, paused });
        await ReconcileAsync(ct);
        if (allowResearch && !paused)
        {
            await TryStartResearchAsync(ct);
        }
        DrainFollowups(ct);
        if (paused || !allowFreshClaim) return FreshClaimOutcome.NotAttempted;
        var freshClaim = new FreshClaimAllowance();
        if (freshClaim.TryReserve(capacity))
        {
            var admittedWithSpareCapacity = capacity.Active < config.Current.MaxConcurrentCodexProcesses;
            if (!ClaimAdmission.TrySnapshot(config.Current, configPath, out var claimSnapshot, out var promptError))
            {
                configError = "Cannot claim: " + promptError;
                log.Write("error", "claim.prompt.rejected", new { error = promptError });
                capacity.Release();
                await RenderAsync();
                return FreshClaimOutcome.Unavailable;
            }
            ExecResult claim;
            try { claim = await RunMaddoxAsync("claim", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { capacity.Release(); throw; }
            catch (Exception exception)
            {
                capacity.Release();
                log.Write("error", "claim.failed", new { error = exception.Message });
                return FreshClaimOutcome.Unavailable;
            }
            if (claim.ExitCode != 0 || string.IsNullOrWhiteSpace(claim.Output) || claim.Output.Trim() == "null")
            {
                capacity.Release();
                return FreshClaimOutcome.Unavailable;
            }
            Job job;
            try
            {
                var task = JsonSerializer.Deserialize<TaskDto>(claim.Output, JsonOptions) ?? throw new InvalidDataException("Claim response was empty.");
                var snapshot = claimSnapshot!;
                var adopted = false;
                lock (journalGate)
                {
                    var owned = BlockedWorkspaceAdoption.TryAdopt(journal, task, snapshot.Config.WorktreeRoot, clock.UtcNow);
                    adopted = owned is not null;
                    job = owned ?? new Job { Task = task, Prompt = snapshot.Prompt, Model = snapshot.Config.Model, Effort = snapshot.Config.ReasoningEffort, StartedUtc = clock.UtcNow, PhaseChangedUtc = clock.UtcNow };
                    job.BlockedReassessmentAttempted = false;
                    TaskUpdatePolicy.Seed(job, task);
                    if (!journal.Jobs.Contains(job)) journal.Jobs.Add(job);
                    journal.Save(journalPath);
                }
                await AddCommentAsync(job, ReservationAttribution.Pending, ct);
                job.ReservationOwnerRecorded = true;
                Save(job);
                log.Write("info", "job.claimed", new { task.Sequence, task.Title, task.Repositories, adoptedBlockedWorkspace = adopted });
            }
            catch { capacity.Release(); throw; }
            _ = RunReservedJobAsync(job, RecoveryMode.Initial, ct);
            return admittedWithSpareCapacity ? FreshClaimOutcome.ClaimedWithSpareCapacity : FreshClaimOutcome.ClaimedAtCapacity;
        }
        return FreshClaimOutcome.NotAttempted;
    }

    private void DrainFollowups(CancellationToken ct)
    {
        while (capacity.TryReserve())
        {
            if (!followups.TryDequeue(out var item)) { capacity.Release(); return; }
            lock (queueGate) queued.Remove(item.Job.Task.IssueId);
            _ = RunReservedJobAsync(item.Job, item.Mode, ct);
        }
    }

    private async Task TryStartResearchAsync(CancellationToken ct)
    {
        if (!capacity.TryReserve()) return;
        if (!researchAdmission.TryReserve())
        {
            capacity.Release();
            return;
        }

        var settings = config.Current;
        try
        {
            var cooldown = settings.EffectiveResearchCooldown.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            var claim = await RunMaddoxCommandAsync(["research-claim", "--cooldown", cooldown], ct);
            if (claim.ExitCode != 0)
            {
                log.Write("warning", "research.claim.failed", new { error = claim.Error.Trim(), output = claim.Output.Trim() });
                ReleaseResearchAdmission(signalScheduler: false);
                return;
            }

            if (!TryReadResearchClaim(claim.Output, out var task) || task is null)
            {
                log.Write("info", "research.claim.empty");
                ReleaseResearchAdmission(signalScheduler: false);
                return;
            }

            var schema = WriteSchema("research", ResearchResultSchema);
            log.Write("info", "research.started", new { task.Sequence, task.Title });
            _ = RunResearchJobAsync(task, settings, schema, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReleaseResearchAdmission(signalScheduler: false);
            throw;
        }
        catch (Exception exception)
        {
            log.Write("error", "research.start.failed", new { error = exception.Message });
            ReleaseResearchAdmission(signalScheduler: false);
        }
    }

    private async Task RunResearchJobAsync(TaskDto task, WorkerConfig settings, string schema, CancellationToken ct)
    {
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"maddox-research-{Guid.NewGuid():N}.json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.ClarificationTimeout);
            await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(task), ct);
            var prompt = BuildResearchPrompt(task, snapshotPath);
            var arguments = BuildResearchCodexArguments(settings, schema, prompt);

            var run = await RunResearchCodexAsync(settings, arguments, timeout.Token);
            if (run.ExitCode != 0) throw new InvalidOperationException("Research Codex failed: " + run.Error.Trim());
            var resultJson = ExtractResult(run.Output);
            var plan = ResearchPlanPolicy.Parse(resultJson, task);
            await ApplyResearchPlanAsync(task, plan, ct);
            log.Write("info", "research.completed", new { task.Sequence, outcome = plan.Outcome, mutations = plan.Mutations.Length });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RecordResearchFailureAsync(task, "Research timed out.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown leaves the source task Blocked with its durable marker.
        }
        catch (Exception exception)
        {
            log.Write("error", "research.failed", new { task.Sequence, error = exception.Message });
            await RecordResearchFailureAsync(task, exception.Message, ct);
        }
        finally
        {
            try { File.Delete(snapshotPath); }
            catch (IOException exception) { log.Write("warning", "research.snapshot.cleanup.failed", new { error = exception.Message }); }
            catch (UnauthorizedAccessException exception) { log.Write("warning", "research.snapshot.cleanup.failed", new { error = exception.Message }); }
            ReleaseResearchAdmission(signalScheduler: true);
        }
    }

    private async Task ApplyResearchPlanAsync(TaskDto sourceTask, ResearchPlan plan, CancellationToken ct)
    {
        foreach (var mutation in plan.Mutations)
        {
            switch (mutation.Type)
            {
                case "AddComment":
                    await RunRequiredCommandAsync(new { type = "AddComment", issueId = mutation.IssueId, comment = mutation.Comment }, ResearchPlanPolicy.Actor, ct);
                    break;
                case "UpdateDescription":
                    await RunRequiredCommandAsync(new { type = "UpdateDescription", issueId = mutation.IssueId, description = mutation.Description }, ResearchPlanPolicy.Actor, ct);
                    break;
                case "ChangePriority":
                    await RunRequiredCommandAsync(new { type = "ChangePriority", issueId = mutation.IssueId, newPriority = mutation.NewPriority }, null, ct);
                    break;
                case "AddLabel":
                    await RunRequiredCommandAsync(new { type = "AddLabel", issueId = mutation.IssueId, label = mutation.Label }, null, ct);
                    break;
                case "RemoveLabel":
                    await RunRequiredCommandAsync(new { type = "RemoveLabel", issueId = mutation.IssueId, label = mutation.Label }, null, ct);
                    break;
                case "SetRepositoryLabels":
                    await RunRequiredCommandAsync(new { type = "SetRepositoryLabels", issueId = mutation.IssueId, repositories = mutation.Repositories }, null, ct);
                    break;
                case "ChangeStatus":
                    await RunRequiredCommandAsync(new { type = "ChangeStatus", issueId = mutation.IssueId, newStatus = mutation.NewStatus }, null, ct);
                    break;
                case "CreateIssue":
                    await ApplyResearchCreateAsync(mutation, ct);
                    break;
                default:
                    throw new InvalidDataException("Unsupported research mutation: " + mutation.Type);
            }
        }

        // Findings are written after task mutations. For an unblocked outcome
        // the source transition below is intentionally the final command.
        await RunRequiredCommandAsync(
            new { type = "AddComment", issueId = sourceTask.IssueId, comment = ResearchPlanPolicy.FindingsComment(plan) },
            ResearchPlanPolicy.Actor,
            ct);

        if (plan.Outcome == ResearchPlanPolicy.Unblocked)
        {
            await RunRequiredCommandAsync(
                new { type = "CompleteResearch", issueId = sourceTask.IssueId },
                null,
                ct);
        }
    }

    private async Task ApplyResearchCreateAsync(ResearchMutation mutation, CancellationToken ct)
    {
        var result = await RunRequiredCommandAsync(
            new
            {
                type = "CreateIssue",
                title = mutation.Title,
                description = mutation.Description,
                priority = mutation.Priority ?? 3,
                status = mutation.Status ?? "Next",
                parentId = mutation.ParentId
            },
            ResearchPlanPolicy.Actor,
            ct);
        var issueId = ReadCommandIssueId(result.Output);
        if (mutation.Repositories is { Length: > 0 })
        {
            await RunRequiredCommandAsync(
                new { type = "SetRepositoryLabels", issueId, repositories = mutation.Repositories },
                null,
                ct);
        }
    }

    private async Task RecordResearchFailureAsync(TaskDto task, string reason, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            await RunRequiredCommandAsync(
                new { type = "AddComment", issueId = task.IssueId, comment = "Research worker could not complete: " + reason },
                ResearchPlanPolicy.Actor,
                ct);
        }
        catch (Exception exception)
        {
            log.Write("warning", "research.failure-record.failed", new { task.Sequence, error = exception.Message });
        }
    }

    private void ReleaseResearchAdmission(bool signalScheduler)
    {
        try { researchAdmission.Release(); }
        finally
        {
            capacity.Release();
            if (signalScheduler) SignalScheduler(SchedulerWakeReason.CapacityChanged);
        }
    }

    private async Task<ExecResult> RunResearchCodexAsync(WorkerConfig settings, IEnumerable<string> arguments, CancellationToken ct)
    {
        var terminal = new CodexTerminalEventTracker();
        var input = ProcessArguments.WithPromptOnStandardInput(arguments);
        return await processes.RunAsync(settings.CodexExe, input.Arguments, settings.RepoRoot, ct, terminalOutput: new TerminalOutputDirective(terminal.Observe, TimeSpan.FromSeconds(2)), standardInput: input.StandardInput);
    }

    private Task<ExecResult> RunMaddoxCommandAsync(IEnumerable<string> command, CancellationToken ct)
        => processes.RunAsync(config.Current.MaddoxExe, ["agent", .. command], Path.GetDirectoryName(configPath)!, ct);

    private static bool TryReadResearchClaim(string output, out TaskDto? task)
    {
        task = null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return false;
            if (!root.TryGetProperty("task", out var taskElement) || taskElement.ValueKind == JsonValueKind.Null) return true;
            task = JsonSerializer.Deserialize<TaskDto>(taskElement.GetRawText(), JsonOptions);
            return task is not null;
        }
        catch (JsonException) { return false; }
    }

    private static string ReadCommandIssueId(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("issueId", out var issueId) && issueId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(issueId.GetString()))
                return issueId.GetString()!;
        }
        catch (JsonException) { }
        throw new InvalidDataException("CreateIssue command returned no issue id.");
    }

    public static List<string> BuildResearchCodexArguments(WorkerConfig settings, string schema, string prompt)
        => ["--search", "exec", "--json", "--output-schema", schema,
            "-m", settings.Model, "-c", $"model_reasoning_effort={settings.ReasoningEffort}",
            "--sandbox", "read-only", "--skip-git-repo-check", "-C", settings.RepoRoot, prompt];

    public static string BuildResearchPrompt(TaskDto sourceTask, string snapshotPath)
        => "You are the Maddox blocked-task research worker. Investigate only the single selected blocked task identified below. Parse the file locally to read its description and comments and identify the current blocker. The file contains only this task, not the task database. Read selected fields or recent comments in bounded chunks if its history is long. Do not enumerate or load the whole Maddox task database.\n\n"
            + "Use the available live web search tools to investigate the blocker and find a concrete way to resolve it. Start with focused queries based on this task; open relevant results, prefer primary sources and current documentation, and cite source URLs in your findings. Distinguish verified facts from suggested next steps. If search cannot resolve the blocker or the search tools are unavailable, explain what is missing and leave the task blocked.\n\n"
            + "This is a read-only investigation. You may perform read-only web or other external research when useful. Do not edit files, run commands that mutate state, use Git or GitHub for mutations, create branches, commit, push, open or merge pull requests, send messages, or perform any external mutation or other side effect. The worker process alone will apply the returned task-entry mutations through MaddoxTasks after validating them.\n\n"
            + "You may propose only these task-entry mutations: AddComment, UpdateDescription, ChangePriority, AddLabel, RemoveLabel, SetRepositoryLabels, ChangeStatus on an existing task other than the source task, and CreateIssue. You may include repository labels on a newly created issue; the worker will create it and then apply those labels. Never directly change the source task status. Set outcome to unblocked only when the proposed task-entry changes genuinely remove the blocker; otherwise set stillBlocked. Return JSON matching the supplied schema, with concise findings explaining the evidence and next step.\n\n"
            + "SOURCE TASK:\n"
            + JsonSerializer.Serialize(new { sourceTask.Sequence, sourceTask.IssueId })
            + "\n\nSELECTED TASK JSON FILE (authoritative for this research run; read-only):\n"
            + JsonSerializer.Serialize(snapshotPath);

    private async Task RunReservedJobAsync(Job job, RecoveryMode mode, CancellationToken ct)
    {
        try
        {
            await EnsureReservationAttributionAsync(job, ct);
            if (mode == RecoveryMode.Publish) await ResumePublishingAsync(job, ct);
            else if (mode == RecoveryMode.UnrecoverablePublication) throw new InvalidDataException("Interrupted publication predates durable structured-result journaling; preserved workspace requires manual diagnosis.");
            else await ProcessJobAsync(job, mode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            log.Write("error", "job.failed", new { job.Task.Sequence, error = exception.Message });
            try { await BlockAsync(job, exception.Message, ct); }
            catch (Exception blockException) { log.Write("error", "job.block.failed", new { job.Task.Sequence, error = blockException.Message, originalError = exception.Message }); SetPhase(job, JobPhases.Blocked); }
        }
        finally { capacity.Release(); SignalScheduler(SchedulerWakeReason.CapacityChanged); await RenderAsync(); }
    }

    private async Task EnsureReservationAttributionAsync(Job job, CancellationToken ct)
    {
        if (ReservationAttribution.NeedsPending(job))
        {
            await AddCommentAsync(job, ReservationAttribution.Pending, ct);
            job.ReservationOwnerRecorded = true;
            Save(job);
        }
        if (ReservationAttribution.NeedsExact(job))
        {
            await AddCommentAsync(job, ReservationAttribution.Exact(job.ThreadId!), ct);
            job.ExactReservationOwnerRecorded = true;
            Save(job);
        }
    }

    private async Task ProcessJobAsync(Job job, RecoveryMode mode, CancellationToken ct)
    {
        var repair = mode == RecoveryMode.ResumeRepair;
        var resume = mode is RecoveryMode.ResumeInitial or RecoveryMode.ResumeRepair;
        if (job.Task.Repositories.Length == 0 && !await ClarifyAsync(job, ct)) return;
        if (job.Task.Repositories.Length == 0 || job.Phase == JobPhases.Blocked) return;
        if (job.Workspaces.Count == 0)
        {
            foreach (var repository in job.Task.Repositories)
            {
                job.Workspaces.Add(await MakeWorkspaceAsync(job, repository, ct));
                Save(job);
            }
        }
        else await ValidateOwnedWorkspacesAsync(job, ct);

        job.TaskUpdateWindowClosed = false;
        SetPhase(job, repair ? JobPhases.Repairing : JobPhases.Implementing);
        if (repair)
        {
            var affectedPullRequests = AffectedPullRequests(job);
            foreach (var url in affectedPullRequests)
            {
                job.RepairAttemptsByPullRequest.TryGetValue(url, out var attempts);
                if (!job.RepairStartedUtcByPullRequest.TryGetValue(url, out var started)) job.RepairStartedUtcByPullRequest[url] = started = clock.UtcNow;
                if (attempts >= config.Current.RepairMaxAttempts || clock.UtcNow - started >= config.Current.RepairMaxElapsed)
                { await BlockAsync(job, $"Repair attempt or elapsed-time limit exhausted for {url}.", ct); return; }
                job.RepairAttemptsByPullRequest[url] = attempts + 1;
            }
            job.RepairStartedUtc ??= clock.UtcNow;
            job.RepairAttempts++;
            Save(job);
        }

        var schema = WriteSchema("result", ResultSchema);
        if (job.ExecutionStartHeads.Count == 0)
        {
            foreach (var workspace in job.Workspaces)
                job.ExecutionStartHeads[workspace.Repository] = (await RequireAsync("git", ["rev-parse", "HEAD"], workspace.Directory, ct)).Output.Trim();
            Save(job);
        }
        PendingTaskUpdateBatch? resumeBatch = null;
        if (resume)
        {
            await RefreshTaskUpdatesAsync(job, ct);
            lock (journalGate) if (TaskUpdatePolicy.HasPending(job)) resumeBatch = TaskUpdatePolicy.Capture(job);
        }
        var envelope = BuildEnvelope(job, repair);
        if (resumeBatch is not null) envelope += "\n" + BuildTaskUpdatePrompt(resumeBatch);
        var arguments = resume && job.ThreadId is not null
            ? new List<string> { "exec", "resume", job.ThreadId, "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", envelope }
            : BuildInitialCodexArguments(job, schema, envelope);
        ExecResult run;
        string resultJson;
        if (resumeBatch is not null) TaskUpdatePolicy.BeginApplying(job);
        try
        {
            await RenderAsync();
            run = await RunCodexAsync(job, arguments, ct);
            if (run.ExitCode != 0) throw new InvalidOperationException("Codex failed: " + run.Error.Trim());
            resultJson = ExtractResult(run.Output);
            if (resumeBatch is not null)
            {
                lock (journalGate)
                {
                    TaskUpdatePolicy.MarkDelivered(job, resumeBatch);
                    journal.Save(journalPath);
                }
            }
        }
        finally
        {
            if (resumeBatch is not null) TaskUpdatePolicy.EndApplying(job);
            await RenderAsync();
        }
        if (!job.ExactReservationOwnerRecorded && job.ThreadId is not null)
        {
            await AddCommentAsync(job, ReservationAttribution.Exact(job.ThreadId), ct);
            job.ExactReservationOwnerRecorded = true;
            Save(job);
        }
        resultJson = await DeliverPendingTaskUpdatesAsync(job, resultJson, schema, ct);
        await CleanIgnoredGeneratedOutputsAsync(job, ct);
        job.PendingResultJson = resultJson;
        job.PendingResultIsRepair = repair;
        SetPhase(job, JobPhases.Publishing);
        using var resultDocument = JsonDocument.Parse(resultJson);
        var result = resultDocument.RootElement;
        await CompleteResultAsync(job, result, repair, ct);
    }

    private async Task ResumePublishingAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.PendingResultJson)) throw new InvalidDataException("Interrupted publication has no persisted Codex result.");
        using var resultDocument = JsonDocument.Parse(job.PendingResultJson);
        await CompleteResultAsync(job, resultDocument.RootElement, job.PendingResultIsRepair, ct);
    }

    private async Task CompleteResultAsync(Job job, JsonElement result, bool repair, CancellationToken ct)
    {
        var status = result.GetProperty("status").GetString();
        if (BlockedReassessmentPolicy.ShouldReassess(job, status))
        {
            job.BlockedReassessmentAttempted = true;
            Save(job);
            await CleanIgnoredGeneratedOutputsAsync(job, ct);
            await RefreshTaskUpdatesAsync(job, ct);
            var schema = WriteSchema("result", ResultSchema);
            var batch = TaskUpdatePolicy.Capture(job);
            var update = BuildTaskUpdatePrompt(batch);
            var prompt = "Reassess the blocked result after worker-owned best-effort ignored-output cleanup. Cleanup failures or ignored generated residue are warnings, not blockers. Return completed or noChanges unless another substantive implementation blocker remains."
                + (string.IsNullOrWhiteSpace(update) ? string.Empty : "\n\nInclude this newly queued human task update in the reassessment:\n" + update)
                + "\nReturn the normal required structured result schema.";
            var applyingTaskUpdate = TaskUpdatePolicy.HasPending(job);
            if (applyingTaskUpdate) TaskUpdatePolicy.BeginApplying(job);
            string reassessedJson;
            try
            {
                await RenderAsync();
                var run = await RunContinuationAsync(job, schema, prompt, ct);
                if (run.ExitCode != 0) throw new InvalidOperationException("Codex blocked-result reassessment failed: " + run.Error.Trim());
                reassessedJson = ExtractResult(run.Output);
                if (applyingTaskUpdate) TaskUpdatePolicy.MarkDelivered(job, batch);
            }
            finally
            {
                if (applyingTaskUpdate) TaskUpdatePolicy.EndApplying(job);
                await RenderAsync();
            }
            job.PendingResultJson = reassessedJson;
            Save(job);
            await CleanIgnoredGeneratedOutputsAsync(job, ct);
            using var reassessed = JsonDocument.Parse(reassessedJson);
            await CompleteResultAsync(job, reassessed.RootElement, repair, ct);
            return;
        }
        if (status == "blocked") { await BlockAsync(job, result.GetProperty("summary").GetString() ?? "Codex reported blocked.", ct, $"{job.Model} {job.Effort}"); return; }
        await ValidateResultAsync(job, result, ct);
        ValidateRepairDispositions(job, result, repair);

        if (status == "noChanges" && !repair)
        {
            if (!job.CodexResultCommentRecorded)
            {
                await AddCommentAsync(job, "No changes: " + result.GetProperty("summary").GetString(), $"{job.Model} {job.Effort}", ct);
                job.CodexResultCommentRecorded = true;
                Save(job);
            }
            await ChangeStatusAsync(job, "Done", ct);
            job.PendingResultJson = null;
            SetPhase(job, JobPhases.Done);
            return;
        }

        var repairingChecks = job.PendingCheckFailures.Count > 0;
        var changed = await PublishAsync(job, result, repair, ct);
        if (repair && repairingChecks && !changed)
        {
            await BlockAsync(job, "Codex repair produced no changes for failing CI checks.", ct);
            return;
        }
        if (!repair && !job.CodexResultCommentRecorded)
        {
            await AddCommentAsync(job, "Codex result: " + result.GetProperty("summary").GetString(), $"{job.Model} {job.Effort}", ct);
            job.CodexResultCommentRecorded = true;
            Save(job);
        }
        if (repair) await ApplyReviewDispositionsAsync(job, result, ct);
        job.PendingResultJson = null;
        job.Publication.Clear();
        job.ExecutionStartHeads.Clear();
        SetPhase(job, JobPhases.Monitoring);
    }

    private string BuildEnvelope(Job job, bool repair)
    {
        var restrictions = "Do not claim tasks, mutate Maddox state, create branches, commit, push, create/merge PRs, or reconcile reviews.";
        var repairContext = repair ? $"\nFAILING CHECKS:\n{JsonSerializer.Serialize(job.PendingCheckFailures)}\nACTIONABLE REVIEW THREADS:\n{JsonSerializer.Serialize(job.PendingFeedback)}\nReturn one checkDispositions item for every failing check ID and one threadDispositions item for every review thread ID. Mark review feedback addressed only when the requested change is complete and include the reply to post." : string.Empty;
        return $"{job.Prompt}\nTASK:\n{JsonSerializer.Serialize(job.Task)}\nWORKTREES:\n{JsonSerializer.Serialize(job.Workspaces)}\nRESTRICTIONS:\n{restrictions}{repairContext}";
    }

    private async Task<string> DeliverPendingTaskUpdatesAsync(Job job, string resultJson, string schema, CancellationToken ct)
    {
        while (true)
        {
            PendingTaskUpdateBatch batch;
            lock (journalGate)
            {
                if (!TaskUpdatePolicy.HasPending(job))
                {
                    job.TaskUpdateWindowClosed = true;
                    journal.Save(journalPath);
                    return resultJson;
                }
                batch = TaskUpdatePolicy.Capture(job);
            }
            var prompt = BuildTaskUpdatePrompt(batch) + "\nContinue the same task with these updates and return the normal required structured result schema.";
            TaskUpdatePolicy.BeginApplying(job);
            try
            {
                await RenderAsync();
                var run = await RunContinuationAsync(job, schema, prompt, ct);
                if (run.ExitCode != 0) throw new InvalidOperationException("Codex task-update continuation failed: " + run.Error.Trim());
                resultJson = ExtractResult(run.Output);
                lock (journalGate)
                {
                    TaskUpdatePolicy.MarkDelivered(job, batch);
                    journal.Save(journalPath);
                }
            }
            finally
            {
                TaskUpdatePolicy.EndApplying(job);
                await RenderAsync();
            }
        }
    }

    private Task<ExecResult> RunContinuationAsync(Job job, string schema, string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ThreadId)) throw new InvalidOperationException("Cannot deliver a task update without the existing Codex thread ID.");
        return RunCodexAsync(job, ["exec", "resume", job.ThreadId, "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", prompt], ct);
    }

    public static string BuildTaskUpdatePrompt(PendingTaskUpdateBatch batch)
    {
        if (batch.Description is null && batch.Comments.Length == 0) return string.Empty;
        return "HUMAN TASK UPDATES (ordered, authoritative delta):\n" + JsonSerializer.Serialize(new { descriptionReplacement = batch.Description, userComments = batch.Comments });
    }

    private async Task<bool> ClarifyAsync(Job job, CancellationToken ct)
    {
        SetPhase(job, JobPhases.Clarifying);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.Current.ClarificationTimeout);
        var schema = WriteSchema("clarify", ClarifySchema);
        var prompt = $"Read-only investigation. Given this exact task JSON, identify the smallest unambiguous repository directory set beneath {config.Current.RepoRoot}. Do not edit anything. Choose action=assign when the objective should remain one task, including tightly coupled multi-repository work. Choose action=split only when each repository has a genuinely independently executable objective; a split must create exactly one child task per repository, with at least two children and no repository repeated.\n{JsonSerializer.Serialize(job.Task)}";
        ExecResult run;
        try { run = await RunCodexAsync(job, ["exec", "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", "--sandbox", "read-only", "--skip-git-repo-check", "-C", config.Current.RepoRoot, prompt], timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Repository clarification timed out."); }
        if (run.ExitCode != 0) throw new InvalidOperationException("Repository clarification failed: " + run.Error.Trim());
        var decision = ClarificationPolicy.Parse(ExtractResult(run.Output));
        var proposedRepositories = decision.Action == "split"
            ? decision.Children.Select(child => child.Repository).ToArray()
            : decision.Repositories;
        foreach (var repository in proposedRepositories) await ValidateRepositoryAsync(repository, ct);

        if (decision.Action == "split")
        {
            var children = decision.Children.Select(child => new { child.Title, child.Description, child.Repository }).ToArray();
            var response = await RunCommandAsync(new { type = "SplitIssue", issueId = job.Task.IssueId, children }, null, ct);
            if (response.ExitCode != 0 || !TryReadSuccess(response.Output)) throw new InvalidOperationException("Task split failed: " + response.Output.Trim() + response.Error.Trim());
            SetPhase(job, JobPhases.Done);
            return false;
        }

        var assignment = await RunCommandAsync(new { type = "SetRepositoryLabels", issueId = job.Task.IssueId, repositories = decision.Repositories }, null, ct);
        if (assignment.ExitCode != 0 || !TryReadSuccess(assignment.Output)) throw new InvalidOperationException("Repository assignment failed: " + assignment.Output.Trim() + assignment.Error.Trim());
        job.Task = job.Task with { Repositories = decision.Repositories };
        Save(job);
        return true;
    }

    private async Task ValidateRepositoryAsync(string repository, CancellationToken ct)
    {
        await new RepositoryBootstrap(processes, config.Current.GhExe, config.Current.PrivateRepositoryOwner).EnsureAsync(config.Current.RepoRoot, repository, ct);
    }

    public static List<string> BuildInitialCodexArguments(Job job, string schema, string envelope)
    {
        var arguments = new List<string> { "exec", "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", "--approve-for-me", "-C", job.Workspaces[0].Directory };
        foreach (var workspace in job.Workspaces.Skip(1)) { arguments.Add("--add-dir"); arguments.Add(workspace.Directory); }
        arguments.Add(envelope);
        return arguments;
    }

    private async Task<Workspace> MakeWorkspaceAsync(Job job, string repository, CancellationToken ct)
    {
        var source = await new RepositoryBootstrap(processes, config.Current.GhExe, config.Current.PrivateRepositoryOwner).EnsureAsync(config.Current.RepoRoot, repository, ct);
        var slug = Regex.Replace(job.Task.Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 30) slug = slug[..30];
        if (slug.Length == 0) slug = "task";
        var branch = $"codex/task-{job.Task.Sequence}-{slug}";
        var repositorySlug = Regex.Replace(repository, "[^A-Za-z0-9._-]+", "-");
        var directory = Path.Combine(config.Current.WorktreeRoot, $"{repositorySlug}-{job.Task.Sequence}");
        if (Directory.Exists(directory)) throw new InvalidOperationException("Foreign worktree collision: " + directory);
        await RequireAsync("git", ["fetch", "origin"], source, ct);
        var branchExists = await processes.RunAsync("git", ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], source, ct);
        if (branchExists.ExitCode == 0) throw new InvalidOperationException("Foreign branch collision: " + branch);
        var remoteBranchExists = await processes.RunAsync("git", ["ls-remote", "--exit-code", "--heads", "origin", branch], source, ct);
        if (remoteBranchExists.ExitCode == 0) throw new InvalidOperationException("Foreign remote branch collision: " + branch);
        if (remoteBranchExists.ExitCode != 2) throw new InvalidOperationException("Could not verify remote branch ownership: " + remoteBranchExists.Error.Trim());
        var head = (await processes.RunAsync("git", ["symbolic-ref", "refs/remotes/origin/HEAD", "--short"], source, ct)).Output.Trim();
        if (string.IsNullOrWhiteSpace(head)) head = "origin/main";
        await RequireAsync("git", ["worktree", "add", "-b", branch, directory, head], source, ct);
        var remote = (await RequireAsync("git", ["remote", "get-url", "origin"], source, ct)).Output.Trim();
        return new Workspace(repository, directory, branch, remote, head);
    }

    private async Task ValidateOwnedWorkspacesAsync(Job job, CancellationToken ct)
    {
        foreach (var workspace in job.Workspaces)
        {
            if (!Directory.Exists(workspace.Directory)) throw new InvalidOperationException("Owned worktree is missing: " + workspace.Directory);
            var branch = await RequireAsync("git", ["branch", "--show-current"], workspace.Directory, ct);
            if (!branch.Output.Trim().Equals(workspace.Branch, StringComparison.Ordinal)) throw new InvalidOperationException("Worktree ownership mismatch: " + workspace.Directory);
        }
    }

    private async Task<bool> PublishAsync(Job job, JsonElement result, bool repair, CancellationToken ct)
    {
        SetPhase(job, JobPhases.Publishing);
        var changed = false;
        var reported = ResultRepositories(result);
        foreach (var workspace in job.Workspaces)
        {
            if (!reported[workspace.Repository]) continue;
            if (!job.Publication.TryGetValue(workspace.Repository, out var progress))
                job.Publication[workspace.Repository] = progress = new PublicationProgress();
            var status = await RequireAsync("git", ["status", "--porcelain"], workspace.Directory, ct);
            changed = true;
            if (!progress.CommitCreated)
            {
                if (!string.IsNullOrWhiteSpace(status.Output))
                {
                    await RequireAsync("git", ["add", "-A"], workspace.Directory, ct);
                    await RequireAsync("git", ["commit", "-m", result.GetProperty("commitMessage").GetString() ?? $"Complete task {job.Task.Sequence}"], workspace.Directory, ct);
                }
                else if (!await HasExecutionChangesAsync(job, workspace, ct))
                    throw new InvalidOperationException($"Persisted publication reports changes but no task commit exists for {workspace.Repository}.");
                progress.CommitCreated = true;
                Save(job);
            }

            var localHead = (await RequireAsync("git", ["rev-parse", "HEAD"], workspace.Directory, ct)).Output.Trim();
            var remoteHeadResult = await processes.RunAsync("git", ["ls-remote", "origin", $"refs/heads/{workspace.Branch}"], workspace.Directory, ct);
            if (remoteHeadResult.ExitCode != 0) throw new InvalidOperationException("Could not inspect remote publication state: " + remoteHeadResult.Error.Trim());
            var remoteHead = remoteHeadResult.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!PublicationPolicy.NeedsPush(progress, localHead, remoteHead))
            {
                if (!progress.Pushed) { progress.Pushed = true; Save(job); }
            }
            else
            {
                await RequireAsync("git", ["push", "-u", "origin", workspace.Branch], workspace.Directory, ct);
                progress.Pushed = true;
                Save(job);
            }

            var existing = job.PullRequests.FirstOrDefault(pr => pr.Repository.Equals(workspace.Repository, StringComparison.OrdinalIgnoreCase))?.Url
                ?? progress.PullRequestUrl
                ?? await FindPullRequestAsync(workspace, ct);
            if (existing is null && !repair)
            {
                var created = await RequireAsync(config.Current.GhExe, ["pr", "create", "--head", workspace.Branch, "--title", result.GetProperty("prTitle").GetString() ?? job.Task.Title, "--body", result.GetProperty("prBody").GetString() ?? result.GetProperty("summary").GetString() ?? "Automated task"], workspace.Directory, ct);
                existing = created.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
                log.Write("info", "github.pr.created", new { job.Task.Sequence, workspace.Repository, url = existing });
            }
            if (existing is not null)
            {
                progress.PullRequestUrl = existing;
                if (job.PullRequests.All(pr => !pr.Repository.Equals(workspace.Repository, StringComparison.OrdinalIgnoreCase))) job.PullRequests.Add(new PullRequestState(existing, workspace.Repository));
                Save(job);
            }
        }
        if (!repair && job.PullRequests.Count == 0) throw new InvalidOperationException("Codex reported completion but produced no changes.");
        if (!repair && !job.PullRequestCommentRecorded)
        {
            await AddCommentAsync(job, "Pull requests: " + string.Join(", ", job.PullRequests.Select(pr => pr.Url)), ct);
            job.PullRequestCommentRecorded = true;
            Save(job);
        }
        job.PendingCheckFailures.Clear();
        Save(job);
        return changed;
    }

    private async Task ValidateResultAsync(Job job, JsonElement result, CancellationToken ct)
    {
        var reported = ResultRepositories(result);
        if (reported.Count != job.Workspaces.Count || job.Workspaces.Any(workspace => !reported.ContainsKey(workspace.Repository)))
            throw new InvalidDataException("Codex result repository manifest does not match assigned workspaces.");
        foreach (var workspace in job.Workspaces)
        {
            var hasChanges = await HasExecutionChangesAsync(job, workspace, ct);
            if (reported[workspace.Repository] != hasChanges)
                throw new InvalidDataException($"Codex result change flag does not match repository state for {workspace.Repository}.");
        }
    }

    private static Dictionary<string, bool> ResultRepositories(JsonElement result) => result.GetProperty("repositories").EnumerateArray().ToDictionary(
        item => item.GetProperty("repository").GetString() ?? string.Empty,
        item => item.GetProperty("changed").GetBoolean(),
        StringComparer.OrdinalIgnoreCase);

    private async Task<bool> HasExecutionChangesAsync(Job job, Workspace workspace, CancellationToken ct)
    {
        var status = await RequireAsync("git", ["status", "--porcelain"], workspace.Directory, ct);
        if (!string.IsNullOrWhiteSpace(status.Output)) return true;
        if (job.Publication.TryGetValue(workspace.Repository, out var progress) && progress.CommitCreated) return true;
        if (!job.ExecutionStartHeads.TryGetValue(workspace.Repository, out var startHead)) return false;
        var head = (await RequireAsync("git", ["rev-parse", "HEAD"], workspace.Directory, ct)).Output.Trim();
        return !head.Equals(startHead, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FindPullRequestAsync(Workspace workspace, CancellationToken ct)
    {
        var found = await RequireAsync(config.Current.GhExe, ["pr", "list", "--head", workspace.Branch, "--state", "all", "--limit", "1", "--json", "url"], workspace.Directory, ct);
        using var document = JsonDocument.Parse(found.Output);
        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    private static void ValidateRepairDispositions(Job job, JsonElement result, bool repair)
    {
        if (!repair || job.PendingCheckFailures.Count == 0) return;
        var dispositions = result.GetProperty("checkDispositions").EnumerateArray()
            .Select(item => item.GetProperty("checkId").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var missing = job.PendingCheckFailures.Where(check => !dispositions.Contains(check.Id)).Select(check => check.Name).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Codex result omitted check dispositions for: " + string.Join(", ", missing));
    }

    private async Task ApplyReviewDispositionsAsync(Job job, JsonElement result, CancellationToken ct)
    {
        var dispositions = result.GetProperty("threadDispositions").EnumerateArray().Select(item => new ReviewDisposition(item.GetProperty("threadId").GetString() ?? string.Empty, item.GetProperty("addressed").GetBoolean(), item.GetProperty("replyBody").GetString() ?? string.Empty)).ToArray();
        foreach (var action in FeedbackPolicy.ActionsFor(job, dispositions))
        {
            var feedback = job.PendingFeedback.Last(item => item.ThreadId == action.ThreadId);
            var pullRequest = job.PullRequests.FirstOrDefault(pr => new Uri(feedback.Url).AbsolutePath.Contains($"/{pr.Repository}/", StringComparison.OrdinalIgnoreCase)) ?? job.PullRequests.First();
            var replyKey = ReviewActionLedger.ReplyKey(action.ThreadId);
            var resolveKey = ReviewActionLedger.ResolveKey(action.ThreadId);
            if (ReviewActionLedger.NeedsReply(job, action.ThreadId))
            {
                await github.ReplyAsync(pullRequest.Url, feedback, action.ReplyBody, ct);
                job.ProcessedFeedbackIds.Add(replyKey);
                Save(job);
            }
            if (ReviewActionLedger.NeedsResolve(job, action.ThreadId))
            {
                await github.ResolveAsync(pullRequest.Url, action.ThreadId, ct);
                job.ProcessedFeedbackIds.Add(resolveKey);
                Save(job);
            }
            job.ProcessedFeedbackIds.Add(FeedbackPolicy.ActionKey(action.ThreadId));
            foreach (var item in job.PendingFeedback.Where(item => item.ThreadId == action.ThreadId)) job.ProcessedFeedbackIds.Add(item.CommentNodeId);
            job.PendingFeedback.RemoveAll(item => item.ThreadId == action.ThreadId);
            Save(job);
        }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollTaskUpdatesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { log.Write("warning", "task-updates.poll.failed", new { error = exception.Message }); }
            foreach (var job in SnapshotCleanupPending())
            {
                try { await CleanupAsync(job, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception) { log.Write("warning", "job.cleanup.deferred", new { job.Task.Sequence, error = exception.Message }); }
            }
            foreach (var job in SnapshotJobs(JobPhases.Monitoring))
            {
                try { await MonitorJobAsync(job, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception) { log.Write("error", "monitor.failed", new { job.Task.Sequence, error = exception.Message }); }
            }
            await clock.Delay(config.Current.PrPollInterval, ct);
        }
    }

    private async Task PollTaskUpdatesAsync(CancellationToken ct)
    {
        var active = SnapshotJobs(TaskUpdatePolicy.AcceptsUpdates);
        if (active.Length == 0) return;
        var result = await RunMaddoxAsync("issues", ct);
        if (result.ExitCode != 0)
        {
            log.Write("warning", "task-updates.poll.failed", new { error = result.Error.Trim() });
            return;
        }
        TaskDto[] tasks;
        try { tasks = JsonSerializer.Deserialize<TaskDto[]>(result.Output, JsonOptions) ?? []; }
        catch (JsonException exception) { log.Write("warning", "task-updates.poll.invalid", new { error = exception.Message }); return; }
        var byId = tasks.ToDictionary(task => task.IssueId, StringComparer.OrdinalIgnoreCase);
        lock (journalGate)
        {
            var changed = false;
            foreach (var job in active)
            {
                if (!TaskUpdatePolicy.AcceptsUpdates(job) || !byId.TryGetValue(job.Task.IssueId, out var task)) continue;
                if (TaskUpdatePolicy.Ingest(job, task))
                {
                    changed = true;
                    if (TaskUpdatePolicy.HasPending(job)) log.Write("info", "task-updates.queued", new { job.Task.Sequence, description = job.PendingDescription is not null, comments = job.PendingHumanComments.Count });
                }
            }
            if (changed) journal.Save(journalPath);
        }
        await RenderAsync();
    }

    private async Task RefreshTaskUpdatesAsync(Job job, CancellationToken ct)
    {
        var result = await RunMaddoxAsync("issues", ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not refresh task updates before resuming Codex: " + result.Error.Trim());
        var tasks = JsonSerializer.Deserialize<TaskDto[]>(result.Output, JsonOptions) ?? [];
        var task = tasks.FirstOrDefault(candidate => candidate.IssueId.Equals(job.Task.IssueId, StringComparison.OrdinalIgnoreCase));
        if (task is null) throw new InvalidOperationException("Could not find the active Maddox task before resuming Codex.");
        lock (journalGate)
        {
            TaskUpdatePolicy.Ingest(job, task);
            journal.Save(journalPath);
        }
    }

    private async Task MonitorJobAsync(Job job, CancellationToken ct)
    {
        var allGreen = true;
        var newFeedback = false;
        var snapshots = new List<PullRequestSnapshot>();
        foreach (var pullRequest in job.PullRequests)
        {
            var snapshot = await github.InspectAsync(pullRequest.Url, includeFeedback: true, ct);
            snapshots.Add(snapshot);
            if (snapshot.Merged) continue;
            var failures = snapshot.Failures(config.Current.IgnoredChecks);
            allGreen &= snapshot.IsGreen(config.Current.IgnoredChecks);
            foreach (var failure in failures.Where(failure => job.ProcessedCheckIds.Add(failure.Id))) job.PendingCheckFailures.Add(failure with { PullRequestUrl = pullRequest.Url });
            var additions = FeedbackPolicy.AddNew(job, snapshot.Feedback);
            newFeedback |= additions.Count > 0;
        }

        if (job.PullRequests.Count > 0 && snapshots.All(snapshot => snapshot.Merged))
        {
            await ReconcileAsync(ct);
            job.CleanupPending = true;
            SetPhase(job, JobPhases.Done);
            try { await CleanupAsync(job, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { log.Write("warning", "job.cleanup.deferred", new { job.Task.Sequence, error = exception.Message }); }
            return;
        }

        if (job.PendingCheckFailures.Count > 0 || job.PendingFeedback.Count > 0)
        {
            DashboardSummary.Update(job, string.Join('\n', job.PendingFeedback.Select(item => item.Body)), clock.UtcNow);
            Save(job);
            Enqueue(job, RecoveryMode.ResumeRepair);
        }

        var quietPeriodElapsed = job.ReviewWindow.Update(allGreen, newFeedback, clock.UtcNow, config.Current.ReviewQuietPeriod);
        var clearToReview = allGreen && job.PendingFeedback.Count == 0 && job.PendingCheckFailures.Count == 0;
        if (clearToReview)
        {
            if (!job.ReadyForReviewRecorded)
            {
                await ChangeStatusAsync(job, "ReadyForReview", ct);
                job.ReadyForReviewRecorded = true;
                Save(job);
            }
            if (IsAutoMergeAllowed(job) && quietPeriodElapsed)
            {
                foreach (var pullRequest in job.PullRequests) await github.MergeAsync(pullRequest.Url, ct);
                await ReconcileAsync(ct);
            }
        }
        Save(job);
    }

    private bool IsAutoMergeAllowed(Job job) => job.Workspaces.All(workspace => config.Current.AutoMergeRepositories.Any(allowed => RemoteIdentity(workspace.Remote).Equals(allowed, StringComparison.OrdinalIgnoreCase)));
    private static string[] AffectedPullRequests(Job job)
    {
        var urls = job.PendingCheckFailures.Select(check => check.PullRequestUrl).Where(url => !string.IsNullOrWhiteSpace(url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feedback in job.PendingFeedback)
        {
            var matching = job.PullRequests.FirstOrDefault(pr => feedback.Url.Contains($"/{pr.Repository}/pull/", StringComparison.OrdinalIgnoreCase));
            if (matching is not null) urls.Add(matching.Url);
        }
        if (urls.Count == 0) urls.UnionWith(job.PullRequests.Select(pr => pr.Url));
        return urls.ToArray();
    }
    private static string RemoteIdentity(string remote) { var value = remote.Trim().Replace('\\', '/'); if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4]; var marker = value.IndexOf("github.com", StringComparison.OrdinalIgnoreCase); if (marker >= 0) value = value[(marker + "github.com".Length)..].TrimStart('/', ':'); return value; }

    private async Task CleanupAsync(Job job, CancellationToken ct)
    {
        if (!WorkspaceCleanupPolicy.CanDelete(job))
            throw new InvalidOperationException("Destructive workspace cleanup is allowed only for completed jobs with pending cleanup.");
        var forceAllowed = WorkspaceCleanupPolicy.IsProvenOwned(job, config.Current.WorktreeRoot);
        foreach (var workspace in job.Workspaces)
        {
            var source = Path.GetFullPath(Path.Combine(config.Current.RepoRoot, workspace.Repository));
            if (Directory.Exists(workspace.Directory))
            {
                var remove = await processes.RunAsync("git", ["worktree", "remove", workspace.Directory], source, ct);
                if (remove.ExitCode != 0)
                {
                    if (!forceAllowed) throw new InvalidOperationException("Cleanup ownership could not be proven: " + workspace.Directory);
                    await RequireAsync("git", ["worktree", "remove", "--force", workspace.Directory], source, ct);
                }
            }
            var branch = await processes.RunAsync("git", ["show-ref", "--verify", "--quiet", $"refs/heads/{workspace.Branch}"], source, ct);
            if (branch.ExitCode == 0) await RequireAsync("git", ["branch", "-D", workspace.Branch], source, ct);
        }
        job.CleanupPending = false;
        Save(job);
        log.Write("info", "job.cleaned", new { job.Task.Sequence });
    }

    private async Task CleanIgnoredGeneratedOutputsAsync(Job job, CancellationToken ct)
    {
        if (!WorkspaceCleanupPolicy.IsProvenOwned(job, config.Current.WorktreeRoot))
        {
            log.Write("warning", "job.generated-clean.skipped", new { job.Task.Sequence, reason = "workspace ownership not proven" });
            return;
        }
        foreach (var workspace in job.Workspaces)
        {
            try
            {
                var result = await processes.RunAsync("git", ["clean", "-fdX"], workspace.Directory, ct);
                if (result.ExitCode != 0) log.Write("warning", "job.generated-clean.failed", new { job.Task.Sequence, workspace.Repository, error = result.Error.Trim() });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { log.Write("warning", "job.generated-clean.failed", new { job.Task.Sequence, workspace.Repository, error = exception.Message }); }
        }
    }

    private void Enqueue(Job job, RecoveryMode mode)
    {
        lock (queueGate)
        {
            if (!queued.Add(job.Task.IssueId)) return;
            followups.Enqueue(new WorkItem(job, mode));
        }
        log.Write("info", "job.queued", new { job.Task.Sequence, mode });
        SignalScheduler(SchedulerWakeReason.Followup);
    }
    private void SignalScheduler(SchedulerWakeReason reason)
    {
        lock (wakeGate) pendingWakeReasons |= reason;
        try { wakeScheduler.Release(); } catch (SemaphoreFullException) { }
    }
    private SchedulerWakeReason TakeSchedulerWakeReasons()
    {
        lock (wakeGate)
        {
            var reasons = pendingWakeReasons;
            pendingWakeReasons = SchedulerWakeReason.None;
            return reasons;
        }
    }
    private async Task<SchedulerWakeReason> WaitForTickAsync(TimeSpan delay, CancellationToken ct)
    {
        delay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = clock.Delay(delay, wait.Token);
        var signal = wakeScheduler.WaitAsync(wait.Token);
        var completed = await Task.WhenAny(timer, signal);
        wait.Cancel();
        try { await Task.WhenAll(timer, signal); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        var reasons = TakeSchedulerWakeReasons();
        return completed == timer ? reasons | SchedulerWakeReason.Timer : reasons;
    }

    private async Task WatchFilesAsync(CancellationToken ct)
    {
        var configStamp = File.GetLastWriteTimeUtc(configPath);
        var promptPath = ResolvePromptPath(config.Current);
        var promptStamp = File.Exists(promptPath) ? File.GetLastWriteTimeUtc(promptPath) : DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            await clock.Delay(TimeSpan.FromSeconds(1), ct);
            var newConfigStamp = File.GetLastWriteTimeUtc(configPath);
            if (newConfigStamp != configStamp)
            {
                await clock.Delay(TimeSpan.FromMilliseconds(250), ct);
                configStamp = newConfigStamp;
                if (config.TryReload(configPath, out var error)) { configError = null; log.Write("info", "config.reloaded"); promptPath = ResolvePromptPath(config.Current); SignalScheduler(SchedulerWakeReason.ConfigurationChanged); }
                else { configError = error; log.Write("error", "config.reload.rejected", new { error }); await RenderAsync(); }
            }
            var newPromptStamp = File.Exists(promptPath) ? File.GetLastWriteTimeUtc(promptPath) : DateTime.MinValue;
            if (newPromptStamp != promptStamp)
            {
                promptStamp = newPromptStamp;
                if (ClaimAdmission.TrySnapshot(config.Current, configPath, out _, out var promptError)) { configError = null; log.Write("info", "prompt.changed", new { promptPath }); }
                else { configError = "Cannot claim: " + promptError; log.Write("error", "prompt.reload.rejected", new { error = promptError }); }
                await RenderAsync();
            }
        }
    }
    private string ResolvePromptPath(WorkerConfig settings) => Path.IsPathRooted(settings.PromptFile) ? settings.PromptFile : Path.Combine(Path.GetDirectoryName(configPath)!, settings.PromptFile);

    private async Task ReadKeysAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected) return;
        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable) { await clock.Delay(TimeSpan.FromMilliseconds(100), ct); continue; }
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.P: paused = !paused; log.Write("info", paused ? "claims.paused" : "claims.resumed"); if (!paused) SignalScheduler(SchedulerWakeReason.ConfigurationChanged); break;
                case ConsoleKey.R: SignalScheduler(SchedulerWakeReason.Manual); break;
                case ConsoleKey.Q: RequestStop(); return;
            }
        }
    }

    [Flags]
    private enum SchedulerWakeReason
    {
        None = 0,
        Timer = 1,
        Followup = 2,
        CapacityChanged = 4,
        ConfigurationChanged = 8,
        Manual = 16
    }

    private async Task<ExecResult> RunCodexAsync(Job job, IEnumerable<string> arguments, CancellationToken ct)
    {
        var terminal = new CodexTerminalEventTracker();
        var input = ProcessArguments.WithPromptOnStandardInput(arguments);
        var workingDirectory = job.Workspaces.FirstOrDefault()?.Directory ?? config.Current.RepoRoot;
        return await processes.RunAsync(config.Current.CodexExe, input.Arguments, workingDirectory, ct, line =>
        {
            try
            {
                var (threadId, text) = CodexEventParser.Parse(line);
                var changed = false;
                if (!string.IsNullOrWhiteSpace(threadId) && job.ThreadId != threadId) { job.ThreadId = threadId; changed = true; }
                if (!string.IsNullOrWhiteSpace(text) && DashboardSummary.Update(job, text, clock.UtcNow)) changed = true;
                if (changed) Save(job);
            }
            catch (JsonException) { }
        }, new TerminalOutputDirective(terminal.Observe, TimeSpan.FromSeconds(2)), standardInput: input.StandardInput);
    }

    private async Task BlockAsync(Job job, string reason, CancellationToken ct, string actor = "maddox-worker")
    {
        job.BlockReason = reason;
        SetPhase(job, JobPhases.Blocked);
        await AddCommentAsync(job, "Worker blocked: " + reason, actor, ct);
        await ChangeStatusAsync(job, "Blocked", ct);
    }
    private Task<ExecResult> AddCommentAsync(Job job, string comment, CancellationToken ct) => AddCommentAsync(job, comment, "maddox-worker", ct);
    private Task<ExecResult> AddCommentAsync(Job job, string comment, string actor, CancellationToken ct) => RunRequiredCommandAsync(new { type = "AddComment", issueId = job.Task.IssueId, comment }, actor, ct);
    private Task<ExecResult> ChangeStatusAsync(Job job, string newStatus, CancellationToken ct) => RunRequiredCommandAsync(new { type = "ChangeStatus", issueId = job.Task.IssueId, newStatus }, null, ct);
    private Task<ExecResult> RunCommandAsync(object command, string? actor, CancellationToken ct)
    {
        var arguments = new List<string> { "agent", "command" };
        if (actor is not null) { arguments.Add("--actor"); arguments.Add(actor); }
        arguments.Add("--json"); arguments.Add(JsonSerializer.Serialize(command));
        return processes.RunAsync(config.Current.MaddoxExe, arguments, Path.GetDirectoryName(configPath)!, ct);
    }
    private async Task<ExecResult> RunRequiredCommandAsync(object command, string? actor, CancellationToken ct)
    {
        var result = await RunCommandAsync(command, actor, ct);
        if (result.ExitCode != 0 || !TryReadSuccess(result.Output)) throw new InvalidOperationException("Maddox command failed: " + result.Output.Trim() + result.Error.Trim());
        return result;
    }
    private Task<ExecResult> RunMaddoxAsync(string command, CancellationToken ct) => processes.RunAsync(config.Current.MaddoxExe, ["agent", command], Path.GetDirectoryName(configPath)!, ct);
    private async Task ReconcileAsync(CancellationToken ct) { var result = await RunMaddoxAsync("reconcile-reviews", ct); log.Write(result.ExitCode == 0 ? "info" : "error", "reviews.reconciled", new { result.ExitCode }); }
    private async Task PreflightAsync(CancellationToken ct)
    {
        var settings = config.Current;
        var checks = new[]
        {
            processes.RunAsync(settings.MaddoxExe, ["agent", "issues"], Path.GetDirectoryName(configPath)!, ct),
            processes.RunAsync(settings.CodexExe, ["--version"], settings.RepoRoot, ct),
            processes.RunAsync(settings.GhExe, ["auth", "status"], settings.RepoRoot, ct)
        };
        var results = await Task.WhenAll(checks);
        if (results.Any(result => result.ExitCode != 0)) throw new InvalidOperationException("Startup preflight failed; verify MaddoxTasks, Codex, and GitHub CLI installation/authentication. No task was claimed.");
        log.Write("info", "preflight.succeeded");
    }
    private async Task<ExecResult> RequireAsync(string executable, IEnumerable<string> arguments, string cwd, CancellationToken ct) { var result = await processes.RunAsync(executable, arguments, cwd, ct); if (result.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {result.Error.Trim()}"); return result; }
    private static bool TryReadSuccess(string output) { try { using var document = JsonDocument.Parse(output); return document.RootElement.GetProperty("success").GetBoolean(); } catch { return false; } }

    private void SetPhase(Job job, string phase)
    {
        if (job.Phase != phase) { job.Phase = phase; job.PhaseChangedUtc = clock.UtcNow; }
        log.Write("info", "job.phase", new { job.Task.Sequence, phase });
        Save(job);
    }
    private void Save(Job job) { lock (journalGate) journal.Save(journalPath); _ = RenderAsync(); }
    private Job[] SnapshotJobs(string phase) { lock (journalGate) return journal.Jobs.Where(job => job.Phase == phase).ToArray(); }
    private Job[] SnapshotJobs(Func<Job, bool> predicate) { lock (journalGate) return journal.Jobs.Where(predicate).ToArray(); }
    private Job[] SnapshotCleanupPending() { lock (journalGate) return WorkspaceCleanupPolicy.Pending(journal.Jobs).ToArray(); }
    private async Task RenderAsync()
    {
        if (Console.IsOutputRedirected) return;
        await renderLock.WaitAsync();
        var previousColor = Console.ForegroundColor;
        try
        {
            Console.Clear();
            var scheduleStatus = config.Current.MaxConcurrentCodexProcesses == 0
                ? "paused by concurrency cap"
                : paused ? "claims paused by keyboard" : $"next {nextTickUtc.ToLocalTime():T}";
            ConsoleSegmentWriter.WriteLine([new ConsoleSegment($"Maddox Worker | active {capacity.Active}/{config.Current.MaxConcurrentCodexProcesses} | follow-ups {followups.Count} | {scheduleStatus}", DashboardSegments.Structural)]);
            if (configError is not null) ConsoleSegmentWriter.WriteLine([new ConsoleSegment(DashboardFormatter.Truncate("Configuration error: " + configError, Math.Max(10, Console.WindowWidth - 1)), DashboardSegments.Detail)]);
            foreach (var job in DashboardPolicy.VisibleJobs(journal.Jobs, clock.UtcNow, config.Current.EffectiveBlockedDisplayDuration))
            {
                var width = Math.Max(10, Console.WindowWidth - 1);
                var phase = job.Phase switch
                {
                    JobPhases.Blocked => "Recently blocked",
                    JobPhases.Monitoring => MonitoringDisplay.Describe(job, clock.UtcNow, config.Current.ReviewQuietPeriod, IsAutoMergeAllowed(job)),
                    _ => job.Phase
                };
                phase = TaskUpdatePolicy.DashboardPhase(job, phase);
                ConsoleSegmentWriter.WriteLine(DashboardSegments.Truncate(DashboardSegments.JobHeader(job, phase, clock.UtcNow - job.StartedUtc), width));
                var repositories = job.Workspaces.Count == 0 ? string.Join(", ", job.Task.Repositories) : string.Join(", ", job.Workspaces.Select(workspace => workspace.Repository));
                var pullRequests = job.PullRequests.Count == 0 ? null : string.Join(", ", job.PullRequests.Select(pr => pr.Url));
                ConsoleSegmentWriter.WriteLine(DashboardSegments.Truncate(DashboardSegments.RepositoryLine(repositories, pullRequests), width));
                var details = job.Phase == JobPhases.Blocked && !string.IsNullOrWhiteSpace(job.BlockReason)
                    ? new[] { "Reason: " + DashboardFormatter.LatestLines(job.BlockReason).LastOrDefault() }
                    : DashboardFormatter.NormalizePersistedLatest(job.Latest);
                var detailLimit = job.Phase == JobPhases.Blocked ? 1 : 3;
                var latestChangedLocal = job.Phase == JobPhases.Blocked ? null : job.LatestChangedUtc?.ToLocalTime();
                var updatePrefixWidth = latestChangedLocal is null ? 0 : ("  " + DashboardSegments.FormatUpdateTimestamp(latestChangedLocal.Value) + " ").Length - 2;
                var wrapped = DashboardFormatter.WrapLines(details, Math.Max(3, width - updatePrefixWidth), maxLines: detailLimit);
                for (var index = 0; index < wrapped.Length; index++)
                {
                    var segments = index == 0 && latestChangedLocal is not null
                        ? DashboardSegments.UpdateLine(wrapped[index], latestChangedLocal.Value)
                        : [new ConsoleSegment(wrapped[index], DashboardSegments.Detail)];
                    ConsoleSegmentWriter.WriteLine(segments);
                }
            }
        }
        finally { Console.ForegroundColor = previousColor; renderLock.Release(); }
    }

    public static string ExtractResult(string jsonLines)
    {
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("result", out var result)) return result.ValueKind == JsonValueKind.String ? result.GetString()! : result.GetRawText();
                if (root.TryGetProperty("status", out _) && root.TryGetProperty("summary", out _)) return root.GetRawText();
                if (root.TryGetProperty("type", out var eventType) && eventType.GetString() == "event_msg"
                    && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "task_complete"
                    && payload.TryGetProperty("last_agent_message", out var lastMessage) && lastMessage.ValueKind == JsonValueKind.String)
                {
                    var candidate = lastMessage.GetString()!;
                    using var structured = JsonDocument.Parse(candidate);
                    if (structured.RootElement.ValueKind == JsonValueKind.Object) return candidate;
                }
                if (root.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    var candidate = text.GetString()!;
                    using var structured = JsonDocument.Parse(candidate);
                    if (structured.RootElement.ValueKind == JsonValueKind.Object) return candidate;
                }
            }
            catch (JsonException) { }
        }
        throw new InvalidDataException("Codex emitted no structured result.");
    }
    private static string WriteSchema(string name, string body) { var path = Path.Combine(Path.GetTempPath(), $"maddox-{name}-schema.json"); File.WriteAllText(path, body); return path; }
    private sealed record WorkItem(Job Job, RecoveryMode Mode);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string ClarifySchema = """{"type":"object","properties":{"action":{"enum":["assign","split"]},"repositories":{"type":"array","items":{"type":"string"}},"children":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"description":{"type":"string"},"repository":{"type":"string"},"rationale":{"type":"string"}},"required":["title","description","repository","rationale"],"additionalProperties":false}},"rationale":{"type":"string"},"confidence":{"type":"number","minimum":0,"maximum":1},"ambiguous":{"type":"boolean"}},"required":["action","repositories","children","rationale","confidence","ambiguous"],"additionalProperties":false}""";
    private const string ResultSchema = """{"type":"object","properties":{"status":{"enum":["completed","noChanges","blocked"]},"summary":{"type":"string"},"validationEvidence":{"type":"array","items":{"type":"string"}},"repositories":{"type":"array","items":{"type":"object","properties":{"repository":{"type":"string"},"changed":{"type":"boolean"}},"required":["repository","changed"],"additionalProperties":false}},"commitMessage":{"type":"string"},"prTitle":{"type":"string"},"prBody":{"type":"string"},"checkDispositions":{"type":"array","items":{"type":"object","properties":{"checkId":{"type":"string"},"addressed":{"type":"boolean"},"summary":{"type":"string"}},"required":["checkId","addressed","summary"],"additionalProperties":false}},"threadDispositions":{"type":"array","items":{"type":"object","properties":{"threadId":{"type":"string"},"addressed":{"type":"boolean"},"replyBody":{"type":"string"}},"required":["threadId","addressed","replyBody"],"additionalProperties":false}}},"required":["status","summary","validationEvidence","repositories","commitMessage","prTitle","prBody","checkDispositions","threadDispositions"],"additionalProperties":false}""";
    private const string ResearchResultSchema = """{"type":"object","properties":{"outcome":{"enum":["unblocked","stillBlocked"]},"summary":{"type":"string"},"findings":{"type":"array","items":{"type":"string"}},"mutations":{"type":"array","items":{"type":"object","properties":{"type":{"enum":["AddComment","UpdateDescription","ChangePriority","AddLabel","RemoveLabel","SetRepositoryLabels","ChangeStatus","CreateIssue"]},"issueId":{"type":"string"},"comment":{"type":"string"},"description":{"type":"string"},"newPriority":{"type":"integer","minimum":1,"maximum":5},"label":{"type":"string"},"newStatus":{"enum":["Backlog","Next","Active","Blocked","ReadyForReview","Done","Rejected"]},"repositories":{"type":"array","items":{"type":"string"}},"title":{"type":"string"},"priority":{"type":"integer","minimum":1,"maximum":5},"status":{"enum":["Next","Backlog"]},"parentId":{"type":"string"}},"required":["type"],"additionalProperties":false}}},"required":["outcome","summary","findings","mutations"],"additionalProperties":false}""";
}
