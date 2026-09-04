using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class WorkerPolicyTests
{
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
        Assert.Equal(["Summary: Implemented the fix", "Repositories: MaddoxTasks (changed)"], result);
        Assert.DoesNotContain(result, line => line.Contains('{'));

        var clarification = DashboardFormatter.LatestLines("""{"repositories":["StasisLang"],"rationale":"The parser lives here","confidence":0.9,"ambiguous":false}""");
        Assert.Equal(["Repository clarification: identified", "Repositories: StasisLang", "Rationale: The parser lives here"], clarification);
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
        Assert.All(wrapped, line => Assert.StartsWith("  ", line));
        Assert.DoesNotContain(wrapped, line => line.Contains('{'));
    }

    [Fact]
    public void DashboardSegments_UseDarkTerminalPaletteBySemanticRole()
    {
        var job = CreateJob(JobPhases.Implementing);
        var header = DashboardSegments.JobHeader(job, "Implementing", TimeSpan.FromMinutes(2));
        Assert.Equal(ConsoleColor.Cyan, header[0].Color);
        Assert.Equal(ConsoleColor.Magenta, header[2].Color);
        Assert.Equal(ConsoleColor.Cyan, header[4].Color);
        Assert.Equal(ConsoleColor.Gray, header[5].Color);
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
        Assert.Equal("  Update · 9:07 PM ", segments[0].Text);
        Assert.Equal(ConsoleColor.Cyan, segments[0].Color);
        Assert.Equal("current status", segments[1].Text);
        Assert.Equal(ConsoleColor.White, segments[1].Color);
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
    public void CodexEventParser_ReadsThreadAndNestedAssistantMessage()
    {
        var started = CodexEventParser.Parse("{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}");
        var message = CodexEventParser.Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"working\"}}");
        Assert.Equal("thread-1", started.ThreadId);
        Assert.Equal("working", message.Text);
    }

    [Fact]
    public void ExtractResult_ReadsCodexJsonlAgentMessage()
    {
        var output = "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"completed\\\"}\"}}\n{\"type\":\"turn.completed\"}";
        using var result = JsonDocument.Parse(WorkerHost.ExtractResult(output));
        Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
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
        Assert.DoesNotContain("--sandbox", arguments);
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
    public void ProcessRunner_DecodesChildOutputAndErrorsAsUtf8()
    {
        var startInfo = ProcessRunner.CreateStartInfo("codex", @"D:\code");
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
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

    private static void WriteConfig(string path, string root, int cap, string model, string? blockedDisplayDuration = null)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, claimInterval = "00:15:00", maxConcurrentCodexProcesses = cap, prPollInterval = "00:01:00",
            clarificationTimeout = "00:10:00", promptFile = "worker-prompt.md", model, reasoningEffort = "medium",
            repairMaxAttempts = 3, repairMaxElapsed = "02:00:00", reviewQuietPeriod = "00:30:00", ignoredChecks = Array.Empty<string>(),
            blockedDisplayDuration,
            autoMergeRepositories = new[] { "benwmaddox/StasisLang" }, autoMergeMethod = "squash", maddoxExe = "MaddoxTasks.exe",
            codexExe = "codex", ghExe = "gh", repoRoot = root, worktreeRoot = Path.Combine(root, "worktrees")
        }));
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
