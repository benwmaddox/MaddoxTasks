using System.Reflection;
using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class WorkerHostMonitoringTests
{
    [Fact]
    public async Task ActiveUsageLimitCooldown_SkipsResearchFollowupsAndFreshClaims()
    {
        using var fixture = HostFixture.CreateThrottled(new DateTime(2026, 9, 10, 19, 14, 0, DateTimeKind.Utc));

        var outcome = await fixture.TickAsync();

        Assert.Equal(FreshClaimOutcome.Unavailable, outcome);
        Assert.DoesNotContain(fixture.Processes.Commands, command => command.Executable == "codex");
        Assert.DoesNotContain(fixture.Processes.Commands, command => command.Arguments.Contains("claim"));
        Assert.DoesNotContain(fixture.Processes.Commands, command => command.Arguments.Contains("research-claim"));
    }

    [Fact]
    public async Task ActiveUsageLimitCooldown_StillPublishesPendingBlockedStatus()
    {
        using var fixture = HostFixture.CreateThrottled(new DateTime(2026, 9, 10, 19, 14, 0, DateTimeKind.Utc));
        fixture.Job.Phase = JobPhases.StatusSyncPending;
        fixture.Job.BlockReason = "missingInput: exact test fixture is absent";

        var outcome = await fixture.TickAsync();

        Assert.Equal(FreshClaimOutcome.Unavailable, outcome);
        Assert.Equal(JobPhases.Blocked, fixture.Job.Phase);
        Assert.Contains(fixture.Processes.Commands, command => command.IsStatus("Blocked"));
        Assert.DoesNotContain(fixture.Processes.Commands, command => command.Executable == "codex");
    }

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
    public async Task MergedPullRequest_EnsuresTaskIsDoneBeforeCleanup()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(true));

        await fixture.MonitorAsync();

        Assert.Equal(JobPhases.Done, fixture.Job.Phase);
        Assert.Contains(fixture.Processes.Commands, command => command.IsStatus("Done"));
    }

    [Fact]
    public async Task MergedPullRequest_AcceptsAlreadyDoneStatusAfterReconciliation()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(true));
        fixture.Processes.Responder = call => call.IsStatus("Done")
            ? new ExecResult(0, "{\"success\":false,\"message\":\"Issue task already has status 'Done'.\",\"status\":null}", "")
            : call.Arguments.Contains("command", StringComparer.Ordinal)
                ? new ExecResult(0, "{\"success\":true}", "")
                : new ExecResult(0, "", "");

        await fixture.MonitorAsync();

        Assert.Equal(JobPhases.Done, fixture.Job.Phase);
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
    public async Task ConflictingPullRequest_QueuesRevisionScopedRepairAndDoesNotAutoMerge()
    {
        var conflict = Snapshot(false) with { Mergeable = "CONFLICTING", MergeStateStatus = "DIRTY", HeadOid = "abc123", BaseRefName = "release/next" };
        using var fixture = HostFixture.Create(autoMergeAllowed: true, conflict);

        await fixture.MonitorAsync();

        var failure = Assert.Single(fixture.Job.PendingCheckFailures);
        Assert.Equal("pull-request-mergeability", failure.Name);
        Assert.Equal("https://github.com/example/Repo/pull/1#head-abc123", failure.Link);
        Assert.Equal("release/next", failure.BaseRefName);
        Assert.Contains("Merge the latest base branch", failure.Details, StringComparison.Ordinal);
        Assert.False(fixture.Job.ReadyForReviewRecorded);
        Assert.Empty(fixture.GitHub.MergedUrls);
    }

    [Fact]
    public async Task PersistedProcessedConflict_RearmsRepairWhenNoFailureIsPending()
    {
        var conflict = Snapshot(false) with { Mergeable = "CONFLICTING", MergeStateStatus = "DIRTY", HeadOid = "abc123", BaseRefName = "main" };
        using var fixture = HostFixture.Create(autoMergeAllowed: true, conflict);
        fixture.Job.ProcessedCheckIds.Add("pull-request-mergeability|CONFLICTING|https://github.com/example/Repo/pull/1#head-abc123");

        await fixture.MonitorAsync();

        var failure = Assert.Single(fixture.Job.PendingCheckFailures);
        Assert.Equal("pull-request-mergeability", failure.Name);
        Assert.Equal("abc123", fixture.Job.PullRequests[0].HeadOid);
    }

    [Fact]
    public async Task IncompleteInspectionAndClosedPullRequest_CannotBecomeReadyForReview()
    {
        using (var incomplete = HostFixture.Create(autoMergeAllowed: false, Snapshot(false) with { InspectionComplete = false, InspectionError = "network" }))
        {
            await incomplete.MonitorAsync();
            Assert.False(incomplete.Job.ReadyForReviewRecorded);
        }

        using (var closed = HostFixture.Create(autoMergeAllowed: false, Snapshot(false) with { State = "CLOSED" }))
        {
            await closed.MonitorAsync();
            Assert.False(closed.Job.ReadyForReviewRecorded);
            Assert.Equal(JobPhases.Blocked, closed.Job.Phase);
            Assert.Contains(closed.Processes.Commands, command => command.IsStatus("Blocked"));
        }
    }

    [Fact]
    public async Task TimedOutCheck_IsRerunWithoutCodexRepair()
    {
        var timedOut = new CheckState("build", "TIMED_OUT", "fail", "https://github.com/example/Repo/actions/runs/123");
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false, [timedOut]));

        await fixture.MonitorAsync();

        Assert.Equal([timedOut.Link], fixture.GitHub.RerunUrls);
        Assert.Empty(fixture.Job.PendingCheckFailures);
        Assert.False(fixture.Job.ReadyForReviewRecorded);
    }

    [Fact]
    public async Task ActionRequiredCheck_MovesCompletedPullRequestToReviewWithoutAutoMerge()
    {
        var approval = new CheckState("deploy", "ACTION_REQUIRED", "fail", "https://github.com/example/Repo/actions/runs/456");
        using var fixture = HostFixture.Create(autoMergeAllowed: true, Snapshot(false, [approval]));

        await fixture.MonitorAsync();

        Assert.True(fixture.Job.ReadyForReviewRecorded);
        Assert.Empty(fixture.Job.PendingCheckFailures);
        Assert.Empty(fixture.GitHub.MergedUrls);
        Assert.Contains(fixture.Processes.Commands, command => command.IsStatus("ReadyForReview"));
    }

    [Fact]
    public async Task GitHubInspection_CapturesExactPullRequestBase()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        fixture.Processes.Responder = call => call.Arguments.Contains("view", StringComparer.Ordinal)
            ? new ExecResult(0, "{\"mergedAt\":null,\"mergeable\":\"CONFLICTING\",\"mergeStateStatus\":\"DIRTY\",\"headRefOid\":\"abc123\",\"baseRefName\":\"release/next\"}", "")
            : call.Arguments.Contains("checks", StringComparer.Ordinal)
                ? new ExecResult(0, "[]", "")
                : new ExecResult(0, "", "");
        var client = new GitHubClient(fixture.Processes, () => fixture.Settings, new NullLog());

        var snapshot = await client.InspectAsync("https://github.com/example/Repo/pull/1", false, CancellationToken.None);

        Assert.Equal("release/next", snapshot.BaseRefName);
        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.LastOrDefault() == "state,mergedAt,mergeable,mergeStateStatus,headRefOid,baseRefName");
    }

    [Fact]
    public async Task MergeabilityRepair_FetchesAndPreparesExactBaseBeforeCodex()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        fixture.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", fixture.Job.PullRequests[0].Url, "details", "release/next"));

        await fixture.PrepareMergeabilityAsync();

        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.SequenceEqual(["fetch", "origin"]));
        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.SequenceEqual(["merge", "--no-commit", "--no-ff", "--", "origin/release/next"]));
        Assert.Contains("prepared the exact pull request base origin/release/next", fixture.Job.PendingCheckFailures[0].Details);
    }

    [Fact]
    public async Task MergeabilityRepair_ResumesExactMergeAndReplacesStaleMergeBase()
    {
        using var resumed = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        resumed.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", resumed.Job.PullRequests[0].Url, "details", "main"));
        resumed.Processes.Responder = call => call.Arguments.SequenceEqual(["rev-parse", "--verify", "-q", "MERGE_HEAD"])
            ? new ExecResult(0, "base123\n", "")
            : call.Arguments.SequenceEqual(["rev-parse", "origin/main"])
                ? new ExecResult(0, "base123\n", "")
                : new ExecResult(0, "", "");

        await resumed.PrepareMergeabilityAsync();

        Assert.DoesNotContain(resumed.Processes.Commands, call => call.Arguments.FirstOrDefault() == "merge");
        Assert.Contains("all conflicts are resolved and staged", resumed.Job.PendingCheckFailures[0].Details);

        using var mismatched = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        mismatched.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", mismatched.Job.PullRequests[0].Url, "details", "main"));
        mismatched.Processes.Responder = call => call.Arguments.SequenceEqual(["rev-parse", "--verify", "-q", "MERGE_HEAD"])
            ? new ExecResult(0, "stale-base\n", "")
            : call.Arguments.SequenceEqual(["rev-parse", "origin/main"])
                ? new ExecResult(0, "current-base\n", "")
                : new ExecResult(0, "", "");

        await mismatched.PrepareMergeabilityAsync();

        Assert.Contains(mismatched.Processes.Commands, call => call.Arguments.SequenceEqual(["merge", "--abort"]));
        Assert.Contains(mismatched.Processes.Commands, call => call.Arguments.SequenceEqual(["merge", "--no-commit", "--no-ff", "--", "origin/main"]));
        Assert.Contains("aborted that merge and prepared the current exact base current-base", mismatched.Job.PendingCheckFailures[0].Details);
    }

    [Fact]
    public async Task LegacyMergeabilityRepair_RefreshesMissingBaseMetadata()
    {
        var refreshed = Snapshot(false) with { Mergeable = "CONFLICTING", MergeStateStatus = "DIRTY", BaseRefName = "release/legacy" };
        using var fixture = HostFixture.Create(autoMergeAllowed: false, refreshed);
        fixture.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", fixture.Job.PullRequests[0].Url, "legacy details"));

        await fixture.PrepareMergeabilityAsync();

        Assert.Single(fixture.GitHub.Inspections);
        Assert.False(fixture.GitHub.Inspections[0].IncludeFeedback);
        Assert.Equal("release/legacy", fixture.Job.PendingCheckFailures[0].BaseRefName);
        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.SequenceEqual(["merge", "--no-commit", "--no-ff", "--", "origin/release/legacy"]));
    }

    [Fact]
    public async Task MergeabilityRepair_AcceptsConflictOnlyWhenUnresolvedPathsExist()
    {
        using var accepted = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        accepted.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", accepted.Job.PullRequests[0].Url, "details", "main"));
        accepted.Processes.Responder = call => call.Arguments.FirstOrDefault() switch
        {
            "rev-parse" when call.Arguments.Contains("MERGE_HEAD") => new ExecResult(1, "", ""),
            "merge" => new ExecResult(1, "", "conflict"),
            "diff" when call.Arguments.Contains("--diff-filter=U") => new ExecResult(0, "src/file.cs\n", ""),
            _ => new ExecResult(0, "", "")
        };

        await accepted.PrepareMergeabilityAsync();
        Assert.Contains("src/file.cs", accepted.Job.PendingCheckFailures[0].Details);

        using var rejected = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        rejected.Job.PendingCheckFailures.Add(new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", rejected.Job.PullRequests[0].Url, "details", "main"));
        rejected.Processes.Responder = call => call.Arguments.FirstOrDefault() switch
        {
            "rev-parse" when call.Arguments.Contains("MERGE_HEAD") => new ExecResult(1, "", ""),
            "merge" => new ExecResult(1, "", "fatal"),
            _ => new ExecResult(0, "", "")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(rejected.PrepareMergeabilityAsync);
    }

    [Fact]
    public async Task NoOpCheckRepair_ReturnsToFreshMonitoringAndKeepsAttemptBounds()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        var failure = new CheckState("build", "FAILURE", "fail", "check", fixture.Job.PullRequests[0].Url);
        fixture.Job.PendingCheckFailures.Add(failure);
        fixture.Job.ProcessedCheckIds.Add(failure.Id);
        fixture.Job.RepairAttemptsByPullRequest[fixture.Job.PullRequests[0].Url] = 2;

        await fixture.CompleteNoOpRepairAsync(failure.Id);

        Assert.Equal(JobPhases.Monitoring, fixture.Job.Phase);
        Assert.Empty(fixture.Job.PendingCheckFailures);
        Assert.DoesNotContain(failure.Id, fixture.Job.ProcessedCheckIds);
        Assert.Equal(2, fixture.Job.RepairAttemptsByPullRequest[fixture.Job.PullRequests[0].Url]);
    }

    [Fact]
    public async Task BlockedRepair_PublishesAddressedRepositoryChangesBeforeExternalBlock()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        var failure = new CheckState("pull-request-mergeability", "CONFLICTING", "fail", "conflict", fixture.Job.PullRequests[0].Url);
        fixture.Job.PendingCheckFailures.Add(failure);
        fixture.Processes.Responder = call => call.Arguments.SequenceEqual(["status", "--porcelain"])
            ? new ExecResult(0, "M src/file.cs\n", "")
            : call.Arguments.SequenceEqual(["rev-parse", "HEAD"])
                ? new ExecResult(0, "local-head\n", "")
                : call.Arguments.FirstOrDefault() == "ls-remote"
                    ? new ExecResult(0, "", "")
                    : call.Arguments.Contains("command", StringComparer.Ordinal)
                        ? new ExecResult(0, "{\"success\":true}", "")
                        : new ExecResult(0, "", "");
        var result = JsonSerializer.Serialize(new
        {
            status = "blocked",
            summary = "repair complete; credentialed acceptance remains",
            validationEvidence = new[] { "focused tests passed" },
            repositories = new[] { new { repository = "Repo", changed = true } },
            commitMessage = "repair conflict",
            prTitle = "repair conflict",
            prBody = "repair conflict",
            checkDispositions = new[] { new { checkId = failure.Id, addressed = true, summary = "resolved" } },
            threadDispositions = Array.Empty<object>(),
            workComplete = false,
            blocker = new { kind = "missingCredential", summary = "credential required", evidence = new[] { "TOKEN is absent" }, retryAtUtc = (string?)null }
        });

        await fixture.CompleteAsync(result, repair: true);

        Assert.Equal(JobPhases.Blocked, fixture.Job.Phase);
        Assert.Empty(fixture.Job.PendingCheckFailures);
        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.FirstOrDefault() == "commit");
        Assert.Contains(fixture.Processes.Commands, call => call.Arguments.FirstOrDefault() == "push");
        Assert.Contains(fixture.Processes.Commands, call => call.IsStatus("Blocked"));
    }

    [Fact]
    public async Task UnknownMergeability_WaitsWithoutQueuingRepairOrAutoMerge()
    {
        var pending = Snapshot(false) with { Mergeable = "UNKNOWN", MergeStateStatus = "UNKNOWN" };
        using var fixture = HostFixture.Create(autoMergeAllowed: true, pending);

        await fixture.MonitorAsync();

        Assert.Empty(fixture.Job.PendingCheckFailures);
        Assert.False(fixture.Job.ReadyForReviewRecorded);
        Assert.Empty(fixture.GitHub.MergedUrls);
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

    [Fact]
    public async Task Continuation_RunsCodexFromRetainedPrimaryWorkspace()
    {
        using var fixture = HostFixture.Create(autoMergeAllowed: false, Snapshot(false));
        fixture.Job.ThreadId = "thread-1";

        await fixture.ContinueAsync();

        var command = Assert.Single(fixture.Processes.Commands, command => command.Executable == "codex");
        Assert.Equal(fixture.Job.Workspaces[0].Directory, command.WorkingDirectory);
        Assert.Equal(["exec", "resume", "thread-1"], command.Arguments[..3]);
        Assert.Equal("-", command.Arguments[^1]);
    }

    private static PullRequestSnapshot Snapshot(bool merged, IReadOnlyList<CheckState>? checks = null, IReadOnlyList<ReviewFeedback>? feedback = null)
        => new(merged, checks ?? [new CheckState("build", "SUCCESS", "pass", "")], feedback ?? []);

    private sealed class HostFixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();

        private HostFixture(bool autoMergeAllowed, DateTime? unavailableUntilUtc, params PullRequestSnapshot[] snapshots)
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
            Settings = WorkerConfig.Load(configPath);
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
            new Journal { Jobs = [job], CodexUnavailableUntilUtc = unavailableUntilUtc }.Save(Path.Combine(statePath, "worker-journal.json"));

            Host = new WorkerHost(configPath, statePath, Clock, Processes, new NullLog(), GitHub);
            var journal = (Journal)typeof(WorkerHost).GetField("journal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            Job = Assert.Single(journal.Jobs);
        }

        public WorkerHost Host { get; }
        public MutableClock Clock { get; }
        public FakeProcessRunner Processes { get; }
        public WorkerConfig Settings { get; }
        public FakeGitHubClient GitHub { get; }
        public Job Job { get; }
        public TimeSpan QuietPeriod { get; }

        public static HostFixture Create(bool autoMergeAllowed, params PullRequestSnapshot[] snapshots)
            => new(autoMergeAllowed, null, snapshots);

        public static HostFixture CreateThrottled(DateTime unavailableUntilUtc)
            => new(false, unavailableUntilUtc, Snapshot(false));

        public async Task MonitorAsync()
        {
            var method = typeof(WorkerHost).GetMethod("MonitorJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Host, [Job, CancellationToken.None])!;
        }

        public async Task<FreshClaimOutcome> TickAsync()
        {
            var method = typeof(WorkerHost).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (Task<FreshClaimOutcome>)method.Invoke(Host, [CancellationToken.None, true, true])!;
        }

        public async Task ContinueAsync()
        {
            var method = typeof(WorkerHost).GetMethod("RunContinuationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task<ExecResult>)method.Invoke(Host, [Job, "schema.json", "continue", CancellationToken.None])!;
        }

        public async Task PrepareMergeabilityAsync()
        {
            var method = typeof(WorkerHost).GetMethod("PrepareMergeabilityRepairsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Host, [Job, CancellationToken.None])!;
        }

        public async Task CompleteNoOpRepairAsync(string checkId)
        {
            using var result = JsonDocument.Parse($$"""
            {"status":"noChanges","summary":"stale failure","repositories":[{"repository":"Repo","changed":false}],"checkDispositions":[{"checkId":{{JsonSerializer.Serialize(checkId)}},"addressed":false}],"threadDispositions":[]}
            """);
            var method = typeof(WorkerHost).GetMethod("CompleteResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Host, [Job, result.RootElement, true, CancellationToken.None])!;
        }

        public async Task CompleteAsync(string resultJson, bool repair)
        {
            using var result = JsonDocument.Parse(resultJson);
            var method = typeof(WorkerHost).GetMethod("CompleteResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Host, [Job, result.RootElement, repair, CancellationToken.None])!;
        }

        public void Dispose() => directory.Dispose();
    }

    private sealed class FakeGitHubClient(params PullRequestSnapshot[] snapshots) : IGitHubClient
    {
        private readonly Queue<PullRequestSnapshot> remaining = new(snapshots);
        private PullRequestSnapshot last = snapshots.LastOrDefault() ?? Snapshot(false);

        public List<(string Url, bool IncludeFeedback)> Inspections { get; } = [];
        public List<string> MergedUrls { get; } = [];
        public List<string> RerunUrls { get; } = [];

        public Task<PullRequestSnapshot> InspectAsync(string pullRequestUrl, bool includeFeedback, CancellationToken cancellationToken)
        {
            Inspections.Add((pullRequestUrl, includeFeedback));
            if (remaining.Count > 0) last = remaining.Dequeue();
            return Task.FromResult(last);
        }

        public Task ReplyAsync(string pullRequestUrl, ReviewFeedback feedback, string replyBody, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResolveAsync(string pullRequestUrl, string threadId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RerunAsync(string actionsRunUrl, CancellationToken cancellationToken) { RerunUrls.Add(actionsRunUrl); return Task.CompletedTask; }
        public Task MergeAsync(string pullRequestUrl, CancellationToken cancellationToken) { MergedUrls.Add(pullRequestUrl); return Task.CompletedTask; }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<CommandCall> Commands { get; } = [];
        public Func<CommandCall, ExecResult>? Responder { get; set; }

        public Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            var call = new CommandCall(executable, arguments.ToArray(), workingDirectory);
            Commands.Add(call);
            if (Responder is not null) return Task.FromResult(Responder(call));
            if (call.Arguments.SequenceEqual(["rev-parse", "--verify", "-q", "MERGE_HEAD"]))
                return Task.FromResult(new ExecResult(1, string.Empty, string.Empty));
            return Task.FromResult(call.Arguments.Contains("command", StringComparer.Ordinal)
                ? new ExecResult(0, "{\"success\":true}", string.Empty)
                : new ExecResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record CommandCall(string Executable, string[] Arguments, string WorkingDirectory)
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
