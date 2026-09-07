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
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.TransientWorker, "same failure", now, config, out var firstDelay));
        job.Phase = JobPhases.RetryWaiting;
        Assert.Equal(TimeSpan.FromMinutes(1), firstDelay);
        Assert.True(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.TransientWorker, "same failure", now.AddMinutes(1), config, out var secondDelay));
        Assert.Equal(TimeSpan.FromMinutes(2), secondDelay);
        Assert.False(WorkerRetryPolicy.TrySchedule(job, WorkerBlockerKinds.TransientWorker, "same failure", now.AddMinutes(3), config, out _));
        Assert.Equal(2, job.WorkerRetryAttempts);
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
    public void RecoveryPlanner_DoesNotRequeueRetryWithoutCompleteMetadata()
    {
        var invalid = new Job
        {
            Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", []),
            Prompt = "prompt", Model = "model", Effort = "medium", Phase = JobPhases.RetryWaiting,
            StartedUtc = DateTime.UnixEpoch, PhaseChangedUtc = DateTime.UnixEpoch
        };

        Assert.Empty(RecoveryPlanner.JobsToRequeue(new Journal { Jobs = [invalid] }));
    }

    [Fact]
    public void ResearchResultSchema_RequiresEveryDeclaredObjectProperty()
    {
        var schema = (string?)typeof(WorkerHost)
            .GetField("ResearchResultSchema", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue();

        using var document = JsonDocument.Parse(schema!);
        AssertStrictObjects(document.RootElement);
    }

    private static string Result(string status, bool workComplete, string kind, string summary) => JsonSerializer.Serialize(new
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
        blocker = new { kind, summary, evidence = new[] { "evidence" } }
    });

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
