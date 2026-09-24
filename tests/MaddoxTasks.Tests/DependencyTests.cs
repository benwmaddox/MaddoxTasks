using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class DependencyTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateAndReplaceBlockers_ResolveTokensAndPersistVersionedCompleteSets()
    {
        var store = new DependencyEventStore();
        var engine = new IssueEngine(store, new DependencyClock(Now));
        var first = Create(engine, "First prerequisite");
        var second = Create(engine, "Second prerequisite");

        var created = engine.Execute(new CreateIssue(
            "Dependent",
            "Waits for prerequisites",
            Priority.From(2),
            null,
            null,
            BlockedBy: ["1"]));

        Assert.True(created.Success, created.Message);
        var dependent = Assert.IsType<IssueId>(created.IssueId);
        var creationEvents = store.LoadAll().TakeLast(2).ToArray();
        Assert.IsType<IssueCreated>(creationEvents[0]);
        var initialSet = Assert.IsType<IssueBlockersSet>(creationEvents[1]);
        Assert.Equal(IssueBlockersSet.CurrentSchemaVersion, initialSet.SchemaVersion);
        Assert.Equal([first], engine.GetState().Issues[dependent].BlockerIds);

        var replaced = engine.Execute(new SetBlockers(dependent, [second.ToShortCode(), first.ToString()]));

        Assert.True(replaced.Success, replaced.Message);
        Assert.Equal([first, second], engine.GetState().Issues[dependent].BlockerIds);
        var view = engine.QueryIssues(includeDone: true).Single(issue => issue.Issue.Id == dependent);
        Assert.Equal([first.ToString(), second.ToString()], view.BlockedBy!.Select(blocker => blocker.IssueId).ToArray());
        Assert.All(view.BlockedBy!, blocker => Assert.False(blocker.IsSatisfied));
        var blockers = view.BlockedBy ?? [];
        Assert.Contains("complete it", blockers[0].Reason, StringComparison.OrdinalIgnoreCase);

        var beforeDuplicate = store.LoadAll().Count;
        var duplicate = engine.Execute(new SetBlockers(dependent, ["1", first.ToString()]));
        Assert.False(duplicate.Success);
        Assert.Contains("duplicates", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeDuplicate, store.LoadAll().Count);
        Assert.Equal([first, second], engine.GetState().Issues[dependent].BlockerIds);

        var cleared = engine.Execute(new SetBlockers(dependent, []));
        Assert.True(cleared.Success, cleared.Message);
        Assert.Empty(engine.GetState().Issues[dependent].BlockerIds);
        Assert.Empty(Assert.IsType<IssueBlockersSet>(store.LoadAll()[^1]).BlockerIds);
    }

    [Fact]
    public void DependencyValidation_RejectsMissingSelfDuplicateAndIndirectCyclesWithoutWrites()
    {
        var store = new DependencyEventStore();
        var engine = new IssueEngine(store, new DependencyClock(Now));
        var first = Create(engine, "First");
        var second = Create(engine, "Second");
        var third = Create(engine, "Third");

        Assert.True(engine.Execute(new SetBlockers(second, [first.ToShortCode()])).Success);
        Assert.True(engine.Execute(new SetBlockers(third, ["2"])).Success);
        var eventCount = store.LoadAll().Count;

        var self = engine.Execute(new SetBlockers(first, [first.ToString()]));
        var missing = engine.Execute(new SetBlockers(first, ["missing-task"]));
        var cycle = engine.Execute(new SetBlockers(first, [third.ToString()]));

        Assert.False(self.Success);
        Assert.Contains("itself", self.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(missing.Success);
        Assert.Contains("could not be resolved", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(cycle.Success);
        Assert.Contains("cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(eventCount, store.LoadAll().Count);
        Assert.Empty(engine.GetState().Issues[first].BlockerIds);
        Assert.Equal([first], engine.GetState().Issues[second].BlockerIds);
        Assert.Equal([second], engine.GetState().Issues[third].BlockerIds);
    }

    [Theory]
    [InlineData(Status.Backlog)]
    [InlineData(Status.Active)]
    [InlineData(Status.Blocked)]
    [InlineData(Status.ReadyForReview)]
    [InlineData(Status.Rejected)]
    public void ClaimNext_SkipsDependenciesUnlessEveryBlockerIsDone(Status blockerStatus)
    {
        var engine = new IssueEngine(new DependencyEventStore(), new DependencyClock(Now));
        var blocker = Create(engine, "Prerequisite");
        var dependentResult = engine.Execute(new CreateIssue(
            "Dependent",
            null,
            Priority.From(1),
            null,
            null,
            BlockedBy: [blocker.ToString()]));
        var dependent = Assert.IsType<IssueId>(dependentResult.IssueId);
        Assert.True(dependentResult.Success, dependentResult.Message);
        Assert.True(engine.Execute(new ChangeStatus(blocker, blockerStatus)).Success);

        var claim = engine.ClaimNext();

        Assert.True(claim is null || claim.Issue.Id != dependent);
        Assert.Equal(Status.Next, engine.GetState().Issues[dependent].Status);
    }

    [Fact]
    public void ClaimDryRunUsesDependencyEligibilityWithoutWritingOrReordering()
    {
        var store = new DependencyEventStore();
        var engine = new IssueEngine(store, new DependencyClock(Now));
        var blocker = Assert.IsType<IssueId>(engine.Execute(
            new CreateIssue("Prerequisite", null, Priority.From(3), null, null, Status.Backlog)).IssueId);
        var dependent = Assert.IsType<IssueId>(engine.Execute(new CreateIssue(
            "Dependent",
            null,
            Priority.From(1),
            null,
            null,
            BlockedBy: ["1"])).IssueId);
        var fallback = Create(engine, "Fallback");
        var eventCount = store.LoadAll().Count;

        var preview = engine.ClaimNext(dryRun: true);

        Assert.NotNull(preview);
        Assert.Equal(fallback, preview!.Issue.Id);
        Assert.Equal(eventCount, store.LoadAll().Count);
        Assert.Equal(Status.Next, engine.GetState().Issues[dependent].Status);
        Assert.Equal(Status.Backlog, engine.GetState().Issues[blocker].Status);

        Assert.True(engine.Execute(new ChangeStatus(blocker, Status.Done)).Success);
        preview = engine.ClaimNext(dryRun: true);
        Assert.NotNull(preview);
        Assert.Equal(dependent, preview!.Issue.Id);
        Assert.Equal(Status.Next, engine.GetState().Issues[dependent].Status);
    }

    [Fact]
    public void DoneBlockersUnlockClaimsAndMissingBlockersRemainActionable()
    {
        var store = new DependencyEventStore();
        var engine = new IssueEngine(store, new DependencyClock(Now));
        var blocker = Create(engine, "Prerequisite");
        var dependent = Assert.IsType<IssueId>(engine.Execute(new CreateIssue(
            "Dependent",
            null,
            Priority.From(1),
            null,
            null,
            BlockedBy: ["1"])).IssueId);
        Assert.True(engine.Execute(new ChangeStatus(blocker, Status.Done)).Success);

        var claim = engine.ClaimNext();

        Assert.NotNull(claim);
        Assert.Equal(dependent, claim!.Issue.Id);
        Assert.True(Assert.Single(claim.BlockedBy!).IsSatisfied);

        var missingIssue = Create(engine, "Missing reference test");
        var missingId = IssueId.New();
        store.Append(new IssueBlockersSet(
            Guid.NewGuid(),
            missingIssue,
            Now.AddMinutes(1),
            IssueBlockersSet.CurrentSchemaVersion,
            [missingId]));
        var missingView = engine.QueryIssues(includeDone: true).Single(view => view.Issue.Id == missingIssue);
        var missing = Assert.Single(missingView.BlockedBy!);
        Assert.Null(missing.Status);
        Assert.Contains("missing", missing.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(missing.IsSatisfied);
        Assert.Null(engine.ClaimNext());
    }

    [Fact]
    public void ResearchClaim_SkipsBlockedTasksWithUnresolvedDependencies()
    {
        var store = new DependencyEventStore();
        var blocker = IssueId.New();
        var source = IssueId.New();
        store.Append(new IssueCreated(Guid.NewGuid(), blocker, Now.AddHours(-13), "Prerequisite", null, Status.Next, Priority.From(1), null, null));
        store.Append(new IssueCreated(Guid.NewGuid(), source, Now.AddHours(-13), "Blocked source", null, Status.Blocked, Priority.From(2), null, null));
        store.Append(new IssueBlockersSet(
            Guid.NewGuid(),
            source,
            Now.AddHours(-13),
            IssueBlockersSet.CurrentSchemaVersion,
            [blocker]));
        var engine = new IssueEngine(store, new DependencyClock(Now));

        var eventCount = store.LoadAll().Count;
        Assert.Null(engine.ResearchClaimBlocked(dryRun: true).Task);
        Assert.Equal(eventCount, store.LoadAll().Count);
        Assert.Empty(engine.GetState().Issues[source].Comments);
        Assert.True(engine.Execute(new ChangeStatus(blocker, Status.Done)).Success);

        var research = engine.ResearchClaimBlocked();

        Assert.NotNull(research.Task);
        Assert.Equal(source, research.Task!.Issue.Id);
        Assert.True(Assert.Single(research.Task.BlockedBy!).IsSatisfied);
    }

    private static IssueId Create(IssueEngine engine, string title)
    {
        var result = engine.Execute(new CreateIssue(title, null, Priority.From(3), null, null));
        Assert.True(result.Success, result.Message);
        return Assert.IsType<IssueId>(result.IssueId);
    }
}

file sealed class DependencyEventStore : IEventStore
{
    private readonly List<IssueEvent> _events = [];
    public IReadOnlyList<IssueEvent> LoadAll() => _events.ToArray();
    public void Append(IssueEvent issueEvent) => _events.Add(issueEvent);
}

file sealed class DependencyClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; } = utcNow;
}
