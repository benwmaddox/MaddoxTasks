using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class GitHubInspectionTests
{
    private const string PullRequestUrl = "https://github.com/acme/project/pull/42";
    private const string PullRequestView = "{\"state\":\"OPEN\",\"mergedAt\":null,\"mergeable\":\"MERGEABLE\",\"mergeStateStatus\":\"CLEAN\",\"headRefOid\":\"abc123\",\"baseRefName\":\"main\"}";

    [Theory]
    [InlineData("", "", "gh pr checks returned no JSON output")]
    [InlineData("not-json", "", "malformed JSON")]
    [InlineData("", "authentication required", "authentication required")]
    [InlineData("", "network connection failed", "network connection failed")]
    public async Task FailedOrMalformedChecks_NeverBecomeGreen(string checksOutput, string checksError, string expectedError)
    {
        using var fixture = new Fixture(new ExecResult(0, PullRequestView, ""), new ExecResult(1, checksOutput, checksError));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.False(snapshot.InspectionComplete);
        Assert.False(snapshot.IsGreen([]));
        Assert.Contains(expectedError, snapshot.InspectionError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthFailureWithEmptyJson_IsNotAcceptedAsNoChecks()
    {
        using var fixture = new Fixture(new ExecResult(0, PullRequestView, ""), new ExecResult(1, "[]", "authentication required"));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.False(snapshot.InspectionComplete);
        Assert.False(snapshot.IsGreen([]));
    }

    [Fact]
    public async Task ExplicitNoChecksResponse_IsCompleteAndCanBeGreen()
    {
        using var fixture = new Fixture(new ExecResult(0, PullRequestView, ""), new ExecResult(0, "no checks reported on the default branch", ""));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.True(snapshot.InspectionComplete);
        Assert.Empty(snapshot.Checks);
        Assert.True(snapshot.IsGreen([]));
    }

    [Fact]
    public async Task EmptyJsonArray_IsACompleteNoChecksInspection()
    {
        using var fixture = new Fixture(new ExecResult(0, PullRequestView, ""), new ExecResult(0, "[]", ""));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.True(snapshot.InspectionComplete);
        Assert.Empty(snapshot.Checks);
        Assert.True(snapshot.IsGreen([]));
    }

    [Fact]
    public async Task PendingChecksWithGhPendingExitCode_AreCompleteButNotGreen()
    {
        const string checks = "[{\"name\":\"build\",\"state\":\"IN_PROGRESS\",\"bucket\":\"pending\",\"link\":\"\"}]";
        using var fixture = new Fixture(new ExecResult(0, PullRequestView, ""), new ExecResult(8, checks, ""));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.True(snapshot.InspectionComplete);
        Assert.False(snapshot.IsGreen([]));
        Assert.Equal(CheckClassification.Pending, Assert.Single(snapshot.Checks).Classification);
    }

    [Fact]
    public async Task ClosedUnmergedPullRequest_IsNeverReviewReady()
    {
        const string closedView = "{\"state\":\"CLOSED\",\"mergedAt\":null,\"mergeable\":\"MERGEABLE\",\"mergeStateStatus\":\"CLEAN\",\"headRefOid\":\"abc123\",\"baseRefName\":\"main\"}";
        using var fixture = new Fixture(new ExecResult(0, closedView, ""), new ExecResult(0, "[]", ""));

        var snapshot = await fixture.Client.InspectAsync(PullRequestUrl, false, CancellationToken.None);

        Assert.False(snapshot.Merged);
        Assert.False(snapshot.IsOpen);
        Assert.False(snapshot.Open);
        Assert.True(snapshot.InspectionComplete);
        Assert.False(snapshot.IsGreen([]));
    }

    [Fact]
    public void CheckStates_AreClassifiedForRepairRerunApprovalAndPending()
    {
        Assert.Equal(CheckClassification.CodeRepair, new CheckState("build", "FAILURE", "fail", "").Classification);
        Assert.Equal(CheckClassification.TransientRerun, new CheckState("build", "CANCELLED", "fail", "").Classification);
        Assert.Equal(CheckClassification.TransientRerun, new CheckState("build", "TIMED_OUT", "fail", "").Classification);
        Assert.Equal(CheckClassification.TransientRerun, new CheckState("build", "STARTUP_FAILURE", "fail", "").Classification);
        Assert.Equal(CheckClassification.HumanGate, new CheckState("deploy", "ACTION_REQUIRED", "fail", "").Classification);
        Assert.Equal(CheckClassification.Pending, new CheckState("build", "IN_PROGRESS", "pending", "").Classification);
        Assert.Equal(CheckClassification.Passing, new CheckState("build", "SUCCESS", "pass", "").Classification);
        Assert.Equal(CheckClassification.Unknown, new CheckState("build", "SOMETHING_NEW", "", "").Classification);
    }

    [Fact]
    public void SnapshotClassifiesChecksIntoWorkerActions()
    {
        var checks = new CheckState[]
        {
            new("build", "FAILURE", "fail", ""),
            new("retry", "TIMED_OUT", "fail", ""),
            new("approval", "ACTION_REQUIRED", "fail", ""),
            new("pending", "QUEUED", "pending", "")
        };
        var snapshot = new PullRequestSnapshot(false, checks, []);

        Assert.Single(snapshot.CodeRepairs([]));
        Assert.Single(snapshot.TransientFailures([]));
        Assert.Single(snapshot.HumanGates([]));
        Assert.Equal(3, snapshot.Failures([]).Count);
        Assert.False(snapshot.IsGreen([]));
    }

    [Fact]
    public async Task Rerun_UsesRunIdAndRepositoryFromActionsUrl()
    {
        using var fixture = new Fixture();
        const string runUrl = "https://github.com/acme/project/actions/runs/987654321/job/123456";

        await fixture.Client.RerunAsync(runUrl, CancellationToken.None);

        var call = Assert.Single(fixture.Processes.Calls);
        Assert.Equal(["run", "rerun", "987654321", "--repo", "acme/project"], call.Arguments);
    }

    [Fact]
    public async Task RerunWorkflowAlias_UsesSameBoundedCommand()
    {
        using var fixture = new Fixture();

        await fixture.Client.RerunWorkflowAsync("https://github.com/acme/project/actions/runs/987654321", CancellationToken.None);

        var call = Assert.Single(fixture.Processes.Calls);
        Assert.Equal("rerun", call.Arguments[1]);
    }

    [Fact]
    public async Task Rerun_RejectsNonActionsUrlBeforeInvokingGh()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.RerunAsync(PullRequestUrl, CancellationToken.None));

        Assert.Empty(fixture.Processes.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();

        public Fixture(params ExecResult[] responses)
        {
            Processes = new FakeProcessRunner(responses);
            var configPath = Path.Combine(directory.Path, "worker.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                claimInterval = "00:15:00",
                maxConcurrentCodexProcesses = 1,
                prPollInterval = "00:01:00",
                clarificationTimeout = "00:10:00",
                promptFile = "prompt.md",
                model = "model",
                reasoningEffort = "medium",
                repairMaxAttempts = 3,
                repairMaxElapsed = "02:00:00",
                reviewQuietPeriod = "00:30:00",
                ignoredChecks = Array.Empty<string>(),
                autoMergeRepositories = Array.Empty<string>(),
                autoMergeMethod = "squash",
                maddoxExe = "MaddoxTasks.exe",
                codexExe = "codex",
                ghExe = "gh",
                repoRoot = directory.Path,
                worktreeRoot = Path.Combine(directory.Path, "worktrees")
            }));
            File.WriteAllText(Path.Combine(directory.Path, "prompt.md"), "prompt");
            Settings = WorkerConfig.Load(configPath);
            Client = new GitHubClient(Processes, () => Settings, new NullLog());
        }

        public FakeProcessRunner Processes { get; }
        private WorkerConfig Settings { get; }
        public GitHubClient Client { get; }

        public void Dispose() => directory.Dispose();
    }

    private sealed class FakeProcessRunner(params ExecResult[] responses) : IProcessRunner
    {
        private readonly Queue<ExecResult> remaining = new(responses);
        public List<Call> Calls { get; } = [];

        public Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            var call = new Call(executable, arguments.ToArray());
            Calls.Add(call);
            if (remaining.Count == 0) return Task.FromResult(new ExecResult(0, "", ""));
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private sealed record Call(string Executable, string[] Arguments);

    private sealed class NullLog : IRollingLog
    {
        public void Write(string level, string message, object? data = null) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maddox-worker-github-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
