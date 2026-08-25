using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class IssueEngineTests
{
    [Fact]
    public void Replay_ReconstructsIssueFromEventLog()
    {
        var issueId = new IssueId(Guid.NewGuid());
        var createdAt = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc);

        var events = new IssueEvent[]
        {
            new IssueCreated(Guid.NewGuid(), issueId, createdAt, "Implement replay", "Initial", Status.Backlog, Priority.From(3), null, dueDate),
            new StatusChanged(Guid.NewGuid(), issueId, createdAt.AddMinutes(1), Status.Active),
            new PriorityChanged(Guid.NewGuid(), issueId, createdAt.AddMinutes(2), Priority.From(1)),
            new LabelAdded(Guid.NewGuid(), issueId, createdAt.AddMinutes(3), "architecture"),
            new DescriptionUpdated(Guid.NewGuid(), issueId, createdAt.AddMinutes(4), "Final description"),
            new LabelRemoved(Guid.NewGuid(), issueId, createdAt.AddMinutes(5), "architecture"),
            new CommentAdded(Guid.NewGuid(), issueId, createdAt.AddMinutes(6), "Ship this after review")
        };

        var state = IssueState.Replay(events);

        Assert.True(state.TryGetIssue(issueId, out var issue));
        Assert.Equal("Implement replay", issue.Title);
        Assert.Equal("Final description", issue.Description);
        Assert.Equal(Status.Active, issue.Status);
        Assert.Equal(1, issue.Priority.Value);
        Assert.Equal(createdAt, issue.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(6), issue.UpdatedAt);
        Assert.Equal(dueDate, issue.DueDate);
        Assert.Empty(issue.Labels);
        Assert.Single(issue.Comments);
        Assert.Equal("Ship this after review", issue.Comments[0].Comment);
        Assert.Equal("user", issue.Comments[0].Actor);
    }

    [Fact]
    public void Execute_AppendsEventAndRebuildsState()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);

        var createResult = engine.Execute(new CreateIssue("First task", "Desc", Priority.From(2), null, null));
        Assert.True(createResult.Success);
        Assert.Single(store.LoadAll());
        Assert.IsType<IssueCreated>(store.LoadAll()[0]);

        var issueId = Assert.IsAssignableFrom<IssueId>(createResult.IssueId);
        var statusResult = engine.Execute(new ChangeStatus(issueId, Status.Blocked));
        Assert.True(statusResult.Success);
        Assert.Equal(2, store.LoadAll().Count);
        Assert.IsType<StatusChanged>(store.LoadAll()[1]);

        var issue = engine.QueryIssues(includeDone: true).Single().Issue;
        Assert.Equal(Status.Blocked, issue.Status);
        Assert.Equal("First task", issue.Title);

        var commentResult = engine.Execute(new AddComment(issueId, "Need logs from staging"));
        Assert.True(commentResult.Success);
        Assert.Equal(3, store.LoadAll().Count);
        var commentEvent = Assert.IsType<CommentAdded>(store.LoadAll()[2]);
        Assert.Equal("user", commentEvent.Actor);
    }

    [Fact]
    public void ApplyFilter_IsDeterministicAndPure()
    {
        var start = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var id1 = new IssueId(Guid.NewGuid());
        var id2 = new IssueId(Guid.NewGuid());
        var id3 = new IssueId(Guid.NewGuid());

        var events = new IssueEvent[]
        {
            new IssueCreated(Guid.NewGuid(), id1, start, "A", null, Status.Backlog, Priority.From(2), null, start.AddDays(1)),
            new LabelAdded(Guid.NewGuid(), id1, start.AddMinutes(1), "api"),
            new StatusChanged(Guid.NewGuid(), id1, start.AddMinutes(2), Status.Active),

            new IssueCreated(Guid.NewGuid(), id2, start.AddMinutes(3), "B", null, Status.Backlog, Priority.From(4), null, start.AddDays(3)),
            new LabelAdded(Guid.NewGuid(), id2, start.AddMinutes(4), "api"),
            new StatusChanged(Guid.NewGuid(), id2, start.AddMinutes(5), Status.Active),

            new IssueCreated(Guid.NewGuid(), id3, start.AddMinutes(6), "C", null, Status.Backlog, Priority.From(1), null, start.AddDays(1)),
            new LabelAdded(Guid.NewGuid(), id3, start.AddMinutes(7), "infra"),
            new StatusChanged(Guid.NewGuid(), id3, start.AddMinutes(8), Status.Next)
        };

        var state = IssueState.Replay(events);
        var filter = new IssueFilter
        {
            StatusEquals = Status.Active,
            PriorityLessThanOrEqual = 2,
            MustHaveLabels = ["api"],
            DueBefore = start.AddDays(2)
        };

        var firstPass = IssueFiltering.ApplyFilter(state.OrderedIssues, filter).Select(issue => issue.Id).ToArray();
        var secondPass = IssueFiltering.ApplyFilter(state.OrderedIssues, filter).Select(issue => issue.Id).ToArray();

        Assert.Equal(firstPass, secondPass);
        Assert.Single(firstPass);
        Assert.Equal(id1, firstPass[0]);
    }

    [Fact]
    public void QueryIssues_HidesTerminalStatusesByDefaultButAllowsExplicitTerminalFilter()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);

        var done = engine.Execute(new CreateIssue("Done", null, Priority.From(3), null, null));
        var rejected = engine.Execute(new CreateIssue("Rejected", null, Priority.From(3), null, null));
        var active = engine.Execute(new CreateIssue("Active", null, Priority.From(3), null, null));
        Assert.True(done.Success && rejected.Success && active.Success);
        Assert.True(engine.Execute(new ChangeStatus(Assert.IsAssignableFrom<IssueId>(done.IssueId), Status.Done)).Success);
        Assert.True(engine.Execute(new ChangeStatus(Assert.IsAssignableFrom<IssueId>(rejected.IssueId), Status.Rejected)).Success);
        var activeId = Assert.IsAssignableFrom<IssueId>(active.IssueId);
        Assert.True(engine.Execute(new AddLabel(activeId, "repo:status-test")).Success);
        Assert.True(engine.Execute(new ChangeStatus(activeId, Status.Active)).Success);

        var open = engine.QueryIssues(includeDone: false);
        Assert.Single(open);
        Assert.Equal(Status.Active, open[0].Issue.Status);

        var explicitRejected = engine.QueryIssues(new IssueFilter { StatusEquals = Status.Rejected }, includeDone: false);
        Assert.Single(explicitRejected);
        Assert.Equal(Status.Rejected, explicitRejected[0].Issue.Status);

        var explicitDone = engine.QueryIssues(new IssueFilter { StatusEquals = Status.Done }, includeDone: false);
        Assert.Single(explicitDone);
        Assert.Equal(Status.Done, explicitDone[0].Issue.Status);
    }
    [Fact]
    public void AddComment_RequiresNonEmptyComment()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);

        var createResult = engine.Execute(new CreateIssue("First task", null, Priority.From(3), null, null));
        var issueId = Assert.IsAssignableFrom<IssueId>(createResult.IssueId);

        var commentResult = engine.Execute(new AddComment(issueId, "   "));
        Assert.False(commentResult.Success);
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void AddComment_PreservesModelActor()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);

        var createResult = engine.Execute(new CreateIssue("First task", null, Priority.From(3), null, null));
        var issueId = Assert.IsAssignableFrom<IssueId>(createResult.IssueId);

        var commentResult = engine.Execute(new AddComment(issueId, "Automated note", "gpt-5.3-codex high"));
        Assert.True(commentResult.Success);

        var commentEvent = Assert.IsType<CommentAdded>(store.LoadAll()[1]);
        Assert.Equal("gpt-5.3-codex high", commentEvent.Actor);
        Assert.Equal("gpt-5.3-codex high", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
    }

    [Fact]
    public void AddComment_RejectsInvalidActorIdentifier()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);

        var createResult = engine.Execute(new CreateIssue("First task", null, Priority.From(3), null, null));
        var issueId = Assert.IsAssignableFrom<IssueId>(createResult.IssueId);

        var commentResult = engine.Execute(new AddComment(issueId, "Automated note", "bad actor!"));
        Assert.False(commentResult.Success);

        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void ActiveTasksRequireDisjointRepositories()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);
        var first = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("First", null, Priority.From(3), null, null)).IssueId);
        var second = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Second", null, Priority.From(3), null, null)).IssueId);

        Assert.False(engine.Execute(new ChangeStatus(first, Status.Active)).Success);
        Assert.True(engine.Execute(new AddLabel(first, "Repo:StasisLang")).Success);
        Assert.True(engine.Execute(new ChangeStatus(first, Status.Active)).Success);
        Assert.True(engine.Execute(new AddLabel(second, "repo:stasislang")).Success);

        var conflict = engine.Execute(new ChangeStatus(second, Status.Active));
        Assert.False(conflict.Success);
        Assert.Contains("already reserved", conflict.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(engine.Execute(new RemoveLabel(second, "repo:stasislang")).Success);
        Assert.True(engine.Execute(new AddLabel(second, "repo:Other")).Success);
        Assert.True(engine.Execute(new ChangeStatus(second, Status.Active)).Success);
        Assert.Equal(["other"], engine.QueryIssues(includeDone: true).Single(view => view.Issue.Id == second).Issue.Repositories);
    }

    [Fact]
    public void ActiveRepositoryEditsCannotRemoveLastReservation()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);
        var issueId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Task", null, Priority.From(3), null, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(issueId, "repo:one")).Success);
        Assert.True(engine.Execute(new ChangeStatus(issueId, Status.Active)).Success);

        var result = engine.Execute(new RemoveLabel(issueId, "repo:one"));
        Assert.False(result.Success);
        Assert.Contains("at least one repo", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaimNextSelectsAvailableTaskByPriorityAndSequence()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);
        var blocked = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Blocked", null, Priority.From(1), null, null)).IssueId);
        var available = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Available", null, Priority.From(2), null, null)).IssueId);
        var noRepo = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("No repo", null, Priority.From(1), null, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(blocked, "repo:busy")).Success);
        Assert.True(engine.Execute(new AddLabel(available, "repo:free")).Success);
        Assert.True(engine.Execute(new AddLabel(noRepo, "work")).Success);
        Assert.True(engine.Execute(new ChangeStatus(blocked, Status.Next)).Success);
        Assert.True(engine.Execute(new ChangeStatus(available, Status.Next)).Success);
        Assert.True(engine.Execute(new ChangeStatus(noRepo, Status.Next)).Success);
        Assert.True(engine.Execute(new ChangeStatus(blocked, Status.Active)).Success);

        var claim = engine.ClaimNext();
        Assert.NotNull(claim);
        Assert.Equal(available, claim!.Issue.Id);
        Assert.Equal(Status.Active, claim.Issue.Status);
        Assert.Null(engine.ClaimNext());
        Assert.Equal(Status.Active, engine.QueryIssues(includeDone: true).Single(view => view.Issue.Id == blocked).Issue.Status);
    }

    [Fact]
    public void ReadyForReviewHoldsReservationsAndBlocksClaims()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStore();
        var engine = new IssueEngine(store, clock);
        var reviewId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Review", null, Priority.From(1), null, null)).IssueId);
        var blockedId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Blocked", null, Priority.From(2), null, null)).IssueId);
        var freeId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Free", null, Priority.From(3), null, null)).IssueId);

        Assert.True(engine.Execute(new AddLabel(reviewId, "repo:shared")).Success);
        Assert.True(engine.Execute(new ChangeStatus(reviewId, Status.ReadyForReview)).Success);
        Assert.True(engine.Execute(new AddLabel(blockedId, "repo:shared")).Success);
        Assert.True(engine.Execute(new ChangeStatus(blockedId, Status.Next)).Success);
        Assert.True(engine.Execute(new AddLabel(freeId, "repo:free")).Success);

        var blockedActivation = engine.Execute(new ChangeStatus(blockedId, Status.Active));
        Assert.False(blockedActivation.Success);
        Assert.Null(engine.ClaimNext());

        Assert.True(engine.Execute(new ChangeStatus(freeId, Status.Next)).Success);
        var claim = engine.ClaimNext();
        Assert.NotNull(claim);
        Assert.Equal(freeId, claim!.Issue.Id);
    }
}

file sealed class InMemoryEventStore : IEventStore
{
    private readonly List<IssueEvent> _events = [];

    public IReadOnlyList<IssueEvent> LoadAll() => _events.ToArray();

    public void Append(IssueEvent issueEvent) => _events.Add(issueEvent);
}

file sealed class FrozenClock : IClock
{
    public FrozenClock(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; }
}

