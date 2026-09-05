using System.Reflection;
using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class WorkerHostMonitoringTests
{
    [Fact]
    public async Task GreenCi_RecordsReadyForReviewBeforeAutoMergeQuietPeriod()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false));

        await fixture.MonitorAsync();

        Assert.True(fixture.Job.ReadyForReviewRecorded);
        Assert.False(fixture.Job.ReviewWindow.Closed);
        Assert.Empty(fixture.GitHub.MergedUrls);
        Assert.Contains(fixture.Processes.Commands, command => command.IsStatus("ReadyForReview"));
        Assert.All(fixture.GitHub.Inspections, inspection => Assert.True(inspection.IncludeFeedback));
    }

    [Fact]
    public async Task GreenCi_RecordsReadyForReviewImmediatelyForManualRepository()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));

        await fixture.MonitorAsync();

        Assert.True(fixture.Job.ReadyForReviewRecorded);
        Assert.Empty(fixture.GitHub.MergedUrls);
        Assert.Contains(fixture.Processes.Commands, command => command.IsStatus("ReadyForReview"));
        Assert.Equal("Waiting for your PR decision", MonitoringDisplay.Describe(fixture.Job, fixture.Clock.UtcNow, fixture.QuietPeriod, false));
    }

    [Fact]
    public async Task AutoMerge_WaitsForQuietPeriodAfterReadyForReview()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false), Snapshot(false));

        await fixture.MonitorAsync();
        fixture.Clock.Advance(fixture.QuietPeriod);
        await fixture.MonitorAsync();

        Assert.Single(fixture.GitHub.MergedUrls);
        Assert.Single(fixture.Processes.Commands, command => command.IsStatus("ReadyForReview"));
        Assert.True(fixture.Job.ReviewWindow.Closed);
        Assert.Equal("Ready to auto-merge", MonitoringDisplay.Describe(fixture.Job, fixture.Clock.UtcNow, fixture.QuietPeriod, true));
    }

    [Fact]
    public async Task FailedOrPendingCi_PreventsReadyForReviewAndAutoMerge()
    {
        var failed = new CheckState("build", "FAILURE", "fail", "");
        using (var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false, [failed])))
        {
            await fixture.MonitorAsync();

            Assert.False(fixture.Job.ReadyForReviewRecorded);
            Assert.Empty(fixture.GitHub.MergedUrls);
        }

        var pending = new CheckState("build", "IN_PROGRESS", "pending", "");
        using (var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false, [pending])))
        {
            await fixture.MonitorAsync();

            Assert.False(fixture.Job.ReadyForReviewRecorded);
            Assert.Empty(fixture.GitHub.MergedUrls);
        }
    }

    [Fact]
    public async Task ActionableFeedback_PreventsReadyForReviewAndAutoMerge()
    {
        var feedback = new ReviewFeedback("thread-1", "comment-1", 1, "Please fix this", "https://github.com/example/Repo/pull/1#discussion_r1");
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false, [], [feedback]));

        await fixture.MonitorAsync();

        Assert.False(fixture.Job.ReadyForReviewRecorded);
        Assert.Single(fixture.Job.PendingFeedback);
        Assert.Empty(fixture.GitHub.MergedUrls);
        Assert.All(fixture.GitHub.Inspections, inspection => Assert.True(inspection.IncludeFeedback));
    }

    [Fact]
    public async Task ManualRepository_ContinuesInspectingFeedbackAfterLegacyClosedWindow()
    {
        var feedback = new ReviewFeedback("thread-1", "comment-1", 1, "Please fix this", "https://github.com/example/Repo/pull/1#discussion_r1");
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false, [], [feedback]));
        fixture.Job.ReviewWindow.GreenSinceUtc = fixture.Clock.UtcNow - fixture.QuietPeriod;
        fixture.Job.ReviewWindow.Closed = true;

        await fixture.MonitorAsync();

        Assert.Single(fixture.Job.PendingFeedback);
        Assert.False(fixture.Job.ReadyForReviewRecorded);
        Assert.Single(fixture.GitHub.Inspections);
        Assert.True(fixture.GitHub.Inspections[0].IncludeFeedback);
        Assert.False(fixture.Job.ReviewWindow.Closed);
    }

    private static PullRequestSnapshot Snapshot(bool merged, IReadOnlyList<CheckState>? checks = null, IReadOnlyList<ReviewFeedback>? feedback = null)
        => new(merged, checks ?? [new CheckState("build", "SUCCESS", "pass", "")], feedback ?? []);

    private sealed class HostFixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();

        private HostFixture(bool autoMergeAllowed, params PullRequestSnapshot[] snapshots)
        {
            var configPath = Path.Combine(directory.Path, "worker.json");
            var statePath = Path.Combine(directory.Path, "state");
            Directory.CreateDirectory(statePath);
            File.WriteAllText(Path.Combine(directory.Path, "worker-prompt.md"), "prompt");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                claimInterval = "00:15:00",
                researchCooldown = "14.00:00:00",
                maxConcurrentCodexProcesses = 1,
                prPollInterval = "00:01:00",
                clarificationTimeout = "00:10:00",
                promptFile = "worker-prompt.md",
                model = "model",
                reasoningEffort = "medium",
                repairMaxAttempts = 3,
                repairMaxElapsed = "02:00:00",
                reviewQuietPeriod = "00:30:00",
                blockedDisplayDuration = "00:10:00",
                ignoredChecks = Array.Empty<string>(),
                autoMergeRepositories = autoMergeAllowed ? new[] { "example/Repo" } : Array.Empty<string>(),
                autoMergeMethod = "squash",
                maddoxExe = "MaddoxTasks.exe",
                codexExe = "codex",
                ghExe = "gh",
                repoRoot = directory.Path,
                worktreeRoot = Path.Combine(directory.Path, "worktrees")
            }));

            Clock = new MutableClock(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));
            QuietPeriod = TimeSpan.FromMinutes(30);
            Processes = new FakeProcessRunner();
            GitHub = new FakeGitHubClient(snapshots);
            var job = new Job
            {
                Task = new TaskDto(1, Guid.NewGuid().ToString(), "Task", "Description", ["Repo"]),
                Prompt = "prompt",
                Model = "model",
                Effort = "medium",
                Phase = JobPhases.Monitoring,
                StartedUtc = Clock.UtcNow,
                PhaseChangedUtc = Clock.UtcNow,
                Workspaces = [new Workspace("Repo", Path.Combine(directory.Path, "worktree"), "codex/task-1-fix", "https://github.com/example/Repo.git")],
                PullRequests = [new PullRequestState("https://github.com/example/Repo/pull/1", "Repo")]
            };
            new Journal { Jobs = [job] }.Save(Path.Combine(statePath, "worker-journal.json"));

            Host = new WorkerHost(configPath, statePath, Clock, Processes, new NullLog(), GitHub);
            var journal = (Journal)typeof(WorkerHost).GetField("journal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            Job = Assert.Single(journal.Jobs);
        }

        public WorkerHost Host { get; }
        public MutableClock Clock { get; }
        public FakeProcessRunner Processes { get; }
        public FakeGitHubClient GitHub { get; }
        public Job Job { get; }
        public TimeSpan QuietPeriod { get; }

        public static HostFixture Create(bool autoMergeAllowed, params PullRequestSnapshot[] snapshots)
            => new(autoMergeAllowed, snapshots);

        public async Task MonitorAsync()
        {
            var method = typeof(WorkerHost).GetMethod("MonitorJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Host, [Job, CancellationToken.None])!;
        }

        public void Dispose() => directory.Dispose();
    }

    private sealed class FakeGitHubClient(params PullRequestSnapshot[] snapshots) : IGitHubClient
    {
        private readonly Queue<PullRequestSnapshot> remaining = new(snapshots);
        private PullRequestSnapshot last = snapshots.LastOrDefault() ?? Snapshot(false);

        public List<(string Url, bool IncludeFeedback)> Inspections { get; } = [];
        public List<string> MergedUrls { get; } = [];

        public Task<PullRequestSnapshot> InspectAsync(string pullRequestUrl, bool includeFeedback, CancellationToken cancellationToken)
        {
            Inspections.Add((pullRequestUrl, includeFeedback));
            if (remaining.Count > 0) last = remaining.Dequeue();
            return Task.FromResult(last);
        }

        public Task ReplyAsync(string pullRequestUrl, ReviewFeedback feedback, string replyBody, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResolveAsync(string pullRequestUrl, string threadId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MergeAsync(string pullRequestUrl, CancellationToken cancellationToken) { MergedUrls.Add(pullRequestUrl); return Task.CompletedTask; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<CommandCall> Commands { get; } = [];

        public Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null)
        {
            var call = new CommandCall(executable, arguments.ToArray());
            Commands.Add(call);
            return Task.FromResult(call.Arguments.Contains("command", StringComparer.Ordinal)
                ? new ExecResult(0, "{\"success\":true}", string.Empty)
                : new ExecResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record CommandCall(string Executable, string[] Arguments)
    {
        public bool IsStatus(string status) => Arguments.Contains("command", StringComparer.Ordinal) && Arguments.Any(argument => argument.Contains($"\"newStatus\":\"{status}\"", StringComparison.Ordinal));
    }

    private sealed class MutableClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; private set; } = now;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Advance(TimeSpan amount) => UtcNow += amount;
    }

    private sealed class NullLog : IRollingLog
    {
        public void Write(string level, string message, object? data = null) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maddox-worker-monitor-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
