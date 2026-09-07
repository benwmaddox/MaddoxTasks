using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public void WorkerResultPolicy_EnforcesClosedBlockersAndValidCombinations()
    {
        var review = WorkerResultPolicy.Parse(Result("noChanges", true, "humanReview", "Waiting for your review"));
        Assert.True(review.WorkComplete);
        Assert.Equal(WorkerBlockerKinds.HumanReview, review.Blocker.Kind);

        var external = WorkerResultPolicy.Parse(Result("blocked", true, "missingCredential", "GitHub authentication is required"));
        Assert.True(external.WorkComplete);

        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("completed", true, "workerRepairable", "retry")));
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("blocked", false, "none", "nothing")));
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("completed", false, "humanReview", "review")));
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("blocked", true, "humanReview", "review")));
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("blocked", false, "futureKind", "unknown")));
    }

    [Fact]
    public void WorkerResultPolicy_LoadsLegacyResultConservatively()
    {
        var completed = WorkerResultPolicy.Parse("{\"status\":\"completed\",\"summary\":\"old result\"}", allowLegacy: true);
        Assert.True(completed.IsLegacy);
        Assert.Equal(WorkerBlockerKinds.None, completed.Blocker.Kind);

        var blocked = WorkerResultPolicy.Parse("{\"status\":\"blocked\",\"summary\":\"old blocker\"}", allowLegacy: true);
        Assert.True(blocked.IsLegacy);
        Assert.Equal(WorkerBlockerKinds.UpstreamDependency, blocked.Blocker.Kind);
        Assert.False(blocked.Blocker.IsRetryable);
    }

    [Fact]
    public void WorkerRetryPolicy_BoundsSameFingerprintAndBacksOff()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "worker.json");
        File.WriteAllText(configPath, """
        {
          "schemaVersion":1,"claimInterval":"00:15:00","maxConcurrentCodexProcesses":1,
          "prPollInterval":"00:01:00","clarificationTimeout":"00:10:00","promptFile":"prompt",
          "model":"model","reasoningEffort":"medium","repairMaxAttempts":3,"repairMaxElapsed":"02:00:00",
          "reviewQuietPeriod":"00:30:00","ignoredChecks":[],"autoMergeRepositories":[],"autoMergeMethod":"squash",
          "maddoxExe":"MaddoxTasks.exe","codexExe":"codex","ghExe":"gh","repoRoot":"ROOT",
          "worktreeRoot":"WORK","workerRetryMaxAttempts":2,"workerRetryMaxElapsed":"01:00:00","workerRetryBaseDelay":"00:01:00"
        }
        """.Replace("ROOT", directory.Path.Replace("\\", "\\\\"), StringComparison.Ordinal).Replace("WORK", Path.Combine(directory.Path, "worktrees").Replace("\\", "\\\\"), StringComparison.Ordinal));
        var config = WorkerConfig.Load(configPath);
        var job = new Job
        {
            Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", ["Repo"]),
            Prompt = "prompt", Model = "model", Effort = "medium", Phase = JobPhases.RetryWaiting,
            StartedUtc = DateTime.UnixEpoch, PhaseChangedUtc = DateTime.UnixEpoch,
            Workspaces = [new Workspace("Repo", Path.Combine(directory.Path, "worktrees"), "codex/task-1", "origin")]
        };
        var now = DateTime.UnixEpoch;
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "same failure", now, config, out var firstDelay));
        job.Phase = JobPhases.RetryWaiting;
        Assert.Equal(TimeSpan.FromMinutes(1), firstDelay);
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "same failure", now.AddMinutes(1), config, out var secondDelay));
        Assert.Equal(TimeSpan.FromMinutes(2), secondDelay);
        Assert.False(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "same failure", now.AddMinutes(3), config, out _));
        Assert.Equal(2, job.WorkerRetryAttempts);
    }

    [Fact]
    public void WorkerRetryPolicy_NewRepairableFingerprintGetsFreshElapsedBudget()
    {
        using var directory = new TemporaryDirectory();
        var config = TestConfig(directory.Path, maxAttempts: 2, maxElapsed: TimeSpan.FromHours(1));
        var started = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        var job = CreateJob(JobPhases.RetryWaiting);

        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "first failure", started, config, out _));
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "different failure", started.AddHours(2), config, out _));
        Assert.Equal(1, job.WorkerRetryAttempts);
        Assert.Equal(started.AddHours(2), job.WorkerRetryStartedUtc);
    }

    [Fact]
    public void WorkerRetryPolicy_RetriesTransientFailuresIndefinitelyWithCappedDelay()
    {
        using var directory = new TemporaryDirectory();
        var config = TestConfig(directory.Path, maxAttempts: 1, maxElapsed: TimeSpan.FromMinutes(1), baseDelay: TimeSpan.FromMinutes(20));
        var job = CreateJob(JobPhases.RetryWaiting);
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.TransientWorker, "service temporarily unavailable", now, config, out var delay));
            Assert.InRange(delay, TimeSpan.FromMinutes(1), WorkerRetryPolicy.MaxRetryDelay);
            job.Phase = JobPhases.RetryWaiting;
            now = job.WorkerRetryNextUtc!.Value;
        }

        Assert.True(job.WorkerRetryAttempts >= 12);
        Assert.True(WorkerRetryPolicy.HasSafeMetadata(job));
    }

    [Fact]
    public void WorkerRetryPolicy_UsesFutureResetTimestampAndSurvivesJournalRestart()
    {
        using var directory = new TemporaryDirectory();
        var config = TestConfig(directory.Path, baseDelay: TimeSpan.FromMinutes(5));
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var reset = new DateTime(2026, 9, 10, 15, 14, 0, DateTimeKind.Utc);
        Assert.True(RetryTimestampPolicy.TryParseFuture("Try again at Sep 10th, 2026 3:14 PM", now, out var parsed));
        Assert.Equal(reset, parsed);

        var job = CreateJob(JobPhases.RetryWaiting);
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.TransientWorker, "usage limit reached", now, config, parsed, out _));
        Assert.Equal(reset, job.WorkerRetryNextUtc);
        Assert.Equal(reset, job.WorkerRetryResetUtc);

        var journalPath = Path.Combine(directory.Path, "journal.json");
        new Journal { Jobs = [job] }.Save(journalPath);
        var recovered = Journal.Load(journalPath).Jobs.Single();
        Assert.True(WorkerRetryPolicy.HasSafeMetadata(recovered));
        Assert.False(WorkerRetryPolicy.IsDue(recovered, reset.AddSeconds(-1)));
        Assert.True(WorkerRetryPolicy.IsDue(recovered, reset));
    }

    [Fact]
    public void WorkerRetryPolicy_SignalsFreshSessionForRepairableAndInternalThreadFailures()
    {
        Assert.True(WorkerRetryPolicy.RequiresFreshSession(WorkerBlockerKinds.WorkerRepairable, "structured result could not be recovered"));
        Assert.True(WorkerRetryPolicy.RequiresFreshSession(WorkerBlockerKinds.TransientWorker, "internal thread is no longer available"));
        Assert.False(WorkerRetryPolicy.RequiresFreshSession(WorkerBlockerKinds.TransientWorker, "network timeout"));

        using var directory = new TemporaryDirectory();
        var config = TestConfig(directory.Path);
        var job = CreateJob(JobPhases.RetryWaiting);
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.WorkerRepairable, "resume failed: invalid thread", DateTime.UtcNow, config, out _));
        Assert.True(job.WorkerRetryFreshSession);
        Assert.True(WorkerRetryPolicy.ShouldStartFreshSession(job));
    }

    [Fact]
    public void WorkerResultPolicy_RequiresEvidenceForEveryNonNoneBlocker()
    {
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.Parse(Result("blocked", false, "missingInput", "input is absent", [])));
        Assert.Throws<InvalidDataException>(() => WorkerResultPolicy.ValidateCombination(
            "blocked", false, new WorkerBlocker(WorkerBlockerKinds.UpstreamDependency, "dependency", [])));
        var none = WorkerResultPolicy.Parse(Result("completed", true, WorkerBlockerKinds.None, "done", []));
        Assert.Empty(none.Blocker.Evidence);

        var legacy = WorkerResultPolicy.Parse("{\"status\":\"blocked\",\"summary\":\"old blocker\"}", allowLegacy: true);
        Assert.NotEmpty(legacy.Blocker.Evidence);
    }

    [Fact]
    public void RepairRetryPolicy_ResetsBudgetWhenHeadOrPendingIdentityChanges()
    {
        using var directory = new TemporaryDirectory();
        var job = CreateJob(JobPhases.Monitoring);
        var url = "https://github.com/example/Repo/pull/1";
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var checks = new[] { new CheckState("build", "FAILURE", "fail", "https://check/1", url) };
        var feedback = new[] { new ReviewFeedback("thread-1", "comment-1", 1, "please fix", url) };

        Assert.True(RepairRetryPolicy.BeginGeneration(job, url, "head-a", checks, feedback, now));
        job.RepairAttemptsByPullRequest[url] = 2;
        Assert.False(RepairRetryPolicy.BeginGeneration(job, url, "head-a", checks, feedback, now.AddMinutes(1)));
        Assert.Equal(2, job.RepairAttemptsByPullRequest[url]);

        var sameRunDifferentState = new[] { checks[0] with { State = "TIMED_OUT", Bucket = "fail" } };
        Assert.False(RepairRetryPolicy.BeginGeneration(job, url, "head-a", sameRunDifferentState, feedback, now.AddMinutes(1)));
        Assert.Equal(2, job.RepairAttemptsByPullRequest[url]);

        Assert.True(RepairRetryPolicy.BeginGeneration(job, url, "head-b", checks, feedback, now.AddMinutes(2)));
        Assert.Equal(0, job.RepairAttemptsByPullRequest[url]);
        Assert.Equal(now.AddMinutes(2), job.RepairStartedUtcByPullRequest[url]);
        job.RepairAttemptsByPullRequest[url] = 2;

        var newFeedback = new[] { feedback[0] with { CommentNodeId = "comment-2" } };
        Assert.True(RepairRetryPolicy.BeginGeneration(job, url, "head-b", checks, newFeedback, now.AddMinutes(3)));
        Assert.Equal(0, job.RepairAttemptsByPullRequest[url]);
    }

    [Fact]
    public void WorkerFailurePolicy_PrefersPermanentCredentialAndHardwareGates()
    {
        var credential = WorkerFailurePolicy.Classify(new InvalidOperationException("GitHub rate limit response: authentication token missing"));
        Assert.Equal(WorkerBlockerKinds.MissingCredential, credential.Kind);
        Assert.False(credential.Retryable);

        var hardware = WorkerFailurePolicy.Classify(new InvalidOperationException("CUDA device not found"));
        Assert.Equal(WorkerBlockerKinds.MissingHardware, hardware.Kind);
        Assert.False(hardware.Retryable);

        var transient = WorkerFailurePolicy.Classify(new TimeoutException("temporary network timeout"));
        Assert.Equal(WorkerBlockerKinds.TransientWorker, transient.Kind);
        Assert.True(transient.Retryable);

        var unknown = WorkerFailurePolicy.Classify(new InvalidOperationException("Unexpected worker pipeline failure"));
        Assert.Equal(WorkerBlockerKinds.WorkerRepairable, unknown.Kind);
        Assert.True(unknown.Retryable);

        var filePermission = WorkerFailurePolicy.Classify(new IOException("Permission denied writing a generated file"));
        Assert.Equal(WorkerBlockerKinds.WorkerRepairable, filePermission.Kind);
    }

    [Fact]
    public void RecoveryPlanner_ReconstructsIncompleteRetryMetadataConservatively()
    {
        using var directory = new TemporaryDirectory();
        var config = TestConfig(directory.Path, baseDelay: TimeSpan.FromMinutes(2));
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var invalid = new Job
        {
            Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", []),
            Prompt = "prompt", Model = "model", Effort = "medium", Phase = JobPhases.RetryWaiting,
            StartedUtc = now.AddHours(-1), PhaseChangedUtc = now.AddMinutes(-2),
            LastBlockerKind = WorkerBlockerKinds.TransientWorker,
            LastBlockerSummary = "rate limit; try again later"
        };

        Assert.True(WorkerRetryPolicy.TryReconstruct(invalid, now, config, out var reconstruction));
        Assert.Equal(RecoveryMode.Initial, reconstruction.Mode);
        Assert.True(WorkerRetryPolicy.HasSafeMetadata(invalid));
        Assert.Equal(JobPhases.RetryWaiting, invalid.Phase);
        Assert.Contains(invalid, RecoveryPlanner.JobsToRequeue(new Journal { Jobs = [invalid] }, invalid.WorkerRetryNextUtc));

        invalid.ThreadId = "thread-1";
        invalid.PendingCheckFailures.Add(new CheckState("build", "FAILURE", "fail", "link"));
        WorkerRetryPolicy.Clear(invalid);
        invalid.Phase = JobPhases.RetryWaiting;
        Assert.True(WorkerRetryPolicy.TryReconstruct(invalid, now, config, out reconstruction));
        Assert.Equal(RecoveryMode.ResumeRepair, reconstruction.Mode);
    }

    [Fact]
    public void RecoveryPlanner_RequeuesDurableStatusSynchronization()
    {
        var job = CreateJob(JobPhases.StatusSyncPending);
        var journal = new Journal { Jobs = [job] };
        Assert.Contains(job, RecoveryPlanner.JobsToRequeue(journal));
        Assert.Equal(RecoveryMode.SyncBlocked, RecoveryPlanner.ModeFor(job));
    }

    [Fact]
    public void CodexTerminalEventTracker_RecognizesOnlyCompleteKnownContracts()
    {
        var arbitrary = new CodexTerminalEventTracker();
        Assert.False(arbitrary.Observe("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"completed\\\",\\\"summary\\\":\\\"done\\\"}\"}}"));
        Assert.False(arbitrary.Observe("{\"type\":\"turn.completed\"}"));

        var worker = new CodexTerminalEventTracker();
        Assert.False(worker.Observe("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"completed\\\",\\\"summary\\\":\\\"done\\\",\\\"workComplete\\\":true,\\\"blocker\\\":{\\\"kind\\\":\\\"none\\\",\\\"summary\\\":\\\"done\\\",\\\"evidence\\\":[]},\\\"repositories\\\":[] }\"}}"));
        Assert.True(worker.Observe("{\"type\":\"turn.completed\"}"));

        var research = new CodexTerminalEventTracker();
        Assert.False(research.Observe("{\"type\":\"event_msg\",\"payload\":{\"result\":\"{\\\"outcome\\\":\\\"stillBlocked\\\",\\\"summary\\\":\\\"needs input\\\",\\\"findings\\\":[],\\\"mutations\\\":[] }\"}}"));
        Assert.True(research.Observe("{\"type\":\"task_complete\"}"));

        var clarification = new CodexTerminalEventTracker();
        Assert.False(clarification.Observe("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"action\\\":\\\"assign\\\",\\\"repositories\\\":[],\\\"children\\\":[],\\\"rationale\\\":\\\"r\\\",\\\"confidence\\\":0.9,\\\"ambiguous\\\":false}\"}}"));
        Assert.True(clarification.Observe("{\"type\":\"turn.completed\"}"));
    }

    [Theory]
    [InlineData("ResearchResultSchema")]
    [InlineData("ResultSchema")]
    public void ResultSchemas_RequireEveryDeclaredObjectProperty(string fieldName)
    {
        var schema = (string?)typeof(WorkerHost)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue();

        using var document = JsonDocument.Parse(schema!);
        AssertStrictObjects(document.RootElement);
    }

    private static string Result(string status, bool workComplete, string kind, string summary, string[]? evidence = null) => JsonSerializer.Serialize(new
    {
        status,
        summary = "result",
        validationEvidence = Array.Empty<string>(),
        repositories = Array.Empty<object>(),
        commitMessage = "commit",
        prTitle = "title",
        prBody = "body",
        checkDispositions = Array.Empty<object>(),
        threadDispositions = Array.Empty<object>(),
        workComplete,
        blocker = new { kind, summary, evidence = evidence ?? ["evidence"], retryAtUtc = (string?)null }
    });

    private static Job CreateJob(string phase) => new()
    {
        Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", ["Repo"]),
        Prompt = "prompt",
        Model = "model",
        Effort = "medium",
        Phase = phase,
        StartedUtc = DateTime.UnixEpoch,
        PhaseChangedUtc = DateTime.UnixEpoch,
        Workspaces = [new Workspace("Repo", Path.Combine(Path.GetTempPath(), "maddox-worker-test-worktree"), "codex/task-1", "origin")]
    };

    private static WorkerConfig TestConfig(string root, int maxAttempts = 3, TimeSpan? maxElapsed = null, TimeSpan? baseDelay = null)
        => new(
            SchemaVersion: 1,
            ClaimInterval: TimeSpan.FromMinutes(15),
            MaxConcurrentCodexProcesses: 1,
            PrPollInterval: TimeSpan.FromMinutes(1),
            ClarificationTimeout: TimeSpan.FromMinutes(10),
            PromptFile: "prompt",
            Model: "model",
            ReasoningEffort: "medium",
            RepairMaxAttempts: 3,
            RepairMaxElapsed: TimeSpan.FromHours(2),
            ReviewQuietPeriod: TimeSpan.FromMinutes(30),
            IgnoredChecks: [],
            AutoMergeRepositories: [],
            AutoMergeMethod: "squash",
            MaddoxExe: "MaddoxTasks.exe",
            CodexExe: "codex",
            GhExe: "gh",
            RepoRoot: root,
            WorktreeRoot: Path.Combine(root, "worktrees"),
            WorkerRetryMaxAttempts: maxAttempts,
            WorkerRetryMaxElapsed: maxElapsed ?? TimeSpan.FromHours(1),
            WorkerRetryBaseDelay: baseDelay ?? TimeSpan.FromMinutes(5));

    private static void AssertStrictObjects(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal), required);
            foreach (var property in properties.EnumerateObject()) AssertStrictObjects(property.Value);
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            AssertStrictObjects(items);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maddox-worker-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
