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
        WriteConfig(path, directory.Path, 2, "model-two");
        Assert.True(state.TryReload(path, out _));
        Assert.Equal(2, state.Current.MaxConcurrentCodexProcesses);
        Assert.Equal("model-one", job.Model);

        File.WriteAllText(path, "{\"schemaVersion\":99}");
        Assert.False(state.TryReload(path, out var error));
        Assert.NotNull(error);
        Assert.Equal("model-two", state.Current.Model);
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

    private static Job CreateJob(string phase = JobPhases.Claimed, DateTime? started = null) => new()
    {
        Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", ["Repo"]),
        Prompt = "prompt", Model = "model", Effort = "medium", Phase = phase, StartedUtc = started ?? DateTime.UnixEpoch
    };

    private static Job WithSnapshot(Job job, string model) { job.Model = model; return job; }

    private static void WriteConfig(string path, string root, int cap, string model)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, claimInterval = "00:15:00", maxConcurrentCodexProcesses = cap, prPollInterval = "00:01:00",
            clarificationTimeout = "00:10:00", promptFile = "worker-prompt.md", model, reasoningEffort = "medium",
            repairMaxAttempts = 3, repairMaxElapsed = "02:00:00", reviewQuietPeriod = "00:30:00", ignoredChecks = Array.Empty<string>(),
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
