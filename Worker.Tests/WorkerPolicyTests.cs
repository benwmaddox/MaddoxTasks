using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class WorkerPolicyTests
{
    [Fact]
    public void ShippedWorkerConfig_UsesAstraWithLowReasoningByDefault()
    {
        var configPath = FindWorkerAsset("worker.json");
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));

        Assert.Equal("gpt-6-astra", config.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", config.RootElement.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public void ShippedWorkerPrompt_RequiresAgentPolicyAndDelegationMatrix()
    {
        var prompt = File.ReadAllText(FindWorkerAsset("worker-prompt.md"));

        Assert.Contains("read and apply the applicable user-level AGENTS.md", prompt);
        Assert.Contains("gpt-6-astra with low reasoning", prompt);
        Assert.Contains("gpt-5.6-luna with max reasoning", prompt);
        Assert.Contains("gpt-5.6-sol with medium reasoning", prompt);
        Assert.Contains("Review delegated output", prompt);
        Assert.Contains("escalate to a stronger model if stalled", prompt);
    }

    [Fact]
    public void WorkspaceCleanupPolicy_RequiresExactOwnedRootAndTaskBranch()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "worktrees"); Directory.CreateDirectory(root);
        var job = CreateJob();
        job.Workspaces = [new Workspace("Repo", Path.Combine(root, "repo-1"), "codex/task-1-fix", "origin")];
        Assert.True(WorkspaceCleanupPolicy.IsProvenOwned(job, root));
        job.Workspaces[0] = job.Workspaces[0] with { Directory = Path.Combine(directory.Path, "outside") };
        Assert.False(WorkspaceCleanupPolicy.IsProvenOwned(job, root));
        job.Workspaces[0] = job.Workspaces[0] with { Directory = Path.Combine(root, "repo-1"), Branch = "codex/task-2-wrong" };
        Assert.False(WorkspaceCleanupPolicy.IsProvenOwned(job, root));
    }

    [Fact]
    public void WorkspaceCleanupPolicy_RejectsDuplicateEntriesAndFindsInvisiblePendingDoneJobs()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "worktrees"); Directory.CreateDirectory(root);
        var job = CreateJob(JobPhases.Done); job.CleanupPending = true;
        var workspace = new Workspace("Repo", Path.Combine(root, "repo-1"), "codex/task-1-fix", "origin");
        job.Workspaces = [workspace, workspace];
        Assert.False(WorkspaceCleanupPolicy.IsProvenOwned(job, root));
        Assert.Equal([job], WorkspaceCleanupPolicy.Pending([job, CreateJob(JobPhases.Done)]));
        Assert.Empty(DashboardPolicy.VisibleJobs([job], DateTime.UtcNow, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void WorkspaceBranchPolicy_SelectsFirstCollisionFreeRetryWithoutOverwritingPriorWork()
    {
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "codex/task-1-fix" };
        var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "codex/task-1-fix-retry-2" };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\worktrees\repo-1-retry-3" };

        var candidate = WorkspaceBranchPolicy.SelectAvailable("codex/task-1-fix", @"D:\worktrees\repo-1", local, remote, directories);

        Assert.Equal("codex/task-1-fix-retry-4", candidate.Branch);
        Assert.Equal(@"D:\worktrees\repo-1-retry-4", candidate.Directory);
    }

    [Fact]
    public void WorkspaceBranchPolicy_StartsMergedWorkFromTargetAndUnmergedWorkFromPriorHead()
    {
        Assert.Equal("origin/main", WorkspaceBranchPolicy.SelectStartingRef("[{\"mergedAt\":\"2026-09-05T00:00:00Z\",\"baseRefName\":\"main\"}]", "origin/trunk", "origin/codex/task-1-fix"));
        Assert.Equal("origin/codex/task-1-fix", WorkspaceBranchPolicy.SelectStartingRef("[{\"mergedAt\":null,\"baseRefName\":\"main\"}]", "origin/trunk", "origin/codex/task-1-fix"));
        Assert.Equal("origin/codex/task-1-fix", WorkspaceBranchPolicy.SelectStartingRef("[]", "origin/trunk", "origin/codex/task-1-fix"));
        Assert.Equal("origin/trunk", WorkspaceBranchPolicy.SelectStartingRef("[{\"mergedAt\":\"2026-09-05T00:00:00Z\",\"baseRefName\":\"\"}]", "origin/trunk", "origin/codex/task-1-fix"));
    }

    [Fact]
    public void WorkspaceCleanupPolicy_NeverSelectsBlockedJobsEvenWhenCleanupWasPreviouslyPending()
    {
        var blocked = CreateJob(JobPhases.Blocked);
        blocked.CleanupPending = true;
        blocked.Workspaces = [new Workspace("Repo", @"D:\worktrees\task", "codex/task-1-fix", "origin")];

        Assert.False(WorkspaceCleanupPolicy.CanDelete(blocked));
        Assert.Empty(WorkspaceCleanupPolicy.Pending([blocked]));
        Assert.True(blocked.CleanupPending);
        Assert.Single(blocked.Workspaces);
    }
    [Fact]
    public void ReviewWindow_RequiresUninterruptedGreenAndResetsForFeedback()
    {
        var window = new ReviewWindow();
        var start = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var quiet = TimeSpan.FromMinutes(30);

        Assert.False(window.Update(true, false, start, quiet));
        Assert.False(window.Update(true, true, start.AddMinutes(20), quiet));
        Assert.False(window.Update(true, false, start.AddMinutes(49), quiet));
        Assert.True(window.Update(true, false, start.AddMinutes(50), quiet));
    }

    [Fact]
    public void ReviewWindow_LosingGreenClearsTimer()
    {
        var window = new ReviewWindow();
        var start = DateTime.UnixEpoch;
        window.Update(true, false, start, TimeSpan.FromMinutes(30));
        Assert.False(window.Update(false, false, start.AddMinutes(29), TimeSpan.FromMinutes(30)));
        Assert.Null(window.GreenSinceUtc);
        Assert.False(window.Update(true, false, start.AddMinutes(31), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void FeedbackPolicy_DeduplicatesAndOnlyActsOnAddressedPendingThreads()
    {
        var job = CreateJob();
        var first = new ReviewFeedback("thread-a", "comment-a", 10, "fix a", "https://github.com/o/r/pull/1#discussion_r10");
        Assert.Single(FeedbackPolicy.AddNew(job, [first, first]));
        Assert.Empty(FeedbackPolicy.AddNew(job, [first]));

        var actions = FeedbackPolicy.ActionsFor(job,
        [
            new ReviewDisposition("thread-a", false, "not fixed"),
            new ReviewDisposition("thread-missing", true, "done"),
            new ReviewDisposition("thread-a", true, "fixed in the latest push")
        ]);
        Assert.Collection(actions, action => { Assert.True(action.Addressed); Assert.Equal("thread-a", action.ThreadId); });
        job.ProcessedFeedbackIds.Add(FeedbackPolicy.ActionKey("thread-a"));
        Assert.Empty(FeedbackPolicy.ActionsFor(job, actions));
    }

    [Fact]
    public void ReviewActionLedger_PersistsReplyBeforeResolveForRestartIdempotency()
    {
        var job = CreateJob();
        Assert.True(ReviewActionLedger.NeedsReply(job, "thread-a"));
        Assert.True(ReviewActionLedger.NeedsResolve(job, "thread-a"));
        job.ProcessedFeedbackIds.Add(ReviewActionLedger.ReplyKey("thread-a"));
        Assert.False(ReviewActionLedger.NeedsReply(job, "thread-a"));
        Assert.True(ReviewActionLedger.NeedsResolve(job, "thread-a"));
        job.ProcessedFeedbackIds.Add(ReviewActionLedger.ResolveKey("thread-a"));
        Assert.False(ReviewActionLedger.NeedsResolve(job, "thread-a"));
    }

    [Fact]
    public void RecoveryPlanner_RequeuesOnlyInterruptedExecutionPhasesInAgeOrder()
    {
        using var directory = new TemporaryDirectory();
        var journal = new Journal { Jobs =
        [
            CreateJob(JobPhases.Monitoring, DateTime.UnixEpoch),
            CreateJob(JobPhases.Repairing, DateTime.UnixEpoch.AddMinutes(2)),
            CreateJob(JobPhases.Done, DateTime.UnixEpoch),
            CreateJob(JobPhases.Implementing, DateTime.UnixEpoch.AddMinutes(1)),
            CreateJob(JobPhases.Blocked, DateTime.UnixEpoch)
        ] };
        var path = Path.Combine(directory.Path, "journal.json");
        journal.Save(path);
        Assert.False(File.Exists(path + ".tmp"));
        var recovered = Journal.Load(path);
        Assert.Equal([JobPhases.Implementing, JobPhases.Repairing], RecoveryPlanner.JobsToRequeue(recovered).Select(job => job.Phase));
    }

    [Fact]
    public void RecoveryPlanner_ResumesSessionsAndPersistedPublicationExplicitly()
    {
        var implementing = CreateJob(JobPhases.Implementing); implementing.ThreadId = "thread-1";
        var repairing = CreateJob(JobPhases.Repairing); repairing.ThreadId = "thread-2";
        var publishing = CreateJob(JobPhases.Publishing); publishing.PendingResultJson = "{\"status\":\"completed\"}";
        var legacyPublishing = CreateJob(JobPhases.Publishing); legacyPublishing.ThreadId = "thread-3";
        Assert.Equal(RecoveryMode.ResumeInitial, RecoveryPlanner.ModeFor(implementing));
        Assert.Equal(RecoveryMode.ResumeRepair, RecoveryPlanner.ModeFor(repairing));
        Assert.Equal(RecoveryMode.Publish, RecoveryPlanner.ModeFor(publishing));
        Assert.Equal(RecoveryMode.UnrecoverablePublication, RecoveryPlanner.ModeFor(legacyPublishing));
    }

    [Fact]
    public void Journal_LoadsLegacyJobsWithoutTaskUpdateState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "journal.json");
        File.WriteAllText(path, """
        {"Jobs":[{"Task":{"Sequence":1,"IssueId":"issue","Title":"Task","Description":"original","Repositories":["Repo"]},"Phase":"Implementing","StartedUtc":"2026-09-04T12:00:00Z","Prompt":"prompt","Model":"model","Effort":"medium"}]}
        """);

        var job = Assert.Single(Journal.Load(path).Jobs);
        Assert.Null(job.ObservedDescription);
        Assert.Empty(job.ProcessedHumanCommentKeys);
        Assert.Empty(job.PendingHumanComments);
        Assert.False(job.TaskUpdateWindowClosed);
        Assert.False(job.BlockedReassessmentAttempted);
    }

    [Fact]
    public void PublicationPolicy_RecoversCommitPushAndPullRequestProgress()
    {
        var progress = new PublicationProgress { CommitCreated = true, Pushed = true, PullRequestUrl = "https://github.com/o/r/pull/1" };
        Assert.True(PublicationPolicy.HasTaskCommit(progress, false, false));
        Assert.False(PublicationPolicy.NeedsPush(progress, "abc", "ABC"));
        Assert.False(PublicationPolicy.NeedsPullRequest(progress, null));
        var interrupted = new PublicationProgress();
        Assert.True(PublicationPolicy.HasTaskCommit(interrupted, false, true));
        Assert.True(PublicationPolicy.NeedsPush(interrupted, "new", "old"));
        Assert.False(PublicationPolicy.NeedsPullRequest(interrupted, "https://github.com/o/r/pull/1"));
    }

    [Fact]
    public void CommitHookRecovery_RequiresKnownSideEffectingStasisHookAndUnchangedIndex()
    {
        var sideEffectingHook = new ExecResult(1, "Stasis pre-commit: checking canonical source format\nall Stasis files are formatted", "Commit blocked: stage the formatted Stasis changes, then commit again.");
        Assert.True(CommitHookRecoveryPolicy.CanRestoreAndBypass(sideEffectingHook, "tree", "tree", true));
        Assert.False(CommitHookRecoveryPolicy.CanRestoreAndBypass(sideEffectingHook, "tree", "changed", true));
        Assert.False(CommitHookRecoveryPolicy.CanRestoreAndBypass(sideEffectingHook, "tree", "tree", false));

        var formatterRan = sideEffectingHook with { Output = sideEffectingHook.Output + "\nStasis pre-commit: formatting source before blocking this commit" };
        Assert.False(CommitHookRecoveryPolicy.CanRestoreAndBypass(formatterRan, "tree", "tree", true));
        Assert.False(CommitHookRecoveryPolicy.CanRestoreAndBypass(new ExecResult(1, "", "unrelated hook failure"), "tree", "tree", true));

        var enforcedFormat = new ExecResult(1,
            "Stasis pre-commit: enforcing canonical source format",
            "Commit blocked: review and stage the enforced formatting changes, then commit again.");
        Assert.True(CommitHookRecoveryPolicy.CanRestageAndRetry(enforcedFormat, "tree", "tree", true, true));
        Assert.False(CommitHookRecoveryPolicy.CanRestageAndRetry(enforcedFormat, "tree", "changed", true, true));
        Assert.False(CommitHookRecoveryPolicy.CanRestageAndRetry(enforcedFormat, "tree", "tree", true, false));
        Assert.False(CommitHookRecoveryPolicy.CanRestageAndRetry(new ExecResult(1, "", "unrelated hook failure"), "tree", "tree", true, true));
    }

    [Fact]
    public void ExecResultDiagnostics_PrefersStderrAndFallsBackToBoundedStdout()
    {
        Assert.Equal("stderr detail", ExecResultDiagnostics.Failure(new ExecResult(1, "stdout detail", "stderr detail")));
        Assert.Equal("stdout detail", ExecResultDiagnostics.Failure(new ExecResult(1, "stdout detail", "")));
        Assert.Equal("[earlier output truncated]\n6789", ExecResultDiagnostics.Failure(new ExecResult(1, "0123456789", ""), 4));
        Assert.Equal("exit code 7 with no process output", ExecResultDiagnostics.Failure(new ExecResult(7, "", "")));
    }

    [Fact]
    public void Dashboard_StripsControlSequencesAndKeepsLatestThreeLines()
    {
        var lines = DashboardFormatter.LatestLines("old\n\u001b[31msecond\u001b[0m\nthi\u0001rd\nfourth");
        Assert.Equal(["second", "third", "fourth"], lines);
        Assert.Equal("ab...", DashboardFormatter.Truncate("abcdefgh", 5));
    }

    [Fact]
    public void Dashboard_HumanizesKnownStructuredCodexResults()
    {
        var result = DashboardFormatter.LatestLines("""{"status":"completed","summary":"Implemented the fix","repositories":[{"repository":"MaddoxTasks","changed":true}]}""");
        Assert.Equal(["Summary: Implemented the fix"], result);
        Assert.DoesNotContain(result, line => line.Contains('{'));

        var clarification = DashboardFormatter.LatestLines("""{"repositories":["StasisLang"],"rationale":"The parser lives here","confidence":0.9,"ambiguous":false}""");
        Assert.Equal(["Repository clarification: identified", "Rationale: The parser lives here"], clarification);
        Assert.DoesNotContain(clarification, line => line.Contains('{'));
    }

    [Fact]
    public void Dashboard_WrapsAtSpacesWithIndentAwareWidth()
    {
        Assert.Equal(["  alpha beta", "  gamma"], DashboardFormatter.WrapLines(["alpha beta gamma"], 12));
        Assert.Equal(["    alpha", "    beta"], DashboardFormatter.WrapLines(["alpha beta"], 9, "    "));
    }

    [Fact]
    public void Dashboard_WrappedDetailsAreCappedAtThreeLines()
    {
        Assert.Equal(["  one", "  two", "  three"], DashboardFormatter.WrapLines(["one two three four five"], 7));
    }

    [Fact]
    public void Dashboard_SanitizesAndEllipsizesOnlyUnbreakableTokens()
    {
        var prose = DashboardFormatter.LatestLines("\u001b[31msupercalifragilistic\u001b[0m ok");
        Assert.Equal(["  super...", "  ok"], DashboardFormatter.WrapLines(prose, 10));
    }

    [Fact]
    public void Dashboard_HumanizedResultUsesWrappingPath()
    {
        var humanized = DashboardFormatter.LatestLines("""{"status":"completed","summary":"Implemented a carefully explained change","repositories":[{"repository":"MaddoxTasks","changed":true}]}""");
        var wrapped = DashboardFormatter.WrapLines(humanized, 24);
        Assert.Equal(3, wrapped.Length);
        Assert.DoesNotContain(wrapped, line => line.Contains("Repositories:"));
        Assert.All(wrapped, line => Assert.StartsWith("  ", line));
        Assert.DoesNotContain(wrapped, line => line.Contains('{'));
    }

    [Fact]
    public void DashboardSegments_UseDarkTerminalPaletteBySemanticRole()
    {
        var job = CreateJob(JobPhases.Implementing);
        var header = DashboardSegments.JobHeader(job, "Implementing", TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(345));
        Assert.Equal(ConsoleColor.Cyan, header[0].Color);
        Assert.Equal(ConsoleColor.Magenta, header[2].Color);
        Assert.Equal(ConsoleColor.Cyan, header[4].Color);
        Assert.Equal(ConsoleColor.Gray, header[5].Color);
        Assert.Equal("] 0:02:00", header[5].Text);
        var repository = DashboardSegments.RepositoryLine("MaddoxTasks", null);
        Assert.Equal(ConsoleColor.Cyan, repository[1].Color);
        Assert.Equal(ConsoleColor.White, DashboardSegments.Detail);
    }

    [Fact]
    public void DashboardSummary_TimestampChangesOnlyWhenNormalizedSummaryChanges()
    {
        var job = CreateJob();
        var first = new DateTime(2026, 9, 4, 13, 17, 0, DateTimeKind.Utc);
        Assert.True(DashboardSummary.Update(job, "same\r\nsummary", first));
        Assert.Equal(first, job.LatestChangedUtc);
        Assert.False(DashboardSummary.Update(job, "same\nsummary", first.AddMinutes(1)));
        Assert.Equal(first, job.LatestChangedUtc);
        Assert.True(DashboardSummary.Update(job, "new summary", first.AddMinutes(2)));
        Assert.Equal(first.AddMinutes(2), job.LatestChangedUtc);
    }

    [Fact]
    public void DashboardSegments_FormatsAndColorsUpdateTimestamp()
    {
        var local = new DateTimeOffset(2026, 9, 4, 21, 7, 0, TimeSpan.FromHours(-4));
        Assert.Equal("9:07 PM", DashboardSegments.FormatUpdateTimestamp(local));
        var segments = DashboardSegments.UpdateLine("  current status", local);
        Assert.Equal("  9:07 PM ", segments[0].Text);
        Assert.DoesNotContain("Update", segments[0].Text);
        Assert.Equal(ConsoleColor.Cyan, segments[0].Color);
        Assert.Equal("current status", segments[1].Text);
        Assert.Equal(ConsoleColor.White, segments[1].Color);
    }

    [Fact]
    public void Dashboard_NormalizesPersistedStructuredJsonBeforeFirstRender()
    {
        var lines = DashboardFormatter.NormalizePersistedLatest(["{\"status\":\"completed\",\"summary\":\"Ready\",\"repositories\":[{\"repository\":\"Repo\",\"changed\":true}]}"]);
        Assert.Equal(["Summary: Ready"], lines);
        Assert.DoesNotContain(lines, line => line.Contains('{') || line.Contains("Repositories:"));
    }

    [Fact]
    public void DashboardPolicy_ShowsBlockedBrieflyThenHidesIt()
    {
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var active = CreateJob(JobPhases.Claimed, now.AddMinutes(-20));
        var recent = CreateJob(JobPhases.Blocked, now.AddMinutes(-9)); recent.PhaseChangedUtc = now.AddMinutes(-9);
        var expired = CreateJob(JobPhases.Blocked, now.AddMinutes(-10)); expired.PhaseChangedUtc = now.AddMinutes(-10);
        var done = CreateJob(JobPhases.Done, now.AddMinutes(-1));
        Assert.Equal([active, recent], DashboardPolicy.VisibleJobs([active, recent, expired, done], now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void DashboardPolicy_ShowsOnlyLatestRecordAndRetryReplacesBlockedImmediately()
    {
        var now = DateTime.UnixEpoch.AddDays(1);
        var issueId = Guid.NewGuid().ToString();
        var blocked = CreateJob(JobPhases.Blocked, now.AddMinutes(-1));
        blocked.Task = blocked.Task with { IssueId = issueId };
        blocked.PhaseChangedUtc = now.AddMinutes(-1);
        var retry = CreateJob(JobPhases.Implementing, now);
        retry.Task = retry.Task with { IssueId = issueId };
        retry.PhaseChangedUtc = now;

        Assert.Equal([retry], DashboardPolicy.VisibleJobs([blocked, retry], now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void MonitoringDisplay_DistinguishesQuietWindowFromManualMergeDecision()
    {
        var now = new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc);
        var job = CreateJob(JobPhases.Monitoring, now.AddMinutes(-5));
        Assert.Equal("Waiting on CI/review window", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), false));

        job.ReviewWindow.GreenSinceUtc = now.AddMinutes(-12);
        Assert.Equal("Waiting on CI/review window · 18m left", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), false));
        job.ReviewWindow.GreenSinceUtc = now.AddMinutes(-30);
        Assert.Equal("Waiting on CI/review window", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), true));

        job.ReadyForReviewRecorded = true;
        job.ReviewWindow.GreenSinceUtc = now.AddMinutes(-12);
        job.ReviewWindow.Closed = false;
        Assert.Equal("Ready for review · auto-merge in 18m", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), true));
        job.ReviewWindow.GreenSinceUtc = now.AddMinutes(-30);
        Assert.Equal("Waiting for your PR decision", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), false));
        Assert.Equal("Ready to auto-merge", MonitoringDisplay.Describe(job, now, TimeSpan.FromMinutes(30), true));
    }

    [Fact]
    public void CodexEventParser_ReadsThreadAndNestedAssistantMessage()
    {
        var started = CodexEventParser.Parse("{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}");
        var message = CodexEventParser.Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"working\"}}");
        Assert.Equal("thread-1", started.ThreadId);
        Assert.Equal("working", message.Text);
    }

    [Fact]
    public void CodexTerminalEventTracker_RequiresStructuredResultBeforeTerminalEvent()
    {
        var tracker = new CodexTerminalEventTracker();
        Assert.False(tracker.Observe("{\"type\":\"turn.completed\"}"));
        Assert.False(tracker.Observe("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"completed\\\",\\\"summary\\\":\\\"done\\\"}\"}}"));
        Assert.True(tracker.Observe("{\"type\":\"turn.completed\"}"));

        var legacy = new CodexTerminalEventTracker();
        Assert.True(legacy.Observe("{\"type\":\"task_complete\",\"status\":\"blocked\",\"summary\":\"needs input\"}"));

        var session = new CodexTerminalEventTracker();
        Assert.True(session.Observe("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"{\\\"status\\\":\\\"completed\\\",\\\"summary\\\":\\\"done\\\"}\"}}"));

        var clarification = new CodexTerminalEventTracker();
        Assert.False(clarification.Observe("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"repositories\\\":[\\\"StasisLang\\\"],\\\"ambiguous\\\":false}\"}}"));
        Assert.True(clarification.Observe("{\"type\":\"turn.completed\"}"));
    }

    [Fact]
    public void TaskUpdatePolicy_SeedsNewClaimsAndQueuesOnlyNewHumanUpdates()
    {
        var started = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var old = new TaskCommentDto(started.AddMinutes(-1), "old", "user");
        var worker = new TaskCommentDto(started.AddMinutes(1), "worker status", "maddox-worker");
        var claimed = new TaskDto(1, "issue", "Task", "original", ["Repo"]) { Comments = [old], UpdatedAt = started };
        var job = CreateJob(JobPhases.Implementing, started);
        TaskUpdatePolicy.Seed(job, claimed);

        var live = claimed with { Description = "replacement", Comments = [old, worker, new TaskCommentDto(started.AddMinutes(2), "please adjust", "USER")], UpdatedAt = started.AddMinutes(2) };
        Assert.True(TaskUpdatePolicy.Ingest(job, live));
        Assert.Equal("replacement", job.PendingDescription);
        Assert.Equal("please adjust", Assert.Single(job.PendingHumanComments).Comment);
        Assert.False(TaskUpdatePolicy.Ingest(job, live));
        Assert.Equal("Task update queued", TaskUpdatePolicy.DashboardPhase(job, job.Phase));

        TaskUpdatePolicy.BeginApplying(job);
        Assert.Equal("Applying task update", TaskUpdatePolicy.DashboardPhase(job, job.Phase));
        TaskUpdatePolicy.EndApplying(job);

        var batch = TaskUpdatePolicy.Capture(job);
        TaskUpdatePolicy.MarkDelivered(job, batch);
        Assert.False(TaskUpdatePolicy.HasPending(job));
    }

    [Fact]
    public void TaskUpdatePolicy_LegacyRecoveryOnlyQueuesHumanCommentsAfterStart()
    {
        var started = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var job = CreateJob(JobPhases.Implementing, started);
        var live = job.Task with
        {
            Comments =
            [
                new TaskCommentDto(started.AddMinutes(-1), "before claim", "user"),
                new TaskCommentDto(started.AddMinutes(1), "after claim", "user"),
                new TaskCommentDto(started.AddMinutes(2), "agent output", "gpt-5.6-sol medium")
            ],
            UpdatedAt = started.AddMinutes(2)
        };

        Assert.True(TaskUpdatePolicy.Ingest(job, live));
        Assert.Equal("after claim", Assert.Single(job.PendingHumanComments).Comment);
        Assert.False(TaskUpdatePolicy.Ingest(job, live));
    }

    [Fact]
    public void TaskUpdatePolicy_DeliveryAcknowledgementPreservesUpdatesArrivingInFlight()
    {
        var job = CreateJob();
        job.PendingDescription = "first";
        job.PendingHumanComments.Add(new TaskCommentDto(DateTime.UnixEpoch, "first", "user"));
        var batch = TaskUpdatePolicy.Capture(job);
        job.PendingDescription = "second";
        job.PendingHumanComments.Add(new TaskCommentDto(DateTime.UnixEpoch.AddSeconds(1), "second", "user"));

        TaskUpdatePolicy.MarkDelivered(job, batch);
        Assert.Equal("second", job.PendingDescription);
        Assert.Equal("second", Assert.Single(job.PendingHumanComments).Comment);
    }

    [Fact]
    public void TaskUpdatePolicy_InFlightStateIsTransientAcrossJournalRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maddox-worker-{Guid.NewGuid():N}.json");
        try
        {
            var job = CreateJob();
            job.PendingDescription = "updated";
            TaskUpdatePolicy.BeginApplying(job);
            new Journal { Jobs = [job] }.Save(path);

            var recovered = Assert.Single(Journal.Load(path).Jobs);
            Assert.False(recovered.TaskUpdateInFlight);
            Assert.Equal("Task update queued", TaskUpdatePolicy.DashboardPhase(recovered, recovered.Phase));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ClarificationPolicy_ParsesAssignmentAndOneRepositoryPerSplitChild()
    {
        var assignment = ClarificationPolicy.Parse("""{"action":"assign","repositories":["MaddoxTasks","StasisLang"],"children":[],"rationale":"coupled","confidence":0.9,"ambiguous":false}""");
        Assert.Equal("assign", assignment.Action);
        Assert.Equal(["MaddoxTasks", "StasisLang"], assignment.Repositories);

        var split = ClarificationPolicy.Parse("""{"action":"split","repositories":[],"children":[{"title":"A","description":"Do A","repository":"Alpha","rationale":"independent"},{"title":"B","description":"Do B","repository":"Beta","rationale":"independent"}],"rationale":"independent work","confidence":0.95,"ambiguous":false}""");
        Assert.Equal("split", split.Action);
        Assert.Equal(["Alpha", "Beta"], split.Children.Select(child => child.Repository));
    }

    [Theory]
    [InlineData("{\"action\":\"split\",\"repositories\":[],\"children\":[{\"title\":\"A\",\"description\":\"Do A\",\"repository\":\"Alpha\",\"rationale\":\"x\"}],\"rationale\":\"x\",\"confidence\":1,\"ambiguous\":false}")]
    [InlineData("{\"action\":\"split\",\"repositories\":[],\"children\":[{\"title\":\"A\",\"description\":\"Do A\",\"repository\":\"Alpha\",\"rationale\":\"x\"},{\"title\":\"B\",\"description\":\"Do B\",\"repository\":\"alpha\",\"rationale\":\"x\"}],\"rationale\":\"x\",\"confidence\":1,\"ambiguous\":false}")]
    [InlineData("{\"action\":\"assign\",\"repositories\":[\"Alpha\"],\"children\":[],\"rationale\":\"unclear\",\"confidence\":0.2,\"ambiguous\":true}")]
    public void ClarificationPolicy_RejectsInvalidOrAmbiguousDecisions(string json)
        => Assert.Throws<InvalidDataException>(() => ClarificationPolicy.Parse(json));

    [Fact]
    public void BlockedReassessmentPolicy_AllowsOnlyOneSameSessionAttempt()
    {
        var job = CreateJob(JobPhases.Publishing);
        Assert.False(BlockedReassessmentPolicy.ShouldReassess(job, "blocked"));
        job.ThreadId = "thread-1";
        Assert.True(BlockedReassessmentPolicy.ShouldReassess(job, "blocked"));
        job.BlockedReassessmentAttempted = true;
        Assert.False(BlockedReassessmentPolicy.ShouldReassess(job, "blocked"));
        Assert.False(BlockedReassessmentPolicy.ShouldReassess(job, "completed"));
    }

    [Fact]
    public void ExtractResult_ReadsCodexJsonlAgentMessage()
    {
        var output = "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"completed\\\"}\"}}\n{\"type\":\"turn.completed\"}";
        using var result = JsonDocument.Parse(WorkerHost.ExtractResult(output));
        Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ExtractResult_ReadsNestedSessionTaskComplete()
    {
        var output = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"{\\\"status\\\":\\\"noChanges\\\",\\\"summary\\\":\\\"already done\\\"}\"}}";
        using var result = JsonDocument.Parse(WorkerHost.ExtractResult(output));
        Assert.Equal("noChanges", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void TaskUpdatePrompt_ContainsDescriptionAndOrderedComments()
    {
        var batch = new PendingTaskUpdateBatch("ToddlerMatch only", [
            new TaskCommentDto(DateTime.UnixEpoch, "first", "user"),
            new TaskCommentDto(DateTime.UnixEpoch.AddSeconds(1), "second", "user")]);
        var prompt = WorkerHost.BuildTaskUpdatePrompt(batch);
        Assert.Contains("ToddlerMatch only", prompt);
        Assert.True(prompt.IndexOf("first", StringComparison.Ordinal) < prompt.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void ResearchPlanPolicy_AllowsOnlyTaskEntryMutations()
    {
        const string sourceId = "01234567-89ab-cdef-0123-456789abcdef";
        const string targetId = "fedcba98-7654-3210-fedc-ba9876543210";
        var json = $$"""
        {
          "outcome": "unblocked",
          "summary": "The dependency can be documented and reprioritized.",
          "findings": ["The target task owns the missing contract."],
          "mutations": [
            {"type":"AddComment","issueId":"{{targetId}}","comment":"Contract is ready."},
            {"type":"UpdateDescription","issueId":"{{targetId}}","description":"Updated contract."},
            {"type":"ChangePriority","issueId":"{{targetId}}","newPriority":2},
            {"type":"AddLabel","issueId":"{{targetId}}","label":"researched"},
            {"type":"RemoveLabel","issueId":"{{targetId}}","label":"old"},
            {"type":"SetRepositoryLabels","issueId":"{{targetId}}","repositories":["alpha"]},
            {"type":"ChangeStatus","issueId":"{{targetId}}","newStatus":"Next"},
            {"type":"CreateIssue","title":"Track contract","description":"Follow-up","status":"Backlog","priority":3,"repositories":["beta"]}
          ]
        }
        """;

        var plan = ResearchPlanPolicy.Parse(json, new TaskDto(42, sourceId, "Source", "Blocked", ["alpha"]));

        Assert.Equal(ResearchPlanPolicy.Unblocked, plan.Outcome);
        Assert.Equal(8, plan.Mutations.Length);
        Assert.Equal(["AddComment", "UpdateDescription", "ChangePriority", "AddLabel", "RemoveLabel", "SetRepositoryLabels", "ChangeStatus", "CreateIssue"], plan.Mutations.Select(mutation => mutation.Type).ToArray());
    }

    [Fact]
    public void ResearchPlanPolicy_AllowsCompletedLedgerOnlyOutcome()
    {
        var plan = ResearchPlanPolicy.Parse(
            """{"outcome":"completed","summary":"Task entries updated.","findings":[],"mutations":[]}""",
            new TaskDto(499, "01234567-89ab-cdef-0123-456789abcdef", "Unblock", "Triage tasks", []));

        Assert.Equal(ResearchPlanPolicy.Completed, plan.Outcome);
    }

    [Theory]
    [InlineData("SplitIssue")]
    [InlineData("RequeueBlocked")]
    [InlineData("RunCommand")]
    [InlineData("DeleteIssue")]
    public void ResearchPlanPolicy_RejectsNonTaskEntryMutationTypes(string mutationType)
    {
        var json = $$"""
        {"outcome":"stillBlocked","summary":"No safe change.","findings":[],"mutations":[{"type":"{{mutationType}}"}]}
        """;

        Assert.Throws<InvalidDataException>(() => ResearchPlanPolicy.Parse(json, new TaskDto(42, "01234567-89ab-cdef-0123-456789abcdef", "Source", "Blocked", [])));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("compact")]
    [InlineData("guid-prefix-d")]
    [InlineData("guid-prefix-n")]
    [InlineData("sequence")]
    [InlineData("hash-sequence")]
    public void ResearchPlanPolicy_RejectsSourceStatusMutationByEveryIssueTokenForm(string tokenForm)
    {
        const string sourceId = "01234567-89ab-cdef-0123-456789abcdef";
        var token = tokenForm switch
        {
            "full" => sourceId,
            "compact" => "0123456789abcdef0123456789abcdef",
            "guid-prefix-d" => "01234567-",
            "guid-prefix-n" => "0123456789ab",
            "sequence" => "42",
            "hash-sequence" => "#42",
            _ => throw new ArgumentOutOfRangeException(nameof(tokenForm))
        };
        var json = $$"""
        {"outcome":"unblocked","summary":"Attempted source status change.","findings":[],"mutations":[{"type":"ChangeStatus","issueId":"{{token}}","newStatus":"Next"}]}
        """;

        Assert.Throws<InvalidDataException>(() => ResearchPlanPolicy.Parse(json, new TaskDto(42, sourceId, "Source", "Blocked", [])));
    }

    [Fact]
    public void ResearchPrompt_AllowsReadOnlyExternalResearchButForbidsMutations()
    {
        var prompt = WorkerHost.BuildResearchPrompt(
            new TaskDto(42, "source", "Blocked task", "Find the dependency", ["alpha"]),
            "[]");

        Assert.Contains("read-only web", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external mutation", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("call external services", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not edit files", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ResearchArguments_EnableLiveSearchAndSendPromptThroughStdin()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "worker.json");
        WriteConfig(configPath, directory.Path, 4, "model");
        var arguments = WorkerHost.BuildResearchCodexArguments(WorkerConfig.Load(configPath), "schema.json", "research prompt");
        var input = ProcessArguments.WithPromptOnStandardInput(arguments);

        Assert.Equal("--search", input.Arguments[0]);
        Assert.Equal("exec", input.Arguments[1]);
        Assert.Contains("read-only", input.Arguments);
        Assert.Equal("-", input.Arguments[^1]);
        Assert.DoesNotContain("research prompt", input.Arguments);
        Assert.Equal("research prompt", input.StandardInput);
    }

    [Fact]
    public void ResearchAdmission_AllowsOnlyOneActiveReservation()
    {
        var admission = new ResearchAdmission();

        Assert.True(admission.TryReserve());
        Assert.True(admission.IsActive);
        Assert.False(admission.TryReserve());
        admission.Release();
        Assert.False(admission.IsActive);
        Assert.True(admission.TryReserve());
        admission.Release();
    }

    [Fact]
    public void ConfigReload_IsAtomicAndExistingSnapshotDoesNotChange()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 4, "model-one");
        var state = new ConfigState(WorkerConfig.Load(path));
        var job = WithSnapshot(CreateJob(), state.Current.Model);
        WriteConfig(path, directory.Path, 2, "model-two", "00:05:00");
        Assert.True(state.TryReload(path, out _));
        Assert.Equal(2, state.Current.MaxConcurrentCodexProcesses);
        Assert.Equal(TimeSpan.FromMinutes(5), state.Current.EffectiveBlockedDisplayDuration);
        Assert.Equal("model-one", job.Model);

        File.WriteAllText(path, "{\"schemaVersion\":99}");
        Assert.False(state.TryReload(path, out var error));
        Assert.NotNull(error);
        Assert.Equal("model-two", state.Current.Model);
    }

    [Fact]
    public void WorkerConfig_DefaultsBlockedDisplayDurationToTenMinutes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 2, "model");
        Assert.Equal(TimeSpan.FromMinutes(10), WorkerConfig.Load(path).EffectiveBlockedDisplayDuration);
    }

    [Fact]
    public void WorkerConfig_DefaultsResearchCooldownToFourteenDaysAndRejectsNonPositiveValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 2, "model");
        Assert.Equal(TimeSpan.FromDays(14), WorkerConfig.Load(path).EffectiveResearchCooldown);

        WriteConfig(path, directory.Path, 2, "model", researchCooldown: "00:00:00");
        Assert.Throws<InvalidDataException>(() => WorkerConfig.Load(path));

        WriteConfig(path, directory.Path, 2, "model", researchCooldown: "00:02:00");
        var state = new ConfigState(WorkerConfig.Load(path));
        Assert.Equal(TimeSpan.FromMinutes(2), state.Current.EffectiveResearchCooldown);
        WriteConfig(path, directory.Path, 2, "model", researchCooldown: "00:03:00");
        Assert.True(state.TryReload(path, out var error), error);
        Assert.Equal(TimeSpan.FromMinutes(3), state.Current.EffectiveResearchCooldown);
    }

    [Fact]
    public void WorkerConfig_AllowsZeroConcurrencyAndRejectsNegativeConcurrency()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 0, "model");
        Assert.Equal(0, WorkerConfig.Load(path).MaxConcurrentCodexProcesses);
        WriteConfig(path, directory.Path, -1, "model");
        Assert.Throws<InvalidDataException>(() => WorkerConfig.Load(path));
    }

    [Fact]
    public void ConfigReload_FromZeroConcurrencyLetsGateResume()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 0, "model");
        var state = new ConfigState(WorkerConfig.Load(path));
        var gate = new ConcurrencyGate(() => state.Current.MaxConcurrentCodexProcesses);
        Assert.False(gate.TryReserve());

        WriteConfig(path, directory.Path, 2, "model");
        Assert.True(state.TryReload(path, out _));
        Assert.True(gate.TryReserve());
        gate.Release();
    }

    [Fact]
    public void ClaimAdmission_RequiresReadablePromptBeforeClaimSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "worker.json");
        WriteConfig(configPath, directory.Path, 1, "model-one");
        var config = WorkerConfig.Load(configPath);
        Assert.False(ClaimAdmission.TrySnapshot(config, configPath, out _, out var missingError));
        Assert.Contains("worker-prompt.md", missingError);
        File.WriteAllText(Path.Combine(directory.Path, "worker-prompt.md"), "stable prompt");
        Assert.True(ClaimAdmission.TrySnapshot(config, configPath, out var snapshot, out _));
        Assert.Equal("stable prompt", snapshot!.Prompt);
    }

    [Fact]
    public void ReservationAttribution_ProvidesImmediateAndExactOwnerComments()
    {
        var job = CreateJob();
        Assert.True(ReservationAttribution.NeedsPending(job));
        Assert.False(ReservationAttribution.NeedsExact(job));
        job.ReservationOwnerRecorded = true;
        job.ThreadId = "thread-123";
        Assert.False(ReservationAttribution.NeedsPending(job));
        Assert.True(ReservationAttribution.NeedsExact(job));
        Assert.Equal("Reservation owner: codexThreadId=thread-123", ReservationAttribution.Exact(job.ThreadId));
    }

    [Fact]
    public void RollingLog_WritesDailyJsonLines()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeClock(new DateTime(2026, 9, 4, 12, 30, 0, DateTimeKind.Utc));
        var log = new RollingLog(directory.Path, clock);
        log.Write("info", "test.event", new { count = 2, credential = "token=ghp_abcdefghijklmnopqrstuvwxyz" });
        var line = File.ReadAllText(Path.Combine(directory.Path, "worker-20260904.jsonl"));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("test.event", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        Assert.DoesNotContain("ghp_", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainmentFactory_ExposesPortableSeam()
    {
        using var portable = ChildProcessContainmentFactory.Create(false);
        Assert.IsType<NoopContainment>(portable);
        if (OperatingSystem.IsWindows())
        {
            using var windows = ChildProcessContainmentFactory.Create(true);
            Assert.IsType<WindowsJobContainment>(windows);
        }
    }

    [Fact]
    public void ConcurrencyGate_LoweredCapacityDrainsWithoutStartingMoreWork()
    {
        var limit = 2;
        var gate = new ConcurrencyGate(() => limit);
        Assert.True(gate.TryReserve());
        Assert.True(gate.TryReserve());
        limit = 1;
        Assert.False(gate.TryReserve());
        gate.Release();
        Assert.False(gate.TryReserve());
        gate.Release();
        Assert.True(gate.TryReserve());
        gate.Release();
    }

    [Fact]
    public void ConcurrencyGate_ZeroAdmitsNothingAndReloadAboveZeroResumes()
    {
        var limit = 1;
        var gate = new ConcurrencyGate(() => limit);
        Assert.True(gate.TryReserve());
        limit = 0;
        Assert.False(gate.TryReserve());
        gate.Release();
        Assert.Equal(0, gate.Active);
        Assert.False(gate.TryReserve());
        limit = 2;
        Assert.True(gate.TryReserve());
        gate.Release();
    }

    [Fact]
    public void ProgramOptions_ParsesStopBeforeNormalWorkerAdmission()
    {
        Assert.True(ProgramOptions.Parse(["--stop"]).Stop);
        Assert.Equal("custom.json", ProgramOptions.Parse(["custom.json"]).ConfigPath);
        Assert.False(ProgramOptions.Parse([]).Stop);
        Assert.Throws<ArgumentException>(() => ProgramOptions.Parse(["--unknown"]));
    }

    [Fact]
    public void FreshClaimAllowance_ReservesAtMostOneSlotPerTickWithSpareCapacity()
    {
        var gate = new ConcurrencyGate(() => 2);
        var tick = new FreshClaimAllowance();
        Assert.True(tick.TryReserve(gate));
        Assert.Equal(1, gate.Active);
        Assert.False(tick.TryReserve(gate));
        Assert.Equal(1, gate.Active);
        gate.Release();
        Assert.True(new FreshClaimAllowance().TryReserve(gate));
        gate.Release();
    }

    [Fact]
    public void ClaimCadence_StartsImmediatelyAndKeepsFillingWhileCapacityRemains()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 4, "model");
        var settings = WorkerConfig.Load(path);
        var start = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var cadence = new ClaimCadence(start);

        Assert.True(cadence.IsDue(start, settings));
        cadence.CompleteTick(FreshClaimOutcome.ClaimedWithSpareCapacity, start);
        Assert.True(cadence.ImmediateRefillPending);
        Assert.Equal(start, cadence.NextTickUtc(settings));
        Assert.True(cadence.IsDue(start, settings));

        cadence.CompleteTick(FreshClaimOutcome.ClaimedWithSpareCapacity, start);
        Assert.Equal(start, cadence.NextTickUtc(settings));
    }

    [Fact]
    public void ClaimCadence_FullOrUnavailableClaimUsesNormalRetryInterval()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 2, "model");
        var settings = WorkerConfig.Load(path);
        var start = DateTime.UnixEpoch;
        var cadence = new ClaimCadence(start);

        cadence.CompleteTick(FreshClaimOutcome.ClaimedAtCapacity, start);
        Assert.False(cadence.ImmediateRefillPending);
        Assert.Equal(start.AddMinutes(15), cadence.NextTickUtc(settings));

        cadence.CompleteTick(FreshClaimOutcome.Unavailable, start.AddMinutes(15));
        Assert.False(cadence.ImmediateRefillPending);
        Assert.Equal(start.AddMinutes(30), cadence.NextTickUtc(settings));
    }

    [Fact]
    public void ClaimCadence_AvailableSlotRequestsImmediateRefillAfterBackoff()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 4, "model");
        var settings = WorkerConfig.Load(path);
        var start = DateTime.UnixEpoch;
        var cadence = new ClaimCadence(start);
        cadence.CompleteTick(FreshClaimOutcome.Unavailable, start);
        Assert.False(cadence.IsDue(start.AddMinutes(2), settings));

        cadence.RequestImmediateRefill(start.AddMinutes(2));

        Assert.True(cadence.ImmediateRefillPending);
        Assert.True(cadence.IsDue(start.AddMinutes(2), settings));
        Assert.Equal(start.AddMinutes(2), cadence.NextTickUtc(settings));
    }

    [Fact]
    public void WorkerConfig_IgnoresLegacyCapacityFillInterval()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "worker.json");
        WriteConfig(path, directory.Path, 4, "model");
        AddCapacityFillInterval(path, "00:00:00");
        var settings = WorkerConfig.Load(path);

        Assert.Equal(TimeSpan.FromMinutes(15), settings.ClaimInterval);
    }

    [Fact]
    public void BlockedWorkspaceAdoption_UsesNewestEligibleOwnedJobAndRefreshesClaim()
    {
        using var directory = new TemporaryDirectory();
        var issueId = Guid.NewGuid().ToString();
        var old = OwnedBlockedJob(issueId, directory.Path, "old", DateTime.UnixEpoch);
        var newest = OwnedBlockedJob(issueId, directory.Path, "new", DateTime.UnixEpoch.AddHours(1));
        var duplicateWithoutWorkspace = CreateJob(JobPhases.Blocked, DateTime.UnixEpoch.AddHours(2));
        duplicateWithoutWorkspace.Task = duplicateWithoutWorkspace.Task with { IssueId = issueId, Repositories = ["Repo"] };
        var journal = new Journal { Jobs = [old, newest, duplicateWithoutWorkspace] };
        var claimed = new TaskDto(482, issueId, "Refreshed title", "Refreshed description", ["repo"]);

        var adopted = BlockedWorkspaceAdoption.TryAdopt(journal, claimed, directory.Path, DateTime.UnixEpoch.AddDays(1));

        Assert.Same(newest, adopted);
        Assert.Equal("Refreshed title", adopted!.Task.Title);
        Assert.Equal(JobPhases.Claimed, adopted.Phase);
        Assert.Equal("new-prompt", adopted.Prompt);
        Assert.Single(journal.Jobs, job => job.Phase == JobPhases.Claimed);
        Assert.Equal(2, journal.Jobs.Count(job => job.Phase == JobPhases.Blocked));
    }

    [Fact]
    public void BlockedWorkspaceAdoption_RefusesMismatchedRepositoriesOrNoWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var issueId = Guid.NewGuid().ToString();
        var mismatch = OwnedBlockedJob(issueId, directory.Path, "mismatch", DateTime.UnixEpoch);
        mismatch.Task = mismatch.Task with { Repositories = ["Other"] };
        var noWorkspace = CreateJob(JobPhases.Blocked);
        noWorkspace.Task = noWorkspace.Task with { IssueId = issueId, Repositories = ["Repo"] };
        var claimed = new TaskDto(482, issueId, "Retry", "Description", ["Repo"]);

        Assert.Null(BlockedWorkspaceAdoption.TryAdopt(new Journal { Jobs = [mismatch] }, claimed, directory.Path, DateTime.UtcNow));
        Assert.Null(BlockedWorkspaceAdoption.TryAdopt(new Journal { Jobs = [noWorkspace] }, claimed, directory.Path, DateTime.UtcNow));
    }

    [Fact]
    public void InitialCodexArguments_UseApproveForMeWithoutConflictingSandboxOption()
    {
        var job = CreateJob();
        job.Workspaces.Add(new Workspace("Repo", @"D:\code\Repo-worktree", "codex/task-1", "https://github.com/example/Repo.git"));

        var arguments = WorkerHost.BuildInitialCodexArguments(job, "schema.json", "prompt");

        Assert.Contains("--approve-for-me", arguments);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.DoesNotContain("--sandbox", arguments);
    }

    [Fact]
    public void ContinuationCodexArguments_KeepRepositoryTrustRecoveryEnabled()
    {
        var job = CreateJob();
        job.ThreadId = "thread-123";

        var arguments = WorkerHost.BuildContinuationCodexArguments(job, "schema.json", "prompt");

        Assert.Equal("resume", arguments[1]);
        Assert.Equal("thread-123", arguments[2]);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Equal("prompt", arguments[^1]);
    }

    [Fact]
    public void InitialCodexArguments_UseRepoRootForRepositorylessTask()
    {
        var job = CreateJob();
        job.Task = job.Task with { Repositories = [] };

        var arguments = WorkerHost.BuildInitialCodexArguments(job, "schema.json", "prompt", @"D:\code");

        Assert.Equal(@"D:\code", arguments[arguments.IndexOf("-C") + 1]);
        Assert.Contains("--skip-git-repo-check", arguments);
    }

    [Fact]
    public void ProcessArguments_AddExactSafeDirectoryForGitOnly()
    {
        var workingDirectory = Path.GetFullPath(@"D:\code\Repo");
        var git = ProcessArguments.Prepare("git", ["status", "--porcelain"], workingDirectory);
        var codex = ProcessArguments.Prepare("codex", ["--version"], workingDirectory);

        Assert.Equal(["-c", $"safe.directory={workingDirectory.Replace('\\', '/')}", "status", "--porcelain"], git);
        Assert.Equal(["--version"], codex);
    }

    [Fact]
    public void PublicationMetadata_UsesSafeFallbacksForBlankStructuredFields()
    {
        using var document = JsonDocument.Parse("""{"commitMessage":"  ","prTitle":"","prBody":"\r\n","summary":"Implemented the fix"}""");

        Assert.Equal("Complete task 42", PublicationMetadata.CommitMessage(document.RootElement, 42));
        Assert.Equal("Task title", PublicationMetadata.PullRequestTitle(document.RootElement, "Task title"));
        Assert.Equal("Implemented the fix", PublicationMetadata.PullRequestBody(document.RootElement));
    }

    [Fact]
    public void RepositoryPathPolicy_NormalizesAbsoluteAndRelativePathsBeneathRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "maddox-repositories"));

        Assert.Equal("StasisLang", RepositoryPathPolicy.Normalize(root, "StasisLang"));
        Assert.Equal("StasisLang", RepositoryPathPolicy.Normalize(root, Path.Combine(root, "StasisLang")));
        Assert.Throws<InvalidDataException>(() => RepositoryPathPolicy.Normalize(root, Path.GetDirectoryName(root)!));
    }

    [Fact]
    public void WorkspaceProcessEnvironment_IsolatesCargoPerCommandWorkingDirectory()
    {
        var environment = WorkspaceProcessEnvironment.IsolatedBuild();

        Assert.Equal("target", environment["CARGO_TARGET_DIR"]);
        Assert.Equal("0", environment["CARGO_INCREMENTAL"]);
    }

    [Fact]
    public void ProcessRunner_DecodesChildOutputAndErrorsAsUtf8()
    {
        var startInfo = ProcessRunner.CreateStartInfo("codex", @"D:\code");
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
    }

    private static string FindWorkerAsset(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(directory.FullName, "Worker", fileName),
                    Path.Combine(directory.FullName, fileName)
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not find shipped worker asset '{fileName}'.");
    }

    private static Job CreateJob(string phase = JobPhases.Claimed, DateTime? started = null) => new()
    {
        Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", ["Repo"]),
        Prompt = "prompt", Model = "model", Effort = "medium", Phase = phase, StartedUtc = started ?? DateTime.UnixEpoch,
        PhaseChangedUtc = started ?? DateTime.UnixEpoch
    };

    private static Job OwnedBlockedJob(string issueId, string worktreeRoot, string marker, DateTime started) => new()
    {
        Task = new TaskDto(482, issueId, "Blocked task", "Description", ["Repo"]),
        Prompt = marker + "-prompt",
        Model = marker + "-model",
        Effort = "medium",
        Phase = JobPhases.Blocked,
        StartedUtc = started,
        Workspaces = [new Workspace("Repo", Path.Combine(worktreeRoot, marker), "codex/task-482-" + marker, "https://github.com/owner/Repo.git")]
    };

    private static Job WithSnapshot(Job job, string model) { job.Model = model; return job; }

    private static void WriteConfig(string path, string root, int cap, string model, string? blockedDisplayDuration = null, string? researchCooldown = null)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, claimInterval = "00:15:00", maxConcurrentCodexProcesses = cap, prPollInterval = "00:01:00",
            clarificationTimeout = "00:10:00", promptFile = "worker-prompt.md", model, reasoningEffort = "medium",
            repairMaxAttempts = 3, repairMaxElapsed = "02:00:00", reviewQuietPeriod = "00:30:00", ignoredChecks = Array.Empty<string>(),
            blockedDisplayDuration, researchCooldown,
            autoMergeRepositories = new[] { "benwmaddox/StasisLang" }, autoMergeMethod = "squash", maddoxExe = "MaddoxTasks.exe",
            codexExe = "codex", ghExe = "gh", repoRoot = root, worktreeRoot = Path.Combine(root, "worktrees")
        }));
    }

    private static void AddCapacityFillInterval(string path, string value)
    {
        var json = File.ReadAllText(path);
        json = System.Text.RegularExpressions.Regex.Replace(json, "\\\"capacityFillInterval\\\":\\\"[^\\\"]+\\\",?", string.Empty);
        json = json.Replace("{\"schemaVersion\":1,", $"{{\"schemaVersion\":1,\"capacityFillInterval\":\"{value}\",");
        File.WriteAllText(path, json);
    }

    private sealed class FakeClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maddox-worker-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
