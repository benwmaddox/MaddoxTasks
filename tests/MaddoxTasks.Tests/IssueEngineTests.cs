using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class IssueEngineTests
{
    [Fact]
    public void CreateIssue_DefaultsToNext_AndAllowsExplicitBacklog()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);

        var defaultResult = engine.Execute(new CreateIssue("Default", null, Priority.From(3), null, null));
        var backlogResult = engine.Execute(new CreateIssue("Backlog", null, Priority.From(3), null, null, Status.Backlog));

        Assert.True(defaultResult.Success);
        Assert.True(backlogResult.Success);
        Assert.Equal(Status.Next, engine.QueryIssues(includeDone: true).Single(view => view.Issue.Title == "Default").Issue.Status);
        Assert.Equal(Status.Backlog, engine.QueryIssues(includeDone: true).Single(view => view.Issue.Title == "Backlog").Issue.Status);
    }

    [Fact]
    public void CreateIssue_RejectsReservationAndTerminalInitialStatuses()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);

        var result = engine.Execute(new CreateIssue("Invalid", null, Priority.From(3), null, null, Status.Active));

        Assert.False(result.Success);
        Assert.Contains("Next", result.Message, StringComparison.Ordinal);
        Assert.Empty(engine.GetEventLog());
    }

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
        var blocked = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Blocked", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var available = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Available", null, Priority.From(2), null, null, Status.Backlog)).IssueId);
        var noRepo = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("No repo", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
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
        var reviewId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Review", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var blockedId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Blocked", null, Priority.From(2), null, null, Status.Backlog)).IssueId);
        var freeId = Assert.IsAssignableFrom<IssueId>(engine.Execute(new CreateIssue("Free", null, Priority.From(3), null, null, Status.Backlog)).IssueId);

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

    [Fact]
    public void ClaimNext_SelectsDisjointChildWhenParentRepositoryIsReserved()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);
        var parent = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Parent", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var child = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Child", null, Priority.From(3), parent, null)).IssueId);

        Assert.True(engine.Execute(new AddLabel(parent, "repo:busy")).Success);
        Assert.True(engine.Execute(new ChangeStatus(parent, Status.Active)).Success);
        Assert.True(engine.Execute(new AddLabel(child, "repo:free")).Success);

        var claim = engine.ClaimNext();

        Assert.NotNull(claim);
        Assert.Equal(child, claim!.Issue.Id);
    }

    [Fact]
    public void ClaimNext_SkipsReservedChildAndClaimsLaterSibling()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);
        var parent = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Parent", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var reservedChild = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Reserved child", null, Priority.From(1), parent, null)).IssueId);
        var freeChild = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Free child", null, Priority.From(2), parent, null)).IssueId);
        var reservation = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Reservation", null, Priority.From(5), null, null, Status.Backlog)).IssueId);

        Assert.True(engine.Execute(new AddLabel(reservedChild, "repo:shared")).Success);
        Assert.True(engine.Execute(new AddLabel(freeChild, "repo:free")).Success);
        Assert.True(engine.Execute(new AddLabel(reservation, "repo:shared")).Success);
        Assert.True(engine.Execute(new ChangeStatus(reservation, Status.Active)).Success);

        var claim = engine.ClaimNext();

        Assert.NotNull(claim);
        Assert.Equal(freeChild, claim!.Issue.Id);
    }

    [Fact]
    public void ClaimNext_TraversesDescendantsBeforeAncestorsAndFallsBackByRootOrder()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);
        var root = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Root", null, Priority.From(1), null, null)).IssueId);
        var child = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Child", null, Priority.From(2), root, null)).IssueId);
        var grandchild = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Grandchild", null, Priority.From(5), child, null)).IssueId);
        var otherRoot = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Other root", null, Priority.From(2), null, null)).IssueId);

        Assert.True(engine.Execute(new AddLabel(root, "repo:root")).Success);
        Assert.True(engine.Execute(new AddLabel(child, "repo:child")).Success);
        Assert.True(engine.Execute(new AddLabel(grandchild, "repo:grandchild")).Success);
        Assert.True(engine.Execute(new AddLabel(otherRoot, "repo:other")).Success);

        var firstClaim = engine.ClaimNext();
        Assert.NotNull(firstClaim);
        Assert.Equal(grandchild, firstClaim!.Issue.Id);

        var secondClaim = engine.ClaimNext();
        Assert.NotNull(secondClaim);
        Assert.Equal(child, secondClaim!.Issue.Id);
    }

    [Fact]
    public void ClaimNext_DryRunUsesHierarchyWithoutMutatingState()
    {
        var clock = new FrozenClock(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStore(), clock);
        var parent = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Parent", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var child = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Child", null, Priority.From(2), parent, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(parent, "repo:parent")).Success);
        Assert.True(engine.Execute(new AddLabel(child, "repo:child")).Success);

        var preview = engine.ClaimNext(dryRun: true);

        Assert.NotNull(preview);
        Assert.Equal(child, preview!.Issue.Id);
        var state = engine.GetState();
        Assert.True(state.TryGetIssue(child, out var childIssue));
        Assert.Equal(Status.Next, childIssue.Status);
    }

    [Fact]
    public void HierarchicalIssues_HandlesCyclicAndMissingParentsDeterministically()
    {
        var start = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var firstId = new IssueId(Guid.NewGuid());
        var secondId = new IssueId(Guid.NewGuid());
        var missingParent = new IssueId(Guid.NewGuid());
        var orphanId = new IssueId(Guid.NewGuid());
        var events = new IssueEvent[]
        {
            new IssueCreated(Guid.NewGuid(), firstId, start, "First", null, Status.Next, Priority.From(2), secondId, null),
            new IssueCreated(Guid.NewGuid(), secondId, start.AddMinutes(1), "Second", null, Status.Next, Priority.From(1), firstId, null),
            new IssueCreated(Guid.NewGuid(), orphanId, start.AddMinutes(2), "Orphan", null, Status.Next, Priority.From(3), missingParent, null)
        };

        var state = IssueState.Replay(events);
        var firstPass = state.HierarchicalIssues().Select(issue => issue.Id).ToArray();
        var secondPass = state.HierarchicalIssues().Select(issue => issue.Id).ToArray();

        Assert.Equal(secondPass, firstPass);
        Assert.Equal(3, firstPass.Length);
        Assert.Contains(firstId, firstPass);
        Assert.Contains(secondId, firstPass);
        Assert.Contains(orphanId, firstPass);
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

