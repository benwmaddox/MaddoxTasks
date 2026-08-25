using MaddoxTasks.Agent;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class ReviewReconcilerTests
{
    [Fact]
    public void NoPullRequestsLeavesTaskOpen()
    {
        var (engine, issueId) = CreateReview("No PR", "Nothing to merge");
        var result = new ReviewReconciler(engine, new FakePullRequests()).Reconcile();

        Assert.Equal("noPullRequests", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void AllMergedPullRequestsCloseTask()
    {
        var (engine, issueId) = CreateReview("Merged", "https://github.com/Owner/Repo/pull/42");
        var fake = new FakePullRequests();
        fake.Add("https://github.com/owner/repo/pull/42", merged: true);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("closed", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(Status.Done, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void OpenOrClosedUnmergedPullRequestLeavesTaskOpen()
    {
        var (engine, issueId) = CreateReview("Unmerged", "https://github.com/owner/repo/pull/42");
        var fake = new FakePullRequests();
        fake.Add("https://github.com/owner/repo/pull/42", merged: false);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("unmerged", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void MixedMergedAndUnmergedPullRequestsDoNotCloseTask()
    {
        var (engine, _) = CreateReview("Mixed", "https://github.com/owner/repo/pull/41\nhttps://github.com/owner/repo/pull/42");
        var fake = new FakePullRequests();
        fake.Add("https://github.com/owner/repo/pull/41", merged: true);
        fake.Add("https://github.com/owner/repo/pull/42", merged: false);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("unmerged", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void LookupErrorLeavesTaskOpenAndProcessingContinues()
    {
        var (engine, firstId) = CreateReview("Error", "https://github.com/owner/repo/pull/1");
        var secondId = CreateReview(engine, "Merged", "https://github.com/owner/repo/pull/2", "repo:test-two");
        var fake = new FakePullRequests { ErrorUrl = "https://github.com/owner/repo/pull/1" };
        fake.Add("https://github.com/owner/repo/pull/2", merged: true);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("lookupError", result.Outcomes.Single(outcome => outcome.IssueId == firstId.ToString()).Outcome);
        Assert.Equal("closed", result.Outcomes.Single(outcome => outcome.IssueId == secondId.ToString()).Outcome);
    }

    [Fact]
    public void UrlsAreCanonicalizedAndDeduplicated()
    {
        var (engine, _) = CreateReview("Duplicate", "https://github.com/Owner/Repo/pull/42");
        var issueId = engine.QueryIssues(includeDone: true).Single().Issue.Id;
        Assert.True(engine.Execute(new AddComment(issueId, "Also https://github.com/owner/repo/pull/42.")).Success);
        var fake = new FakePullRequests();
        fake.Add("https://github.com/owner/repo/pull/42", merged: true);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("closed", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void DryRunDoesNotInvokeGitHubOrMutateTask()
    {
        var (engine, _) = CreateReview("Preview", "https://github.com/owner/repo/pull/42");
        var fake = new FakePullRequests { ThrowOnCall = true };

        var result = new ReviewReconciler(engine, fake).Reconcile(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal("dryRun", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void ConcurrentStateChangeDuringLookupIsNotForcedDone()
    {
        var (engine, issueId) = CreateReview("Changed", "https://github.com/owner/repo/pull/42");
        var fake = new FakePullRequests
        {
            OnGet = _ => Assert.True(engine.Execute(new ChangeStatus(issueId, Status.Active)).Success)
        };
        fake.Add("https://github.com/owner/repo/pull/42", merged: true);

        var result = new ReviewReconciler(engine, fake).Reconcile();

        Assert.Equal("concurrentStateChange", Assert.Single(result.Outcomes).Outcome);
        Assert.Equal(Status.Active, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    private static (IssueEngine Engine, IssueId IssueId) CreateReview(string title, string description)
    {
        var engine = new IssueEngine(new TestEventStore(), new TestClock());
        return (engine, CreateReview(engine, title, description));
    }

    private static IssueId CreateReview(IssueEngine engine, string title, string description, string repository = "repo:test")
    {
        var issueId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue(title, description, Priority.From(3), null, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(issueId, repository)).Success);
        Assert.True(engine.Execute(new ChangeStatus(issueId, Status.ReadyForReview)).Success);
        return issueId;
    }

    private sealed class FakePullRequests : IGitHubPullRequestClient
    {
        private readonly Dictionary<string, GitHubPullRequest> _responses = new(StringComparer.OrdinalIgnoreCase);
        public string? ErrorUrl { get; init; }
        public bool ThrowOnCall { get; init; }
        public Action<string>? OnGet { get; init; }
        public int Calls { get; private set; }

        public void Add(string url, bool merged)
            => _responses[url] = new GitHubPullRequest(url, merged ? "MERGED" : "CLOSED", merged ? DateTimeOffset.UtcNow : null);

        public GitHubPullRequest Get(string url)
        {
            Calls++;
            OnGet?.Invoke(url);
            if (ThrowOnCall || string.Equals(url, ErrorUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("test lookup failure");
            }

            return _responses[url];
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly List<IssueEvent> _events = [];
        public IReadOnlyList<IssueEvent> LoadAll() => _events.ToArray();
        public void Append(IssueEvent issueEvent) => _events.Add(issueEvent);
    }

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow => new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    }
}
