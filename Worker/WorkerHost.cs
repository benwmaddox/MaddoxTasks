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
    private readonly BufferedRefresh dashboardRefresh;
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
        dashboardRefresh = new BufferedRefresh(this.clock, TimeSpan.FromSeconds(15), TryRenderDashboardAsync);
        this.log.Write("info", "worker.initialized", new { jobs = journal.Jobs.Count });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        var ct = linked.Token;
        Directory.CreateDirectory(config.Current.WorktreeRoot);
        await PreflightAsync(ct);
        await RecoverInvalidRetryMetadataAsync(ct);
        foreach (var job in RecoveryPlanner.JobsToRequeue(journal, clock.UtcNow)) Enqueue(job, RecoveryPlanner.ModeFor(job));
        var background = new[] { WatchFilesAsync(ct), ReadKeysAsync(ct), MonitorAsync(ct), dashboardRefresh.RunAsync(ct) };
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
                var retryDue = EnqueueDueRetries(now);
                if (automaticDue || followup || retryDue)
                {
                    var outcome = await TickAsync(ct, automaticDue, allowResearch: true);
                    now = clock.UtcNow;
                    if (automaticDue) cadence.CompleteTick(outcome, now);
                }
                nextTickUtc = cadence.NextTickUtc(config.Current);
                var nextRetry = NextRetryUtc();
                if (nextRetry is { } retryAt && retryAt < nextTickUtc) nextTickUtc = retryAt;
                RequestDashboardRefresh();
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
        if (TryGetCodexUnavailableUntil(out var unavailableUntilUtc))
        {
            log.Write("warning", "codex.usage.deferred", new { unavailableUntilUtc });
            foreach (var job in SnapshotJobs(JobPhases.StatusSyncPending))
            {
                try { await SyncBlockedAsync(job, ct); }
                catch (Exception exception) { log.Write("warning", "job.block.sync.deferred", new { job.Task.Sequence, error = exception.Message }); }
            }
            return FreshClaimOutcome.Unavailable;
        }
        if (allowResearch && !paused)
        {
            await TryStartResearchAsync(ct);
        }
        foreach (var job in SnapshotJobs(JobPhases.StatusSyncPending))
            Enqueue(job, RecoveryMode.SyncBlocked);
        EnqueueDueRetries(clock.UtcNow);
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
                RequestDashboardRefresh();
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
            RecoveryMode mode;
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
                    mode = owned is null ? RecoveryMode.Initial : RecoveryPlanner.ModeForAdopted(job);
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
            if (mode == RecoveryMode.Monitoring)
            {
                SetPhase(job, JobPhases.Monitoring);
                capacity.Release();
                SignalScheduler(SchedulerWakeReason.CapacityChanged);
            }
            else _ = RunReservedJobAsync(job, mode, ct);
            return admittedWithSpareCapacity ? FreshClaimOutcome.ClaimedWithSpareCapacity : FreshClaimOutcome.ClaimedAtCapacity;
        }
        return FreshClaimOutcome.NotAttempted;
    }

    private bool EnqueueDueRetries(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? clock.UtcNow;
        var due = SnapshotJobs(job => WorkerRetryPolicy.IsDue(job, now));
        foreach (var job in due) Enqueue(job, RecoveryPlanner.ModeFor(job));
        return due.Length > 0;
    }

    private DateTime? NextRetryUtc()
    {
        lock (journalGate)
            return journal.Jobs
                .Where(job => WorkerRetryPolicy.HasSafeMetadata(job))
                .Select(job => job.WorkerRetryNextUtc)
                .Where(value => value is not null)
                .Min();
    }

    private async Task RecoverInvalidRetryMetadataAsync(CancellationToken ct)
    {
        Job[] invalid;
        lock (journalGate)
            invalid = journal.Jobs
                .Where(job => job.Phase == JobPhases.RetryWaiting && !WorkerRetryPolicy.HasSafeMetadata(job))
                .ToArray();
        foreach (var job in invalid)
        {
            if (!WorkerRetryPolicy.TryReconstruct(job, clock.UtcNow, config.Current, out var reconstruction)) continue;
            await AddCommentAsync(job, "Worker recovered an interrupted retry: " + reconstruction.Reason, ct);
            Save(job);
        }
    }

    private void DrainFollowups(CancellationToken ct)
    {
        while (capacity.TryReserve())
        {
            if (!followups.TryDequeue(out var item)) { capacity.Release(); return; }
            MarkWorkItemDispatched(item);
            lock (queueGate) queued.Remove(item.Job.Task.IssueId);
            _ = RunReservedJobAsync(item.Job, item.Mode, ct);
        }
    }

    private void MarkWorkItemDispatched(WorkItem item)
    {
        if (item.Mode is not (RecoveryMode.ResumeInitial or RecoveryMode.ResumeRepair or RecoveryMode.Initial)) return;
        lock (journalGate)
        {
            // A due RetryWaiting item is still visible to the scheduler until
            // ProcessJobAsync begins.  Marking it as dispatched before
            // releasing queue ownership closes that duplicate-launch window;
            // if the host crashes here, normal startup recovery sees the
            // implementing/repairing phase and resumes the same work.
            var phase = item.Mode == RecoveryMode.ResumeRepair ? JobPhases.Repairing : JobPhases.Implementing;
            if (item.Job.Phase == JobPhases.RetryWaiting) item.Job.Phase = phase;
            if (WorkerRetryPolicy.ShouldStartFreshSession(item.Job))
            {
                item.Job.ThreadId = null;
                item.Job.ExactReservationOwnerRecorded = false;
                item.Job.WorkerRetryFreshSession = false;
            }
            item.Job.PhaseChangedUtc = clock.UtcNow;
            journal.Save(journalPath);
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
            var failureCooldown = settings.EffectiveResearchFailureCooldown.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            var claim = await RunMaddoxCommandAsync(["research-claim", "--cooldown", cooldown, "--failure-cooldown", failureCooldown], ct);
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
            var tasks = await RunMaddoxCommandAsync(["issues"], ct);
            if (tasks.ExitCode != 0)
                throw new InvalidOperationException("Could not load task research context: " + tasks.Error.Trim());
            await File.WriteAllTextAsync(snapshotPath, BuildResearchSnapshot(task, tasks.Output), ct);
            var prompt = BuildResearchPrompt(task, snapshotPath);
            var arguments = BuildResearchCodexArguments(settings, schema, prompt);

            var run = await RunResearchCodexAsync(settings, arguments, timeout.Token);
            if (run.ExitCode != 0) throw new InvalidOperationException("Research Codex failed: " + ExecResultDiagnostics.Failure(run));
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
        catch (Exception exception) when (CodexUsageLimitPolicy.TryGetRetryUtc(exception.Message, clock.UtcNow, TimeZoneInfo.Local, out var retryUtc))
        {
            RecordCodexUnavailable(retryUtc);
            log.Write("warning", "research.usage.deferred", new { task.Sequence, retryUtc });
        }
        catch (Exception exception) when (CodexClientFailurePolicy.IsWorkerWide(exception.Message))
        {
            var retryUtc = clock.UtcNow + settings.EffectiveResearchFailureCooldown;
            RecordCodexUnavailable(retryUtc);
            log.Write("error", "research.client.deferred", new { task.Sequence, retryUtc, error = exception.Message });
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

        // Findings are written after task mutations. For a terminal outcome
        // the source transition below is intentionally the final command.
        await RunRequiredCommandAsync(
            new { type = "AddComment", issueId = sourceTask.IssueId, comment = ResearchPlanPolicy.FindingsComment(plan) },
            ResearchPlanPolicy.Actor,
            ct);

        if (plan.Outcome is ResearchPlanPolicy.Unblocked or ResearchPlanPolicy.Completed)
        {
            await RunRequiredCommandAsync(
                new
                {
                    type = "CompleteResearch",
                    issueId = sourceTask.IssueId,
                    completionStatus = plan.Outcome == ResearchPlanPolicy.Completed ? "Done" : "Next"
                },
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
                new { type = "AddComment", issueId = task.IssueId, comment = ResearchFailureMarkerPrefix + reason },
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
        return await processes.RunAsync(settings.CodexExe, input.Arguments, settings.RepoRoot, ct, terminalOutput: new TerminalOutputDirective(terminal.Observe, TimeSpan.FromSeconds(2)), standardInput: input.StandardInput,
            environment: WorkspaceProcessEnvironment.IsolatedBuild());
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

    public static string BuildResearchSnapshot(TaskDto sourceTask, string tasksJson)
    {
        using var tasks = JsonDocument.Parse(tasksJson);
        if (tasks.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Task research context must be a JSON array.");
        return JsonSerializer.Serialize(new { selectedTask = sourceTask, tasks = tasks.RootElement });
    }

    public static string BuildResearchPrompt(TaskDto sourceTask, string snapshotPath)
        => "You are the Maddox blocked-task recovery researcher. Investigate the selected source task identified below. Parse the worker-supplied snapshot locally to read its full description and comments and identify the current blocker. The snapshot contains all current task records, including parents, children, dependencies, and terminal work, so verify current state instead of trusting a stale blocker comment. Read selected fields or recent comments in bounded chunks if histories are long; do not load the entire file into the prompt.\n\n"
            + "Use the available live web search tools for read-only web research, read-only local repository inspection, and read-only Git or GitHub CLI queries when useful. Check referenced pull requests, releases, workflows, prerequisite tasks, and available tooling directly. Prefer primary sources and current evidence, cite source URLs in findings, and distinguish verified facts from suggestions. A transient process, emulator, validation, usage-limit, stale-dependency, or prior worker-policy failure is not a true task blocker: establish the next executable attempt and return unblocked.\n\n"
            + "This is a read-only investigation. Do not edit files, run commands that mutate state, use Git or GitHub for mutations, create branches, commit, push, open or merge pull requests, send messages, or perform any external mutation or other side effect. The worker process alone will apply the returned task-entry mutations through MaddoxTasks after validating them.\n\n"
            + "Actively route dependency blockers. If an explicitly required prerequisite task is missing, you may propose one narrowly scoped CreateIssue with the correct repository and parent. If a prerequisite exists, use current task, release, workflow, and PR state to update or activate that prerequisite rather than repeatedly blocking the downstream source. A coordination parent whose objective is fully satisfied by terminal children may be completed. A source whose implementation PR is merged may be completed only when the source acceptance criteria are actually satisfied; do not invent visual, device, credential, or other acceptance requirements absent from the task.\n\n"
            + "You may propose only these task-entry mutations: AddComment, UpdateDescription, ChangePriority, AddLabel, RemoveLabel, SetRepositoryLabels, ChangeStatus on an existing task other than the source task, and CreateIssue. You may include repository labels on a newly created issue; the worker will create it and then apply those labels. Never directly change the source task status. Set outcome to completed only when the source objective is fully satisfied by current evidence or the proposed task-entry mutations and requires no repository implementation. Set outcome to unblocked when the normal worker can now attempt or repair the source task; zero mutations are allowed when the research itself establishes that. Otherwise set stillBlocked, with concrete evidence of the exact unavailable user input, credential, hardware, or authorization and the smallest action that would unblock it. Every mutation object must include every schema field; set fields that do not apply to that mutation type to null. Return JSON matching the supplied schema, with concise findings explaining the evidence and next step.\n\n"
            + "SOURCE TASK:\n"
            + JsonSerializer.Serialize(new { sourceTask.Sequence, sourceTask.IssueId })
            + "\n\nRESEARCH SNAPSHOT JSON FILE (authoritative for this research run; read-only):\n"
            + JsonSerializer.Serialize(snapshotPath);

    private async Task RunReservedJobAsync(Job job, RecoveryMode mode, CancellationToken ct)
    {
        try
        {
            await EnsureReservationAttributionAsync(job, ct);
            if (mode == RecoveryMode.Publish) await ResumePublishingAsync(job, ct);
            else if (mode == RecoveryMode.SyncBlocked) await SyncBlockedAsync(job, ct);
            else if (mode == RecoveryMode.UnrecoverablePublication) await RecoverLegacyPublicationAsync(job, ct);
            else await ProcessJobAsync(job, mode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) when (CodexUsageLimitPolicy.TryGetRetryUtc(exception.Message, clock.UtcNow, TimeZoneInfo.Local, out var retryUtc))
        {
            RecordCodexUnavailable(retryUtc);
            log.Write("warning", "job.usage.deferred", new { job.Task.Sequence, retryUtc });
            try
            {
                await AddCommentAsync(job, $"Worker deferred after the account-wide Codex usage limit was reached. Retry is scheduled after {retryUtc:O}; retained workspace and task state were preserved.", ct);
                await ChangeStatusAsync(job, "Next", ct);
                job.BlockReason = $"Codex usage unavailable until {retryUtc:O}.";
                SetPhase(job, JobPhases.Blocked);
            }
            catch (Exception deferException)
            {
                log.Write("error", "job.defer.failed", new { job.Task.Sequence, error = deferException.Message, originalError = exception.Message });
                await HandleProcessingFailureAsync(job, exception, mode, ct);
            }
        }
        catch (Exception exception)
        {
            log.Write("error", "job.failed", new { job.Task.Sequence, error = exception.Message });
            if (job.Phase == JobPhases.StatusSyncPending)
            {
                log.Write("warning", "job.block.sync.deferred", new { job.Task.Sequence, error = exception.Message });
                return;
            }
            try
            {
                await HandleProcessingFailureAsync(job, exception, mode, ct);
            }
            catch (Exception blockException)
            {
                log.Write("error", "job.block.failed", new { job.Task.Sequence, error = blockException.Message, originalError = exception.Message });
                if (job.Phase != JobPhases.StatusSyncPending) SetPhase(job, JobPhases.StatusSyncPending);
            }
        }
        finally { capacity.Release(); SignalScheduler(SchedulerWakeReason.CapacityChanged); RequestDashboardRefresh(); }
    }

    private async Task HandleProcessingFailureAsync(Job job, Exception exception, RecoveryMode mode, CancellationToken ct)
    {
        var classification = WorkerFailurePolicy.Classify(exception, clock.UtcNow);
        job.LastBlockerKind = classification.Kind;
        job.LastBlockerSummary = classification.Summary;
        job.LastBlockerEvidence = [classification.Summary];

        // A repository-less claim has no retained worktree to reserve.  Put a
        // retryable worker failure back in Next so the next claim can start
        // cleanly; durable workspaces use RetryWaiting below instead.
        if (classification.Retryable
            && WorkerRetryPolicy.TrySchedule(job, classification.Kind, classification.Summary, clock.UtcNow, config.Current, classification.RetryAtUtc, out var delay))
        {
            job.BlockReason = $"{classification.Kind}: {classification.Summary}";
            job.WorkerRetryMode = mode is RecoveryMode.ResumeRepair ? nameof(RecoveryMode.ResumeRepair) : nameof(RecoveryMode.ResumeInitial);
            SetPhase(job, JobPhases.RetryWaiting);
            await AddCommentAsync(job,
                $"Worker retry scheduled ({FormatRetryAttempt(job, classification.Kind)}) for {FormatRetryTime(job, delay)}: {classification.Summary}",
                ct);
            Save(job);
            return;
        }

        var reason = classification.Retryable
            ? $"Worker retry limit exhausted after {job.WorkerRetryAttempts} attempt(s) for fingerprint {job.WorkerRetryFingerprint}: {classification.Summary}"
            : $"{classification.Kind}: {classification.Summary}";
        await BlockAsync(job, reason, ct, $"{job.Model} {job.Effort}");
    }

    private static string FormatDelay(TimeSpan delay) => delay.TotalHours >= 1
        ? $"{Math.Ceiling(delay.TotalHours):0}h"
        : delay.TotalMinutes >= 1
            ? $"{Math.Ceiling(delay.TotalMinutes):0}m"
            : $"{Math.Ceiling(delay.TotalSeconds):0}s";

    private static string FormatRetryAttempt(Job job, string kind) => kind == WorkerBlockerKinds.TransientWorker
        ? $"transient attempt {job.WorkerRetryAttempts}"
        : $"attempt {job.WorkerRetryAttempts}";

    private static string FormatRetryTime(Job job, TimeSpan delay) => job.WorkerRetryResetUtc is { } reset
        ? $"service reset at {reset:O}"
        : "retry in " + FormatDelay(delay);

    private bool TryGetCodexUnavailableUntil(out DateTime unavailableUntilUtc)
    {
        lock (journalGate)
        {
            unavailableUntilUtc = journal.CodexUnavailableUntilUtc ?? default;
            return unavailableUntilUtc > clock.UtcNow;
        }
    }

    private void RecordCodexUnavailable(DateTime retryUtc)
    {
        lock (journalGate)
        {
            if (journal.CodexUnavailableUntilUtc is null || retryUtc > journal.CodexUnavailableUntilUtc)
                journal.CodexUnavailableUntilUtc = retryUtc;
            journal.Save(journalPath);
        }
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
        if (job.Phase == JobPhases.Blocked) return;
        var normalizedRepositories = job.Task.Repositories.Select(repository => RepositoryPathPolicy.Normalize(config.Current.RepoRoot, repository)).ToArray();
        if (!normalizedRepositories.SequenceEqual(job.Task.Repositories, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var repository in normalizedRepositories) await ValidateRepositoryAsync(repository, ct);
            var assignment = await RunCommandAsync(new { type = "SetRepositoryLabels", issueId = job.Task.IssueId, repositories = normalizedRepositories }, null, ct);
            if (assignment.ExitCode != 0 || !TryReadSuccess(assignment.Output)) throw new InvalidOperationException("Repository normalization failed: " + assignment.Output.Trim() + assignment.Error.Trim());
            job.Task = job.Task with { Repositories = normalizedRepositories };
            Save(job);
        }
        if (job.Task.Repositories.Length > 0 && job.Workspaces.Count == 0)
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
        var affectedPullRequests = Array.Empty<string>();
        if (repair)
        {
            affectedPullRequests = AffectedPullRequests(job);
            foreach (var url in affectedPullRequests)
            {
                var pullRequest = job.PullRequests.FirstOrDefault(candidate => candidate.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                var checks = job.PendingCheckFailures.Where(check => check.PullRequestUrl.Equals(url, StringComparison.OrdinalIgnoreCase));
                IEnumerable<ReviewFeedback> feedback = pullRequest is null
                    ? Array.Empty<ReviewFeedback>()
                    : job.PendingFeedback.Where(item => item.Url.Contains($"/{pullRequest.Repository}/pull/", StringComparison.OrdinalIgnoreCase));
                RepairRetryPolicy.BeginGeneration(job, url, pullRequest?.HeadOid, checks, feedback, clock.UtcNow);
                job.RepairAttemptsByPullRequest.TryGetValue(url, out var attempts);
                if (!job.RepairStartedUtcByPullRequest.TryGetValue(url, out var started)) job.RepairStartedUtcByPullRequest[url] = started = clock.UtcNow;
                if (attempts >= config.Current.RepairMaxAttempts || clock.UtcNow - started >= config.Current.RepairMaxElapsed)
                { await BlockAsync(job, $"Repair attempt or elapsed-time limit exhausted for {url}.", ct); return; }
            }
            Save(job);
        }

        var schema = WriteSchema("result", ResultSchema);
        if (job.ExecutionStartHeads.Count == 0)
        {
            foreach (var workspace in job.Workspaces)
                job.ExecutionStartHeads[workspace.Repository] = (await RequireAsync("git", ["rev-parse", "HEAD"], workspace.Directory, ct)).Output.Trim();
            Save(job);
        }
        if (repair) await PrepareMergeabilityRepairsAsync(job, ct);
        PendingTaskUpdateBatch? resumeBatch = null;
        if (resume)
        {
            await RefreshTaskUpdatesAsync(job, ct);
            lock (journalGate) if (TaskUpdatePolicy.HasPending(job)) resumeBatch = TaskUpdatePolicy.Capture(job);
        }
        var envelope = BuildEnvelope(job, repair);
        if (resumeBatch is not null) envelope += "\n" + BuildTaskUpdatePrompt(resumeBatch);
        var arguments = resume && job.ThreadId is not null
            ? BuildContinuationCodexArguments(job, schema, envelope)
            : BuildInitialCodexArguments(job, schema, envelope, config.Current.RepoRoot);
        ExecResult run;
        string resultJson;
        if (resumeBatch is not null) TaskUpdatePolicy.BeginApplying(job);
        try
        {
            RequestDashboardRefresh();
            run = await RunCodexAsync(job, arguments, ct);
            if (run.ExitCode != 0) throw new InvalidOperationException("Codex failed: " + ExecResultDiagnostics.Failure(run));
            resultJson = ExtractResult(run.Output);
            // Fresh Codex turns must opt into the lifecycle contract.  This
            // is intentionally strict here; only a persisted journal result
            // gets the legacy compatibility path in ResumePublishingAsync.
            _ = WorkerResultPolicy.Parse(resultJson);
            if (repair)
            {
                foreach (var url in affectedPullRequests)
                {
                    job.RepairAttemptsByPullRequest.TryGetValue(url, out var attempts);
                    job.RepairAttemptsByPullRequest[url] = attempts + 1;
                }
                job.RepairStartedUtc ??= clock.UtcNow;
                job.RepairAttempts++;
                Save(job);
            }
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
            RequestDashboardRefresh();
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

    private async Task RecoverLegacyPublicationAsync(Job job, CancellationToken ct)
    {
        if (job.PullRequests.Count > 0)
        {
            job.PendingResultJson = null;
            SetPhase(job, JobPhases.Monitoring);
            await MonitorJobAsync(job, ct);
            return;
        }

        // Older journals could crash after creating task-owned commits but
        // before persisting the structured publication manifest. Rebuild a
        // conservative baseline from the retained branch's merge base so the
        // normal changed-state validation can publish those commits safely.
        job.AdoptedBlockedWorkspace = true;
        foreach (var workspace in job.Workspaces)
        {
            var baseRef = string.IsNullOrWhiteSpace(workspace.BaseRef)
                ? "origin/HEAD"
                : workspace.BaseRef.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ? workspace.BaseRef : "origin/" + workspace.BaseRef;
            var mergeBase = await RequireAsync("git", ["merge-base", "HEAD", baseRef], workspace.Directory, ct);
            job.ExecutionStartHeads[workspace.Repository] = mergeBase.Output.Trim();
        }
        job.PendingResultJson = null;
        Save(job);
        await ProcessJobAsync(job, string.IsNullOrWhiteSpace(job.ThreadId) ? RecoveryMode.Initial : RecoveryMode.ResumeInitial, ct);
    }

    private async Task CompleteResultAsync(Job job, JsonElement result, bool repair, CancellationToken ct)
    {
        // ResumePublishingAsync may be replaying a result written by an older
        // worker.  Its explicit compatibility path defaults missing lifecycle
        // metadata conservatively; fresh Codex turns are parsed strictly in
        // ProcessJobAsync and every continuation before they are journaled.
        var structured = WorkerResultPolicy.Parse(result, allowLegacy: true);
        var status = structured.Status;
        job.LastBlockerKind = structured.Blocker.Kind;
        job.LastBlockerSummary = structured.Blocker.Summary;
        job.LastBlockerEvidence = structured.Blocker.Evidence;
        job.HumanReviewRequested |= structured.Blocker.IsHumanReview;

        // Keep the one-time legacy reassessment for journals produced before
        // typed blockers existed.  New workerRepairable/transientWorker
        // results use the bounded retained-workspace retry below instead.
        if (structured.IsLegacy && status == "blocked" && BlockedReassessmentPolicy.ShouldReassess(job, status))
        {
            await ReassessLegacyBlockedResultAsync(job, repair, ct);
            return;
        }
        if (status == "blocked")
        {
            var repairManifest = repair ? ValidateResultManifest(job, result) : null;
            if (repairManifest?.Values.Any(changed => changed) == true)
            {
                ValidateRepairDispositions(job, result, repair: true);
                var actualChanges = new List<bool>();
                foreach (var workspace in job.Workspaces)
                    actualChanges.Add(await HasExecutionChangesAsync(job, workspace, ct));
                if (actualChanges.Any(changed => changed))
                {
                    await ValidateResultAsync(job, result, ct);
                    await PublishAsync(job, result, repair: true, ct);
                    await ApplyReviewDispositionsAsync(job, result, ct);
                    job.Publication.Clear();
                    job.ExecutionStartHeads.Clear();
                    Save(job);
                }
            }
            await HandleBlockedResultAsync(job, structured.Blocker, repair, ct);
            return;
        }
        if (await ShouldReassessAdoptedWorkspaceResultAsync(job, result, ct))
        {
            job.AdoptedResultReassessmentAttempted = true;
            Save(job);
            var schema = WriteSchema("result", ResultSchema);
            const string prompt = "Reassess the structured result for this retained worker-owned workspace. Existing staged, unstaged, untracked, or committed changes in the supplied worktree are task-owned work from the previous attempt, not unrelated user changes. Inspect and preserve them, finish and validate the task, and report changed:true for each repository containing that retained task work so the worker can commit and publish it. Return changed:false only when the repository is clean and contains no task-owned commit after the execution start. Return the normal required structured result schema.";
            RequestDashboardRefresh();
            var run = await RunContinuationAsync(job, schema, prompt, ct);
            if (run.ExitCode != 0) throw new InvalidOperationException("Codex retained-workspace result reassessment failed: " + ExecResultDiagnostics.Failure(run));
            var reassessedJson = ExtractResult(run.Output);
            _ = WorkerResultPolicy.Parse(reassessedJson);
            job.PendingResultJson = reassessedJson;
            Save(job);
            using var reassessed = JsonDocument.Parse(reassessedJson);
            await CompleteResultAsync(job, reassessed.RootElement, repair, ct);
            return;
        }
        await ValidateResultAsync(job, result, ct);
        ValidateRepairDispositions(job, result, repair);

        var reportedChanges = ResultRepositories(result).Values.Any(changed => changed);
        var legacyReviewHandoff = structured.IsLegacy && job.PullRequests.Count > 0 && !reportedChanges;
        var humanReview = structured.Blocker.IsHumanReview || legacyReviewHandoff;
        job.HumanReviewRequested |= humanReview;
        if (status == "noChanges" && !repair && !humanReview)
        {
            if (!job.CodexResultCommentRecorded)
            {
                await AddCommentAsync(job, "No changes: " + result.GetProperty("summary").GetString(), $"{job.Model} {job.Effort}", ct);
                job.CodexResultCommentRecorded = true;
                Save(job);
            }
            await ChangeStatusAsync(job, "Done", ct);
            job.PendingResultJson = null;
            WorkerRetryPolicy.Clear(job);
            SetPhase(job, JobPhases.Done);
            return;
        }

        // A humanReview result is a request to hand off an already-complete
        // implementation.  A task with no known PR cannot remain Active on
        // that claim forever, so fail it safely and leave an actionable record.
        if (humanReview && job.PullRequests.Count == 0 && !reportedChanges)
        {
            await BlockAsync(job, "humanReview requested but no pull request is known; publish or identify the pull request before requesting review.", ct, $"{job.Model} {job.Effort}");
            return;
        }

        var repairingChecks = job.PendingCheckFailures.Count > 0;
        var checksBeforePublication = job.PendingCheckFailures.ToArray();
        // noChanges + humanReview deliberately retains the existing PR and
        // skips publication.  This is the common reclaimed-workspace handoff.
        var changed = humanReview && !reportedChanges
            ? false
            : await PublishAsync(job, result, repair, ct);
        if (repair && repairingChecks && !changed)
        {
            await ApplyReviewDispositionsAsync(job, result, ct);
            RepairRetryPolicy.RearmChecks(job, checksBeforePublication);
            job.PendingResultJson = null;
            job.Publication.Clear();
            job.ExecutionStartHeads.Clear();
            WorkerRetryPolicy.Clear(job);
            SetPhase(job, JobPhases.Monitoring);
            if (humanReview) await MonitorJobAsync(job, ct);
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
        WorkerRetryPolicy.Clear(job);
        SetPhase(job, JobPhases.Monitoring);
        if (humanReview) await MonitorJobAsync(job, ct);
    }

    private async Task ReassessLegacyBlockedResultAsync(Job job, bool repair, CancellationToken ct)
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
            RequestDashboardRefresh();
            var run = await RunContinuationAsync(job, schema, prompt, ct);
            if (run.ExitCode != 0) throw new InvalidOperationException("Codex blocked-result reassessment failed: " + ExecResultDiagnostics.Failure(run));
            reassessedJson = ExtractResult(run.Output);
            _ = WorkerResultPolicy.Parse(reassessedJson);
            if (applyingTaskUpdate) TaskUpdatePolicy.MarkDelivered(job, batch);
        }
        finally
        {
            if (applyingTaskUpdate) TaskUpdatePolicy.EndApplying(job);
            RequestDashboardRefresh();
        }
        job.PendingResultJson = reassessedJson;
        Save(job);
        await CleanIgnoredGeneratedOutputsAsync(job, ct);
        using var reassessed = JsonDocument.Parse(reassessedJson);
        await CompleteResultAsync(job, reassessed.RootElement, repair, ct);
    }

    private async Task HandleBlockedResultAsync(Job job, WorkerBlocker blocker, bool repair, CancellationToken ct)
    {
        var evidence = blocker.Evidence.Length == 0 ? string.Empty : " Evidence: " + string.Join(" | ", blocker.Evidence);
        if (blocker.IsRetryable
            && WorkerRetryPolicy.TrySchedule(job, blocker, clock.UtcNow, config.Current, out var delay))
        {
            job.BlockReason = blocker.Kind + ": " + blocker.Summary + evidence;
            job.PendingResultJson = null;
            job.WorkerRetryMode = repair ? nameof(RecoveryMode.ResumeRepair) : nameof(RecoveryMode.ResumeInitial);
            SetPhase(job, JobPhases.RetryWaiting);
            await AddCommentAsync(job,
                $"Worker retry scheduled ({FormatRetryAttempt(job, blocker.Kind)}) for {FormatRetryTime(job, delay)}: {blocker.Summary}{evidence}",
                ct);
            Save(job);
            return;
        }

        var reason = blocker.IsRetryable
            ? $"{blocker.Kind}: retry limit exhausted after {job.WorkerRetryAttempts} attempt(s) for fingerprint {job.WorkerRetryFingerprint}. {blocker.Summary}{evidence}"
            : $"{blocker.Kind}: {blocker.Summary}{evidence}";
        await BlockAsync(job, reason, ct, $"{job.Model} {job.Effort}");
    }

    private string BuildEnvelope(Job job, bool repair)
    {
        var repositoryless = job.Task.Repositories.Length == 0;
        var lifecycle = "Return exactly one structured result with status, summary, validationEvidence, repositories, commitMessage, prTitle, prBody, checkDispositions, threadDispositions, workComplete, and blocker. blocker.kind must be one of none, transientWorker, workerRepairable, upstreamDependency, missingCredential, missingHardware, missingInput, userDecision, policyRestriction, or humanReview; every non-none blocker requires a concise summary and nonempty concrete evidence array. Use workComplete=true only when the task work is complete. Use humanReview only when completed work is waiting solely for a human decision on an already-published PR. This may include subjective or visual review of artifacts already produced on that PR, but not evidence capture, validation, deployment, credential entry, device work, or another action the worker still owns. Worker-owned branch creation, commit, push, and pull-request publication are the expected handoff after a changed result and are never a blocker: when implementation is ready for that handoff, return completed with changed:true and blocker none unless a separate substantive task requirement remains."
            + " Treat usage limits, process/tool launch failures, timeouts, emulator boot failures, malformed structured output, stale dependency assertions, merge conflicts, and unavailable validation fallbacks as transientWorker or workerRepairable, not permanent gates. Before reporting another blocker, attempt the task-authorized fallback and cite the command, current dependency/task/PR state, or capability probe that proves it unavailable. completed/noChanges may use only none or humanReview; blocked must use a non-none blocker and may have workComplete=true when implementation is complete behind an external gate. Never invent an acceptance requirement absent from the task. Do not choose or report a Maddox task status: the worker owns status transitions.";
        var capabilities = "TASK-SCOPED CAPABILITY CONTRACT: The task itself authorizes actions explicitly required by its title, description, or acceptance criteria. You may use already-authenticated noninteractive CLIs/APIs and task-owned applications against only the exact accounts/resources named by the task or supplied repository. For an external change, inspect current state, record a precondition and rollback path, apply the smallest change, verify the requested outcome, and summarize non-secret evidence. Never print or persist secret values. Create/rotate/revoke credentials or perform another irreversible external action only when the task explicitly requires it; install and verify a replacement before revocation. Interactive login, unavailable required physical hardware, or an unconfigured native-UI lane may be reported as a typed external blocker after a concrete capability probe. Maddox status/database operations and Git branch/commit/push/PR publication remain worker-owned and forbidden to Codex.";
        var basePrompt = repositoryless
            ? "You are implementing one already-claimed Maddox task from the configured repository root. No repository was specified, so the impact scope is unknown: start from RepoRoot, inspect what the task requires, and make only changes required by the issue. If the objective is task management, use only the published MaddoxTasks executable and its agent JSON commands; never read or write the database directly. Do not change the source task status because the worker owns its lifecycle. Return blocked only when a substantive requirement in the task cannot be completed with the available context or environment. For a successfully completed task-management objective with no repository file changes, return noChanges. " + lifecycle + "\n\n" + capabilities
            : job.Prompt + "\n\nWORKER LIFECYCLE CONTRACT:\n" + lifecycle + "\n\n" + capabilities;
        var restrictions = repositoryless
            ? "Do not claim another task, change the source task status, edit the Maddox database directly, create branches, commit, push, create/merge PRs, or reconcile reviews."
            : "Do not claim tasks, mutate Maddox state, create branches, commit, push, create/merge PRs, or reconcile reviews. Task-explicit external actions outside Git and Maddox are allowed only under the capability contract above.";
        var repairContext = repair ? $"\nFAILING CHECKS:\n{JsonSerializer.Serialize(job.PendingCheckFailures)}\nACTIONABLE REVIEW THREADS:\n{JsonSerializer.Serialize(job.PendingFeedback)}\nReturn one checkDispositions item for every failing check ID and one threadDispositions item for every review thread ID. Mark review feedback addressed only when the requested change is complete and include the reply to post." : string.Empty;
        var executionRoot = repositoryless ? $"\nREPO ROOT:\n{config.Current.RepoRoot}" : string.Empty;
        var adoptedContext = job.AdoptedBlockedWorkspace && repair
            ? "\nRETAINED WORKSPACE:\nThis worker-owned workspace was retained from a previous blocked attempt. Existing task-branch commits already published on the retained pull request are task-owned context, but are not new repair changes. Inspect and preserve them. Report changed:true only for staged, unstaged, untracked, or committed changes created after this repair execution began."
            : job.AdoptedBlockedWorkspace
            ? "\nRETAINED WORKSPACE:\nThis worker-owned workspace was retained from a previous blocked attempt. Treat its existing staged, unstaged, untracked, and task-branch commit changes as task-owned work: inspect and preserve them, finish and validate the task, and report changed:true when they remain for publication."
            : string.Empty;
        return $"{basePrompt}\nTASK:\n{JsonSerializer.Serialize(job.Task)}\nWORKTREES:\n{JsonSerializer.Serialize(job.Workspaces)}{executionRoot}{adoptedContext}\nRESTRICTIONS:\n{restrictions}{repairContext}";
    }

    private async Task PrepareMergeabilityRepairsAsync(Job job, CancellationToken ct)
    {
        var conflicts = job.PendingCheckFailures
            .Where(check => check.Name.Equals("pull-request-mergeability", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var conflict in conflicts)
        {
            var pullRequest = job.PullRequests.FirstOrDefault(item => item.Url.Equals(conflict.PullRequestUrl, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Pull request mergeability repair does not match a retained pull request.");
            var preparedConflict = conflict;
            if (string.IsNullOrWhiteSpace(preparedConflict.BaseRefName))
            {
                var refreshed = await github.InspectAsync(pullRequest.Url, includeFeedback: false, ct);
                preparedConflict = preparedConflict with { BaseRefName = refreshed.BaseRefName };
            }
            if (!MergeBasePolicy.IsValid(preparedConflict.BaseRefName))
                throw new InvalidDataException($"Pull request mergeability repair has an invalid base ref: {preparedConflict.BaseRefName}");
            var workspace = job.Workspaces.FirstOrDefault(item => item.Repository.Equals(pullRequest.Repository, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Pull request mergeability repair has no workspace for {pullRequest.Repository}.");

            await RequireAsync("git", ["fetch", "origin"], workspace.Directory, ct);
            var remoteBase = $"origin/{preparedConflict.BaseRefName}";
            var preparation = $"Worker fetched and prepared the exact pull request base {remoteBase} before repair.";
            var mergeHead = await processes.RunAsync("git", ["rev-parse", "--verify", "-q", "MERGE_HEAD"], workspace.Directory, ct);
            var startMerge = mergeHead.ExitCode != 0;
            if (mergeHead.ExitCode == 0)
            {
                var expectedBase = await RequireAsync("git", ["rev-parse", remoteBase], workspace.Directory, ct);
                if (!mergeHead.Output.Trim().Equals(expectedBase.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    await RequireAsync("git", ["merge", "--abort"], workspace.Directory, ct);
                    startMerge = true;
                    preparation += $" The retained merge targeted stale base {mergeHead.Output.Trim()}, so the worker aborted that merge and prepared the current exact base {expectedBase.Output.Trim()}.";
                }
                else
                {
                    var unresolved = await processes.RunAsync("git", ["diff", "--name-only", "--diff-filter=U"], workspace.Directory, ct);
                    if (unresolved.ExitCode != 0)
                        throw new InvalidOperationException("Could not inspect retained merge conflicts: " + unresolved.Error.Trim());
                    preparation += string.IsNullOrWhiteSpace(unresolved.Output)
                        ? " The retained merge already targets that exact base and all conflicts are resolved and staged; validate and publish it without restarting the merge."
                        : " The retained merge already targets that exact base. Unresolved merge paths are present for Codex to resolve: "
                            + string.Join(", ", ChangedPaths(unresolved.Output)) + ".";
                }
            }
            if (startMerge)
            {
                var merge = await processes.RunAsync("git", ["merge", "--no-commit", "--no-ff", "--", remoteBase], workspace.Directory, ct);
                if (merge.ExitCode != 0)
                {
                    var unresolved = await processes.RunAsync("git", ["diff", "--name-only", "--diff-filter=U"], workspace.Directory, ct);
                    if (unresolved.ExitCode != 0 || string.IsNullOrWhiteSpace(unresolved.Output))
                        throw new InvalidOperationException("git merge failed without leaving unresolved paths: " + merge.Error.Trim());
                    preparation += " Unresolved merge paths are present for Codex to resolve: "
                        + string.Join(", ", ChangedPaths(unresolved.Output)) + ".";
                }
            }
            var index = job.PendingCheckFailures.FindIndex(check => check.Id == conflict.Id);
            if (index >= 0) job.PendingCheckFailures[index] = preparedConflict with { Details = preparedConflict.Details + "\n" + preparation };
            Save(job);
        }
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
                RequestDashboardRefresh();
                var run = await RunContinuationAsync(job, schema, prompt, ct);
                if (run.ExitCode != 0) throw new InvalidOperationException("Codex task-update continuation failed: " + ExecResultDiagnostics.Failure(run));
                resultJson = ExtractResult(run.Output);
                _ = WorkerResultPolicy.Parse(resultJson);
                lock (journalGate)
                {
                    TaskUpdatePolicy.MarkDelivered(job, batch);
                    journal.Save(journalPath);
                }
            }
            finally
            {
                TaskUpdatePolicy.EndApplying(job);
                RequestDashboardRefresh();
            }
        }
    }

    private Task<ExecResult> RunContinuationAsync(Job job, string schema, string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ThreadId)) throw new InvalidOperationException("Cannot deliver a task update without the existing Codex thread ID.");
        return RunCodexAsync(job, BuildContinuationCodexArguments(job, schema, prompt), ct);
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
        if (run.ExitCode != 0) throw new InvalidOperationException("Repository clarification failed: " + ExecResultDiagnostics.Failure(run));
        var decision = ClarificationPolicy.Parse(ExtractResult(run.Output));
        var proposedRepositories = (decision.Action == "split"
            ? decision.Children.Select(child => child.Repository).ToArray()
            : decision.Repositories).Select(repository => RepositoryPathPolicy.Normalize(config.Current.RepoRoot, repository)).ToArray();
        foreach (var repository in proposedRepositories) await ValidateRepositoryAsync(repository, ct);

        if (decision.Action == "split")
        {
            var children = decision.Children.Zip(proposedRepositories, (child, repository) => new { child.Title, child.Description, Repository = repository }).ToArray();
            var response = await RunCommandAsync(new { type = "SplitIssue", issueId = job.Task.IssueId, children }, null, ct);
            if (response.ExitCode != 0 || !TryReadSuccess(response.Output)) throw new InvalidOperationException("Task split failed: " + response.Output.Trim() + response.Error.Trim());
            SetPhase(job, JobPhases.Done);
            return false;
        }

        var assignment = await RunCommandAsync(new { type = "SetRepositoryLabels", issueId = job.Task.IssueId, repositories = proposedRepositories }, null, ct);
        if (assignment.ExitCode != 0 || !TryReadSuccess(assignment.Output)) throw new InvalidOperationException("Repository assignment failed: " + assignment.Output.Trim() + assignment.Error.Trim());
        job.Task = job.Task with { Repositories = proposedRepositories };
        Save(job);
        return true;
    }

    private async Task ValidateRepositoryAsync(string repository, CancellationToken ct)
    {
        await new RepositoryBootstrap(processes, config.Current.GhExe, config.Current.PrivateRepositoryOwner).EnsureAsync(config.Current.RepoRoot, repository, ct);
    }

    public static List<string> BuildInitialCodexArguments(Job job, string schema, string envelope, string? fallbackDirectory = null)
    {
        var workingDirectory = job.Workspaces.FirstOrDefault()?.Directory ?? fallbackDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidDataException("A repository-less task requires a fallback working directory.");
        var arguments = new List<string> { "exec", "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", "--approve-for-me", "--skip-git-repo-check", "-C", workingDirectory };
        foreach (var workspace in job.Workspaces.Skip(1)) { arguments.Add("--add-dir"); arguments.Add(workspace.Directory); }
        arguments.Add(envelope);
        return arguments;
    }

    public static List<string> BuildContinuationCodexArguments(Job job, string schema, string prompt)
    {
        if (string.IsNullOrWhiteSpace(job.ThreadId)) throw new InvalidOperationException("Cannot resume without an existing Codex thread ID.");
        return ["exec", "resume", job.ThreadId, "--json", "--output-schema", schema, "-m", job.Model,
            "-c", $"model_reasoning_effort={job.Effort}", "--skip-git-repo-check", prompt];
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
        await RequireAsync("git", ["fetch", "origin"], source, ct);
        var head = (await processes.RunAsync("git", ["symbolic-ref", "refs/remotes/origin/HEAD", "--short"], source, ct)).Output.Trim();
        if (string.IsNullOrWhiteSpace(head)) head = "origin/main";
        var localBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? priorRemoteBranch = null;
        WorkspaceBranchCandidate candidate;
        while (true)
        {
            candidate = WorkspaceBranchPolicy.SelectAvailable(branch, directory, localBranches, remoteBranches, directories);
            if (Directory.Exists(candidate.Directory))
            {
                directories.Add(candidate.Directory);
            }
            var local = await processes.RunAsync("git", ["show-ref", "--verify", "--quiet", $"refs/heads/{candidate.Branch}"], source, ct);
            if (local.ExitCode == 0) localBranches.Add(candidate.Branch);
            else if (local.ExitCode != 1) throw new InvalidOperationException("Could not inspect local task branches: " + local.Error.Trim());
            var remoteProbe = await processes.RunAsync("git", ["ls-remote", "--exit-code", "--heads", "origin", candidate.Branch], source, ct);
            if (remoteProbe.ExitCode == 0)
            {
                remoteBranches.Add(candidate.Branch);
                priorRemoteBranch = candidate.Branch;
            }
            else if (remoteProbe.ExitCode != 2) throw new InvalidOperationException("Could not verify remote branch ownership: " + remoteProbe.Error.Trim());
            if (!localBranches.Contains(candidate.Branch)
                && !remoteBranches.Contains(candidate.Branch)
                && !directories.Contains(candidate.Directory)) break;
        }

        var startingRef = head;
        if (priorRemoteBranch is not null)
        {
            var priorPullRequests = await RequireAsync(config.Current.GhExe, ["pr", "list", "--head", priorRemoteBranch, "--state", "all", "--limit", "1", "--json", "mergedAt,baseRefName"], source, ct);
            startingRef = WorkspaceBranchPolicy.SelectStartingRef(priorPullRequests.Output, head, $"origin/{priorRemoteBranch}");
        }
        await RequireAsync("git", ["worktree", "add", "-b", candidate.Branch, candidate.Directory, startingRef], source, ct);
        var remote = (await RequireAsync("git", ["remote", "get-url", "origin"], source, ct)).Output.Trim();
        return new Workspace(repository, candidate.Directory, candidate.Branch, remote, startingRef);
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
                    await CommitAsync(workspace, PublicationMetadata.CommitMessage(result, job.Task.Sequence), ct);
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
                var created = await processes.RunAsync(config.Current.GhExe, ["pr", "create", "--head", workspace.Branch, "--title", PublicationMetadata.PullRequestTitle(result, job.Task.Title), "--body", PublicationMetadata.PullRequestBody(result)], workspace.Directory, ct);
                if (created.ExitCode == 0)
                    existing = created.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
                else
                {
                    existing = await FindPullRequestAsync(workspace, ct);
                    if (existing is null) throw new InvalidOperationException($"{Path.GetFileName(config.Current.GhExe)} failed: {created.Error.Trim()}");
                }
                if (string.IsNullOrWhiteSpace(existing)) throw new InvalidDataException("GitHub did not return a pull request URL for the published branch.");
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
        var reported = ValidateResultManifest(job, result);
        foreach (var workspace in job.Workspaces)
        {
            var hasChanges = await HasExecutionChangesAsync(job, workspace, ct);
            if (reported[workspace.Repository] != hasChanges)
                throw new InvalidDataException($"Codex result change flag does not match repository state for {workspace.Repository}.");
        }
    }

    private static Dictionary<string, bool> ValidateResultManifest(Job job, JsonElement result)
    {
        var reported = ResultRepositories(result);
        if (reported.Count != job.Workspaces.Count || job.Workspaces.Any(workspace => !reported.ContainsKey(workspace.Repository)))
            throw new InvalidDataException("Codex result repository manifest does not match assigned workspaces.");
        return reported;
    }

    private static Dictionary<string, bool> ResultRepositories(JsonElement result) => result.GetProperty("repositories").EnumerateArray().ToDictionary(
        item => item.GetProperty("repository").GetString() ?? string.Empty,
        item => item.GetProperty("changed").GetBoolean(),
        StringComparer.OrdinalIgnoreCase);

    private async Task<bool> ShouldReassessAdoptedWorkspaceResultAsync(Job job, JsonElement result, CancellationToken ct)
    {
        Dictionary<string, bool> reported;
        try { reported = ResultRepositories(result); }
        catch { return false; }
        if (reported.Count != job.Workspaces.Count || job.Workspaces.Any(workspace => !reported.ContainsKey(workspace.Repository))) return false;
        foreach (var workspace in job.Workspaces)
        {
            var repositoryChanged = await HasExecutionChangesAsync(job, workspace, ct);
            if (AdoptedWorkspaceResultPolicy.ShouldReassess(job.AdoptedBlockedWorkspace, job.AdoptedResultReassessmentAttempted, reported[workspace.Repository], repositoryChanged)) return true;
        }
        return false;
    }

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
        var found = await RequireAsync(config.Current.GhExe, ["pr", "list", "--head", workspace.Branch, "--state", "open", "--limit", "1", "--json", "url"], workspace.Directory, ct);
        using var document = JsonDocument.Parse(found.Output);
        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    private async Task CommitAsync(Workspace workspace, string message, CancellationToken ct)
    {
        var environment = WorkspaceProcessEnvironment.IsolatedBuild();
        var indexBefore = (await RequireAsync("git", ["write-tree"], workspace.Directory, ct)).Output.Trim();
        var commit = await processes.RunAsync("git", ["commit", "-m", message], workspace.Directory, ct, environment: environment);
        if (commit.ExitCode == 0) return;

        var indexAfterResult = await processes.RunAsync("git", ["write-tree"], workspace.Directory, ct);
        var unstaged = await processes.RunAsync("git", ["diff", "--quiet"], workspace.Directory, ct);
        var indexAfter = indexAfterResult.Output.Trim();
        var unstagedPaths = await processes.RunAsync("git", ["diff", "--name-only"], workspace.Directory, ct);
        var untrackedPaths = await processes.RunAsync("git", ["ls-files", "--others", "--exclude-standard"], workspace.Directory, ct);
        var onlyStasisChanges = unstagedPaths.ExitCode == 0 && untrackedPaths.ExitCode == 0
            && ChangedPaths(unstagedPaths.Output, untrackedPaths.Output).All(path => path.EndsWith(".stasis", StringComparison.OrdinalIgnoreCase));
        if (indexAfterResult.ExitCode == 0 && unstaged.ExitCode is 0 or 1
            && CommitHookRecoveryPolicy.CanRestageAndRetry(commit, indexBefore, indexAfter, unstaged.ExitCode == 1, onlyStasisChanges))
        {
            log.Write("warning", "git.commit.format-restage", new { workspace.Repository, workspace.Directory });
            await RequireAsync("git", ["add", "-A", "--", ":(glob)**/*.stasis"], workspace.Directory, ct);
            var formattedIndex = (await RequireAsync("git", ["write-tree"], workspace.Directory, ct)).Output.Trim();
            if (formattedIndex.Equals(indexBefore, StringComparison.Ordinal))
                throw new InvalidOperationException("The Stasis formatter reported changes but restaging did not update the commit index.");
            await RequireAsync("git", ["commit", "-m", message], workspace.Directory, ct, environment);
            return;
        }
        if (indexAfterResult.ExitCode != 0 || unstaged.ExitCode is not (0 or 1)
            || !CommitHookRecoveryPolicy.CanRestoreAndBypass(commit, indexBefore, indexAfter, unstaged.ExitCode == 1))
            throw new InvalidOperationException($"git failed: {commit.Error.Trim()}");

        log.Write("warning", "git.commit.hook-side-effects", new { workspace.Repository, workspace.Directory });
        await RequireAsync("git", ["restore", "--worktree", "--", "."], workspace.Directory, ct);
        var untracked = await RequireAsync("git", ["ls-files", "--others", "--exclude-standard"], workspace.Directory, ct);
        if (!string.IsNullOrWhiteSpace(untracked.Output)) await RequireAsync("git", ["clean", "-fd"], workspace.Directory, ct);
        var restored = await processes.RunAsync("git", ["diff", "--quiet"], workspace.Directory, ct);
        var restoredIndex = (await RequireAsync("git", ["write-tree"], workspace.Directory, ct)).Output.Trim();
        if (restored.ExitCode != 0 || !restoredIndex.Equals(indexBefore, StringComparison.Ordinal))
            throw new InvalidOperationException("Could not safely restore side effects from the failed pre-commit hook.");
        await RequireAsync("git", ["commit", "--no-verify", "-m", message], workspace.Directory, ct, environment);
    }

    private static IEnumerable<string> ChangedPaths(params string[] outputs) => outputs
        .SelectMany(output => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
        RequestDashboardRefresh();
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
        var allGreen = job.PullRequests.Count > 0;
        var allReviewReady = job.PullRequests.Count > 0;
        var newFeedback = false;
        var snapshots = new List<PullRequestSnapshot>();
        for (var pullRequestIndex = 0; pullRequestIndex < job.PullRequests.Count; pullRequestIndex++)
        {
            var pullRequest = job.PullRequests[pullRequestIndex];
            var snapshot = await github.InspectAsync(pullRequest.Url, includeFeedback: true, ct);
            snapshots.Add(snapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.HeadOid) && !snapshot.HeadOid.Equals(pullRequest.HeadOid, StringComparison.OrdinalIgnoreCase))
            {
                pullRequest = pullRequest with { HeadOid = snapshot.HeadOid };
                job.PullRequests[pullRequestIndex] = pullRequest;
            }
            if (snapshot.Merged) continue;
            if (snapshot.InspectionComplete && !snapshot.IsOpen)
            {
                await BlockAsync(job, $"userDecision: Pull request {pullRequest.Url} was closed without merge. Reopen it or explicitly authorize a replacement before review can continue.", ct);
                return;
            }

            var codeFailures = snapshot.CodeRepairs(config.Current.IgnoredChecks);
            var transientFailures = snapshot.TransientFailures(config.Current.IgnoredChecks);
            var humanGates = snapshot.HumanGates(config.Current.IgnoredChecks);
            allGreen &= snapshot.IsGreen(config.Current.IgnoredChecks);
            allReviewReady &= snapshot.IsReviewReady(config.Current.IgnoredChecks);
            foreach (var failure in codeFailures.Where(failure => job.ProcessedCheckIds.Add(failure.Id)))
                job.PendingCheckFailures.Add(failure with { PullRequestUrl = pullRequest.Url });
            foreach (var transient in transientFailures)
            {
                if (job.TransientCheckRerunUtc.TryGetValue(transient.Id, out var lastRerun)
                    && clock.UtcNow - lastRerun < TimeSpan.FromMinutes(5)) continue;
                await github.RerunAsync(transient.Link, ct);
                job.TransientCheckRerunUtc[transient.Id] = clock.UtcNow;
                await AddCommentAsync(job, $"Worker reran transient check {transient.Name} after state {transient.State}: {transient.Link}", ct);
            }
            foreach (var humanGate in humanGates)
            {
                if (job.ProcessedCheckIds.Contains(humanGate.Id)) continue;
                await AddCommentAsync(job, $"Ready for review; GitHub check {humanGate.Name} is waiting for human approval: {humanGate.Link}", ct);
                job.ProcessedCheckIds.Add(humanGate.Id);
            }
            if (snapshot.HasMergeConflict)
            {
                var conflict = new CheckState(
                    "pull-request-mergeability",
                    "CONFLICTING",
                    "fail",
                    $"{pullRequest.Url}#head-{snapshot.HeadOid}",
                    pullRequest.Url,
                    $"GitHub reports mergeable={snapshot.Mergeable}, mergeStateStatus={snapshot.MergeStateStatus}. Merge the latest base branch into the task branch, resolve conflicts without discarding task work, rerun relevant validation, and push the repaired branch.",
                    snapshot.BaseRefName);
                job.ProcessedCheckIds.Add(conflict.Id);
                if (!job.PendingCheckFailures.Any(failure => failure.Id.Equals(conflict.Id, StringComparison.Ordinal)))
                    job.PendingCheckFailures.Add(conflict);
            }
            var additions = FeedbackPolicy.AddNew(job, snapshot.Feedback);
            newFeedback |= additions.Count > 0;
        }

        if (job.PullRequests.Count > 0 && snapshots.All(snapshot => snapshot.Merged))
        {
            await ReconcileAsync(ct);
            await ChangeStatusAsync(job, "Done", ct);
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

        var openPullRequests = snapshots.Any(snapshot => !snapshot.Merged);
        var quietPeriodElapsed = job.ReviewWindow.Update(allGreen, newFeedback, clock.UtcNow, config.Current.ReviewQuietPeriod);
        var clearToReview = openPullRequests && allReviewReady && job.PendingFeedback.Count == 0 && job.PendingCheckFailures.Count == 0;
        if (clearToReview)
        {
            if (!job.ReadyForReviewRecorded)
            {
                await ChangeStatusAsync(job, "ReadyForReview", ct);
                job.ReadyForReviewRecorded = true;
                Save(job);
            }
            if (IsAutoMergeAllowed(job) && allGreen && quietPeriodElapsed)
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
                if (config.TryReload(configPath, out var error)) { configError = null; log.Write("info", "config.reloaded"); promptPath = ResolvePromptPath(config.Current); SignalScheduler(SchedulerWakeReason.ConfigurationChanged); RequestDashboardRefresh(); }
                else { configError = error; log.Write("error", "config.reload.rejected", new { error }); RequestDashboardRefresh(); }
            }
            var newPromptStamp = File.Exists(promptPath) ? File.GetLastWriteTimeUtc(promptPath) : DateTime.MinValue;
            if (newPromptStamp != promptStamp)
            {
                promptStamp = newPromptStamp;
                if (ClaimAdmission.TrySnapshot(config.Current, configPath, out _, out var promptError)) { configError = null; log.Write("info", "prompt.changed", new { promptPath }); }
                else { configError = "Cannot claim: " + promptError; log.Write("error", "prompt.reload.rejected", new { error = promptError }); }
                RequestDashboardRefresh();
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
                case ConsoleKey.P: paused = !paused; log.Write("info", paused ? "claims.paused" : "claims.resumed"); if (!paused) SignalScheduler(SchedulerWakeReason.ConfigurationChanged); RequestDashboardRefresh(); break;
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
        }, new TerminalOutputDirective(terminal.Observe, TimeSpan.FromSeconds(2)), standardInput: input.StandardInput,
            environment: WorkspaceProcessEnvironment.IsolatedBuild());
    }

    private async Task BlockAsync(Job job, string reason, CancellationToken ct, string actor = "maddox-worker")
    {
        job.BlockReason = reason;
        job.BlockCommentRecorded = false;
        job.BlockActor = actor;
        SetPhase(job, JobPhases.StatusSyncPending);
        await SyncBlockedAsync(job, ct);
    }
    private async Task SyncBlockedAsync(Job job, CancellationToken ct)
    {
        if (job.Phase != JobPhases.StatusSyncPending) return;
        if (!job.BlockCommentRecorded)
        {
            await AddCommentAsync(job, "Worker blocked: " + job.BlockReason, job.BlockActor ?? "maddox-worker", ct);
            job.BlockCommentRecorded = true;
            Save(job);
        }
        await ChangeStatusAsync(job, "Blocked", ct);
        WorkerRetryPolicy.Clear(job);
        SetPhase(job, JobPhases.Blocked);
    }
    private Task<ExecResult> AddCommentAsync(Job job, string comment, CancellationToken ct) => AddCommentAsync(job, comment, "maddox-worker", ct);
    private Task<ExecResult> AddCommentAsync(Job job, string comment, string actor, CancellationToken ct) => RunRequiredCommandAsync(new { type = "AddComment", issueId = job.Task.IssueId, comment }, actor, ct);
    private async Task<ExecResult> ChangeStatusAsync(Job job, string newStatus, CancellationToken ct)
    {
        var result = await RunCommandAsync(new { type = "ChangeStatus", issueId = job.Task.IssueId, newStatus }, null, ct);
        if (result.ExitCode == 0 && (TryReadSuccess(result.Output) || AlreadyHasStatus(result.Output, newStatus))) return result;
        throw new InvalidOperationException("Maddox command failed: " + result.Output.Trim() + result.Error.Trim());
    }
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
        var ledger = await processes.RunAsync(settings.MaddoxExe, ["agent", "issues"], Path.GetDirectoryName(configPath)!, ct);
        if (ledger.ExitCode != 0)
            throw new InvalidOperationException("Startup preflight failed: MaddoxTasks ledger access is required before work can be claimed.");

        await Task.WhenAll(
            ProbeOptionalAsync(settings.CodexExe, ["--version"], settings.RepoRoot, "preflight.codex.unavailable", ct),
            ProbeOptionalAsync(settings.GhExe, ["auth", "status"], settings.RepoRoot, "preflight.github.unavailable", ct));
        log.Write("info", "preflight.succeeded");
    }
    private async Task ProbeOptionalAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string eventName, CancellationToken ct)
    {
        try
        {
            var result = await processes.RunAsync(fileName, arguments, workingDirectory, ct);
            if (result.ExitCode != 0) log.Write("warning", eventName, new { error = ExecResultDiagnostics.Failure(result) });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { log.Write("warning", eventName, new { error = exception.Message }); }
    }
    private static bool AlreadyHasStatus(string output, string status)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && message.GetString()!.Contains($"already has status '{status}'.", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }
    private async Task<ExecResult> RequireAsync(string executable, IEnumerable<string> arguments, string cwd, CancellationToken ct, IReadOnlyDictionary<string, string?>? environment = null) { var result = await processes.RunAsync(executable, arguments, cwd, ct, environment: environment); if (result.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {result.Error.Trim()}"); return result; }
    private static bool TryReadSuccess(string output) { try { using var document = JsonDocument.Parse(output); return document.RootElement.GetProperty("success").GetBoolean(); } catch { return false; } }

    private void SetPhase(Job job, string phase)
    {
        if (job.Phase != phase) { job.Phase = phase; job.PhaseChangedUtc = clock.UtcNow; }
        log.Write("info", "job.phase", new { job.Task.Sequence, phase });
        Save(job);
    }
    private void Save(Job job) { lock (journalGate) journal.Save(journalPath); RequestDashboardRefresh(); }
    private Job[] SnapshotJobs(string phase) { lock (journalGate) return journal.Jobs.Where(job => job.Phase == phase).ToArray(); }
    private Job[] SnapshotJobs(Func<Job, bool> predicate) { lock (journalGate) return journal.Jobs.Where(predicate).ToArray(); }
    private Job[] SnapshotCleanupPending() { lock (journalGate) return WorkspaceCleanupPolicy.Pending(journal.Jobs).ToArray(); }
    private void RequestDashboardRefresh()
    {
        if (!Console.IsOutputRedirected) dashboardRefresh.Request();
    }

    private async Task TryRenderDashboardAsync()
    {
        try { await RenderDashboardAsync(); }
        catch (Exception exception) { log.Write("warning", "dashboard.refresh.failed", new { error = exception.Message }); }
    }

    private async Task RenderDashboardAsync()
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
            Job[] jobs;
            lock (journalGate) jobs = journal.Jobs.ToArray();
            foreach (var job in DashboardPolicy.VisibleJobs(jobs, clock.UtcNow, config.Current.EffectiveBlockedDisplayDuration))
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
                if (root.TryGetProperty("result", out var result) && TryKnownResult(result, out var direct)) return direct;
                if (TryKnownResult(root, out var rootResult)) return rootResult;
                if (root.TryGetProperty("type", out var eventType) && eventType.GetString() == "event_msg"
                    && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "task_complete"
                    && payload.TryGetProperty("last_agent_message", out var lastMessage) && lastMessage.ValueKind == JsonValueKind.String)
                {
                    var candidate = lastMessage.GetString()!;
                    if (TryKnownResult(lastMessage, out var structured)) return structured;
                }
                if (root.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    var candidate = text.GetString()!;
                    if (TryKnownResult(text, out var structured)) return structured;
                }
            }
            catch (JsonException) { }
        }
        throw new InvalidDataException("Codex emitted no structured result.");
    }
    private static bool TryKnownResult(JsonElement candidate, out string json)
    {
        json = string.Empty;
        if (candidate.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(candidate.GetString() ?? string.Empty);
                return TryKnownResult(parsed.RootElement, out json);
            }
            catch (JsonException) { return false; }
        }
        if (candidate.ValueKind != JsonValueKind.Object) return false;
        var worker = candidate.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("summary", out var workerSummary) && workerSummary.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("workComplete", out var workComplete) && workComplete.ValueKind is JsonValueKind.True or JsonValueKind.False
            && candidate.TryGetProperty("blocker", out var blocker) && blocker.ValueKind == JsonValueKind.Object
            && candidate.TryGetProperty("repositories", out var repositories) && repositories.ValueKind == JsonValueKind.Array;
        var research = candidate.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("summary", out var researchSummary) && researchSummary.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array
            && candidate.TryGetProperty("mutations", out var mutations) && mutations.ValueKind == JsonValueKind.Array;
        var clarification = candidate.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("repositories", out var clarificationRepositories) && clarificationRepositories.ValueKind == JsonValueKind.Array
            && candidate.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            && candidate.TryGetProperty("rationale", out var rationale) && rationale.ValueKind == JsonValueKind.String
            && candidate.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number
            && candidate.TryGetProperty("ambiguous", out var ambiguous) && ambiguous.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (!worker && !research && !clarification) return false;
        json = candidate.GetRawText();
        return true;
    }
    private static string WriteSchema(string name, string body) { var path = Path.Combine(Path.GetTempPath(), $"maddox-{name}-schema.json"); File.WriteAllText(path, body); return path; }
    private sealed record WorkItem(Job Job, RecoveryMode Mode);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string ResearchFailureMarkerPrefix = "Research worker could not complete: ";
    private const string ClarifySchema = """{"type":"object","properties":{"action":{"enum":["assign","split"]},"repositories":{"type":"array","items":{"type":"string"}},"children":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"description":{"type":"string"},"repository":{"type":"string"},"rationale":{"type":"string"}},"required":["title","description","repository","rationale"],"additionalProperties":false}},"rationale":{"type":"string"},"confidence":{"type":"number","minimum":0,"maximum":1},"ambiguous":{"type":"boolean"}},"required":["action","repositories","children","rationale","confidence","ambiguous"],"additionalProperties":false}""";
    private const string ResultSchema = """{"type":"object","properties":{"status":{"enum":["completed","noChanges","blocked"]},"summary":{"type":"string"},"validationEvidence":{"type":"array","items":{"type":"string"}},"repositories":{"type":"array","items":{"type":"object","properties":{"repository":{"type":"string"},"changed":{"type":"boolean"}},"required":["repository","changed"],"additionalProperties":false}},"commitMessage":{"type":"string"},"prTitle":{"type":"string"},"prBody":{"type":"string"},"checkDispositions":{"type":"array","items":{"type":"object","properties":{"checkId":{"type":"string"},"addressed":{"type":"boolean"},"summary":{"type":"string"}},"required":["checkId","addressed","summary"],"additionalProperties":false}},"threadDispositions":{"type":"array","items":{"type":"object","properties":{"threadId":{"type":"string"},"addressed":{"type":"boolean"},"replyBody":{"type":"string"}},"required":["threadId","addressed","replyBody"],"additionalProperties":false}},"workComplete":{"type":"boolean"},"blocker":{"type":"object","properties":{"kind":{"enum":["none","transientWorker","workerRepairable","upstreamDependency","missingCredential","missingHardware","missingInput","userDecision","policyRestriction","humanReview"]},"summary":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}},"retryAtUtc":{"type":["string","null"]}},"required":["kind","summary","evidence","retryAtUtc"],"additionalProperties":false}},"required":["status","summary","validationEvidence","repositories","commitMessage","prTitle","prBody","checkDispositions","threadDispositions","workComplete","blocker"],"additionalProperties":false}""";
    private const string ResearchResultSchema = """{"type":"object","properties":{"outcome":{"enum":["completed","unblocked","stillBlocked"]},"summary":{"type":"string"},"findings":{"type":"array","items":{"type":"string"}},"mutations":{"type":"array","items":{"type":"object","properties":{"type":{"enum":["AddComment","UpdateDescription","ChangePriority","AddLabel","RemoveLabel","SetRepositoryLabels","ChangeStatus","CreateIssue"]},"issueId":{"type":["string","null"]},"comment":{"type":["string","null"]},"description":{"type":["string","null"]},"newPriority":{"type":["integer","null"],"minimum":1,"maximum":5},"label":{"type":["string","null"]},"newStatus":{"enum":["Backlog","Next","Active","Blocked","ReadyForReview","Done","Rejected",null]},"repositories":{"type":["array","null"],"items":{"type":"string"}},"title":{"type":["string","null"]},"priority":{"type":["integer","null"],"minimum":1,"maximum":5},"status":{"enum":["Next","Backlog",null]},"parentId":{"type":["string","null"]}},"required":["type","issueId","comment","description","newPriority","label","newStatus","repositories","title","priority","status","parentId"],"additionalProperties":false}}},"required":["outcome","summary","findings","mutations"],"additionalProperties":false}""";
}
