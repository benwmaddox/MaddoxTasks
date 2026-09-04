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
    private readonly SemaphoreSlim wakeScheduler = new(0, 1);
    private readonly SemaphoreSlim renderLock = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrencyGate capacity;
    private volatile bool paused;
    private volatile string? configError;
    private DateTime nextTickUtc;

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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await TickAsync(ct);
                nextTickUtc = clock.UtcNow + config.Current.ClaimInterval;
                await RenderAsync();
                await WaitForTickAsync(config.Current.ClaimInterval, ct);
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

    internal async Task TickAsync(CancellationToken ct)
    {
        log.Write("info", "scheduler.tick", new { active = capacity.Active, queued = followups.Count, paused });
        await ReconcileAsync(ct);
        DrainFollowups(ct);
        if (paused) return;
        var freshClaim = new FreshClaimAllowance();
        if (freshClaim.TryReserve(capacity))
        {
            if (!ClaimAdmission.TrySnapshot(config.Current, configPath, out var claimSnapshot, out var promptError))
            {
                configError = "Cannot claim: " + promptError;
                log.Write("error", "claim.prompt.rejected", new { error = promptError });
                capacity.Release();
                await RenderAsync();
                return;
            }
            ExecResult claim;
            try { claim = await RunMaddoxAsync("claim", ct); }
            catch { capacity.Release(); throw; }
            if (claim.ExitCode != 0 || string.IsNullOrWhiteSpace(claim.Output) || claim.Output.Trim() == "null") { capacity.Release(); return; }
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
        }
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
        finally { capacity.Release(); SignalScheduler(); await RenderAsync(); }
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
        if (job.Task.Repositories.Length == 0) await ClarifyAsync(job, ct);
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
        var envelope = BuildEnvelope(job, repair);
        var arguments = resume && job.ThreadId is not null
            ? new List<string> { "exec", "resume", job.ThreadId, "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", envelope }
            : BuildInitialCodexArguments(job, schema, envelope);
        var run = await RunCodexAsync(job, arguments, ct);
        if (run.ExitCode != 0) throw new InvalidOperationException("Codex failed: " + run.Error.Trim());
        if (!job.ExactReservationOwnerRecorded && job.ThreadId is not null)
        {
            await AddCommentAsync(job, ReservationAttribution.Exact(job.ThreadId), ct);
            job.ExactReservationOwnerRecorded = true;
            Save(job);
        }
        var resultJson = ExtractResult(run.Output);
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

    private async Task ClarifyAsync(Job job, CancellationToken ct)
    {
        SetPhase(job, JobPhases.Clarifying);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.Current.ClarificationTimeout);
        var schema = WriteSchema("clarify", ClarifySchema);
        var prompt = $"Read-only investigation. Given this exact task JSON, identify the smallest unambiguous repository directory set beneath {config.Current.RepoRoot}. Do not edit anything.\n{JsonSerializer.Serialize(job.Task)}";
        ExecResult run;
        try { run = await RunCodexAsync(job, ["exec", "--json", "--output-schema", schema, "-m", job.Model, "-c", $"model_reasoning_effort={job.Effort}", "--sandbox", "read-only", "--skip-git-repo-check", "-C", config.Current.RepoRoot, prompt], timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Repository clarification timed out."); }
        if (run.ExitCode != 0) throw new InvalidOperationException("Repository clarification failed: " + run.Error.Trim());
        using var document = JsonDocument.Parse(ExtractResult(run.Output));
        var root = document.RootElement;
        var repositories = root.GetProperty("repositories").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (root.GetProperty("ambiguous").GetBoolean() || repositories.Length == 0) throw new InvalidOperationException("Codex could not determine an unambiguous repository: " + root.GetProperty("rationale").GetString());
        foreach (var repository in repositories) await ValidateRepositoryAsync(repository, ct);
        var response = await RunCommandAsync(new { type = "SetRepositoryLabels", issueId = job.Task.IssueId, repositories }, null, ct);
        if (response.ExitCode != 0 || !TryReadSuccess(response.Output)) throw new InvalidOperationException("Repository assignment failed: " + response.Output.Trim() + response.Error.Trim());
        job.Task = job.Task with { Repositories = repositories };
        Save(job);
    }

    private async Task ValidateRepositoryAsync(string repository, CancellationToken ct)
    {
        var root = Path.GetFullPath(config.Current.RepoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, repository));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path)) throw new InvalidOperationException("Invalid proposed repository: " + repository);
        var inside = await processes.RunAsync("git", ["rev-parse", "--is-inside-work-tree"], path, ct);
        var remote = await processes.RunAsync("git", ["remote", "get-url", "origin"], path, ct);
        if (inside.ExitCode != 0 || inside.Output.Trim() != "true" || remote.ExitCode != 0 || string.IsNullOrWhiteSpace(remote.Output)) throw new InvalidOperationException("Repository has no usable Git origin: " + repository);
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
        var source = Path.GetFullPath(Path.Combine(config.Current.RepoRoot, repository));
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

    private async Task MonitorJobAsync(Job job, CancellationToken ct)
    {
        var allGreen = true;
        var newFeedback = false;
        var snapshots = new List<PullRequestSnapshot>();
        foreach (var pullRequest in job.PullRequests)
        {
            var snapshot = await github.InspectAsync(pullRequest.Url, !job.ReviewWindow.Closed, ct);
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
        if (!allGreen) job.ReviewWindow.Update(false, false, clock.UtcNow, config.Current.ReviewQuietPeriod);
        else if (job.ReviewWindow.Update(true, newFeedback, clock.UtcNow, config.Current.ReviewQuietPeriod)
                 && job.PendingFeedback.Count == 0 && job.PendingCheckFailures.Count == 0)
        {
            if (!job.ReadyForReviewRecorded)
            {
                await ChangeStatusAsync(job, "ReadyForReview", ct);
                job.ReadyForReviewRecorded = true;
                Save(job);
            }
            if (IsAutoMergeAllowed(job))
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

    private void Enqueue(Job job, RecoveryMode mode)
    {
        lock (queueGate)
        {
            if (!queued.Add(job.Task.IssueId)) return;
            followups.Enqueue(new WorkItem(job, mode));
        }
        log.Write("info", "job.queued", new { job.Task.Sequence, mode });
        SignalScheduler();
    }
    private void SignalScheduler() { try { wakeScheduler.Release(); } catch (SemaphoreFullException) { } }
    private async Task WaitForTickAsync(TimeSpan delay, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = clock.Delay(delay, wait.Token);
        var signal = wakeScheduler.WaitAsync(wait.Token);
        await Task.WhenAny(timer, signal);
        wait.Cancel();
        try { await Task.WhenAll(timer, signal); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
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
                if (config.TryReload(configPath, out var error)) { configError = null; log.Write("info", "config.reloaded"); promptPath = ResolvePromptPath(config.Current); SignalScheduler(); }
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
                case ConsoleKey.P: paused = !paused; log.Write("info", paused ? "claims.paused" : "claims.resumed"); if (!paused) SignalScheduler(); break;
                case ConsoleKey.R: SignalScheduler(); break;
                case ConsoleKey.Q: RequestStop(); return;
            }
        }
    }

    private async Task<ExecResult> RunCodexAsync(Job job, IEnumerable<string> arguments, CancellationToken ct) => await processes.RunAsync(config.Current.CodexExe, arguments, config.Current.RepoRoot, ct, line =>
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
    });

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
    private const string ClarifySchema = """{"type":"object","properties":{"repositories":{"type":"array","items":{"type":"string"}},"rationale":{"type":"string"},"confidence":{"type":"number"},"ambiguous":{"type":"boolean"}},"required":["repositories","rationale","confidence","ambiguous"],"additionalProperties":false}""";
    private const string ResultSchema = """{"type":"object","properties":{"status":{"enum":["completed","noChanges","blocked"]},"summary":{"type":"string"},"validationEvidence":{"type":"array","items":{"type":"string"}},"repositories":{"type":"array","items":{"type":"object","properties":{"repository":{"type":"string"},"changed":{"type":"boolean"}},"required":["repository","changed"],"additionalProperties":false}},"commitMessage":{"type":"string"},"prTitle":{"type":"string"},"prBody":{"type":"string"},"checkDispositions":{"type":"array","items":{"type":"object","properties":{"checkId":{"type":"string"},"addressed":{"type":"boolean"},"summary":{"type":"string"}},"required":["checkId","addressed","summary"],"additionalProperties":false}},"threadDispositions":{"type":"array","items":{"type":"object","properties":{"threadId":{"type":"string"},"addressed":{"type":"boolean"},"replyBody":{"type":"string"}},"required":["threadId","addressed","replyBody"],"additionalProperties":false}}},"required":["status","summary","validationEvidence","repositories","commitMessage","prTitle","prBody","checkDispositions","threadDispositions"],"additionalProperties":false}""";
}
