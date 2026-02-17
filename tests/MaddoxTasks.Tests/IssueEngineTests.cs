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
        Assert.IsType<CommentAdded>(store.LoadAll()[2]);
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

