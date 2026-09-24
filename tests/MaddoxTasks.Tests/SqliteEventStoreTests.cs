using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;
using Microsoft.Data.Sqlite;

namespace MaddoxTasks.Tests;

public sealed class SqliteEventStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteEventStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"maddoxtasks-tests-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public void RequeueBlocked_RollsBackEveryStatusWhenSecondInsertFails()
    {
        var timestamp = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);
        var first = IssueId.New();
        var second = IssueId.New();
        store.Append(new IssueCreated(Guid.NewGuid(), first, timestamp, "First", null, Status.Blocked, Priority.From(3), null, null));
        store.Append(new IssueCreated(Guid.NewGuid(), second, timestamp, "Second", null, Status.Blocked, Priority.From(3), null, null));
        var eventCount = store.LoadAll().Count;

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER FailSecondRequeue
                BEFORE INSERT ON Events
                WHEN NEW.EventType = 'StatusChanged' AND NEW.IssueId = '{second}'
                BEGIN
                    SELECT RAISE(FAIL, 'injected second status failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var result = new IssueEngine(store, new FrozenClockForSqliteReservations(timestamp.AddHours(1))).RequeueBlocked();

        Assert.False(result.Success);
        Assert.Contains("injected second status failure", result.Message, StringComparison.Ordinal);
        Assert.Equal(eventCount, store.LoadAll().Count);
        var state = IssueState.Replay(store.LoadAll());
        Assert.Equal(Status.Blocked, state.Issues[first].Status);
        Assert.Equal(Status.Blocked, state.Issues[second].Status);
    }

    [Fact]
    public void AppendAndLoadAll_RoundTripsEvents()
    {
        var issueId = new IssueId(Guid.NewGuid());
        var timestamp = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);

        store.Append(new IssueCreated(Guid.NewGuid(), issueId, timestamp, "Persist me", null, Status.Backlog, Priority.From(3), null, null));
        store.Append(new StatusChanged(Guid.NewGuid(), issueId, timestamp.AddMinutes(1), Status.Active));
        store.Append(new CheckoutSet(Guid.NewGuid(), issueId, timestamp.AddMinutes(2), "WORKTREE:persisted"));

        var loaded = store.LoadAll();

        Assert.Equal(3, loaded.Count);
        Assert.IsType<IssueCreated>(loaded[0]);
        Assert.IsType<StatusChanged>(loaded[1]);
        Assert.IsType<CheckoutSet>(loaded[2]);

        var replayed = IssueState.Replay(loaded);
        var issue = Assert.Single(replayed.OrderedIssues);
        Assert.Equal("Persist me", issue.Title);
        Assert.Equal(Status.Active, issue.Status);
        Assert.Equal("worktree:persisted", issue.Checkout);
    }

    [Fact]
    public void LegacyIssueEvents_DefaultToCanonicalCheckout()
    {
        var issueId = new IssueId(Guid.NewGuid());
        var timestamp = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);
        store.Append(new IssueCreated(Guid.NewGuid(), issueId, timestamp, "Legacy", null, Status.Next, Priority.From(3), null, null));

        var replayed = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll());

        Assert.Equal(CheckoutIdentity.Canonical, replayed.Issues[issueId].Checkout);
    }

    [Fact]
    public void StatusChanged_RejectedRoundTripsThroughSqlite()
    {
        var issueId = new IssueId(Guid.NewGuid());
        var timestamp = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);

        store.Append(new IssueCreated(Guid.NewGuid(), issueId, timestamp, "Reject me", null, Status.Backlog, Priority.From(3), null, null));
        store.Append(new StatusChanged(Guid.NewGuid(), issueId, timestamp.AddMinutes(1), Status.Rejected));

        var loaded = store.LoadAll();
        var replayed = IssueState.Replay(loaded);
        var issue = Assert.Single(replayed.OrderedIssues);
        Assert.Equal(Status.Rejected, issue.Status);
    }

    [Fact]
    public void IssueBlockersSet_RoundTripsItsSchemaVersionAndFullBlockerSet()
    {
        var blocker = IssueId.New();
        var dependent = IssueId.New();
        var timestamp = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);
        store.Append(new IssueCreated(Guid.NewGuid(), blocker, timestamp, "Prerequisite", null, Status.Done, Priority.From(2), null, null));
        store.Append(new IssueCreated(Guid.NewGuid(), dependent, timestamp, "Dependent", null, Status.Next, Priority.From(3), null, null));
        store.Append(new IssueBlockersSet(Guid.NewGuid(), dependent, timestamp.AddMinutes(1), IssueBlockersSet.CurrentSchemaVersion, [blocker]));

        var loaded = store.LoadAll();
        var blockersSet = Assert.IsType<IssueBlockersSet>(loaded[^1]);
        Assert.Equal(IssueBlockersSet.CurrentSchemaVersion, blockersSet.SchemaVersion);
        Assert.Equal([blocker], blockersSet.BlockerIds);
        Assert.Equal([blocker], IssueState.Replay(loaded).Issues[dependent].BlockerIds);
        Assert.True(new IssueEngine(store, new FrozenClockForSqliteReservations(timestamp.AddMinutes(2)))
            .GetState().GetView(dependent).BlockedBy!.Single().IsSatisfied);
    }

    [Fact]
    public void IssueBlockersSet_RejectsUnsupportedSchemaVersionDuringReplay()
    {
        var issueId = IssueId.New();
        var store = new SqliteEventStore(_dbPath);
        store.Append(new IssueCreated(Guid.NewGuid(), issueId, DateTime.UtcNow, "Task", null, Status.Next, Priority.From(3), null, null));
        store.Append(new IssueBlockersSet(Guid.NewGuid(), issueId, DateTime.UtcNow, 2, []));

        Assert.Throws<InvalidOperationException>(() => IssueState.Replay(store.LoadAll()));
    }

    [Fact]
    public async Task ConcurrentBlockerEdits_CannotCommitADependencyCycle()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var first = Assert.IsType<IssueId>(setup.Execute(new CreateIssue("First", null, Priority.From(2), null, null)).IssueId);
        var second = Assert.IsType<IssueId>(setup.Execute(new CreateIssue("Second", null, Priority.From(2), null, null)).IssueId);

        var firstUpdateTask = Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).Execute(new SetBlockers(first, [second.ToString()])));
        var secondUpdateTask = Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).Execute(new SetBlockers(second, [first.ToString()])));
        var updates = await Task.WhenAll(firstUpdateTask, secondUpdateTask);

        Assert.Single(updates, result => result.Success);
        Assert.Single(updates, result => !result.Success && result.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Single(new SqliteEventStore(_dbPath).LoadAll(), issueEvent => issueEvent is IssueBlockersSet);
        var state = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll());
        Assert.NotEqual(state.Issues[first].BlockerIds.Contains(second), state.Issues[second].BlockerIds.Contains(first));
    }

    [Fact]
    public async Task ConcurrentClaimAndBlockerCompletion_UseOneAtomicDependencySnapshot()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var blocker = Assert.IsType<IssueId>(setup.Execute(new CreateIssue("Prerequisite", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var dependent = Assert.IsType<IssueId>(setup.Execute(new CreateIssue("Dependent", null, Priority.From(2), null, null, BlockedBy: ["1"])).IssueId);

        var completionTask = Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).Execute(new ChangeStatus(blocker, Status.Done)));
        var claimTask = Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext());
        await Task.WhenAll(completionTask, claimTask);
        var completion = await completionTask;
        Assert.True(completion.Success);
        var claim = await claimTask;
        var events = new SqliteEventStore(_dbPath).LoadAll();
        var doneIndex = events.Select((issueEvent, index) => (issueEvent, index))
            .Single(item => item.issueEvent is StatusChanged status && status.IssueId == blocker && status.NewStatus == Status.Done).index;
        if (claim is null) claim = new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext();

        Assert.NotNull(claim);
        Assert.Equal(dependent, claim!.Issue.Id);
        var activeIndex = new SqliteEventStore(_dbPath).LoadAll().Select((issueEvent, index) => (issueEvent, index))
            .Single(item => item.issueEvent is StatusChanged status && status.IssueId == dependent && status.NewStatus == Status.Active).index;
        Assert.True(doneIndex < activeIndex, "A dependent task must not be activated before its blocker is Done in the ledger.");
    }

    [Fact]
    public async Task ConcurrentClaims_ClaimSameTaskOnlyOnce()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var issueId = Assert.IsAssignableFrom<IssueId>(setup.Execute(new CreateIssue("Claim me", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        Assert.True(setup.Execute(new AddLabel(issueId, "repo:shared")).Success);
        Assert.True(setup.Execute(new ChangeStatus(issueId, Status.Next)).Success);

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()));

        Assert.Single(claims, claim => claim is not null);
        Assert.Single(claims, claim => claim is null);
        var finalIssue = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll()).OrderedIssues.Single();
        Assert.Equal(Status.Active, finalIssue.Status);
    }

    [Fact]
    public async Task ConcurrentClaims_CannotShareRepositoryButCanUseDisjointRepositories()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        foreach (var repository in new[] { "shared", "shared", "other" })
        {
            var issueId = Assert.IsAssignableFrom<IssueId>(setup.Execute(new CreateIssue(repository, null, Priority.From(1), null, null, Status.Backlog)).IssueId);
            Assert.True(setup.Execute(new AddLabel(issueId, $"repo:{repository}")).Success);
            Assert.True(setup.Execute(new ChangeStatus(issueId, Status.Next)).Success);
        }

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()));

        Assert.Equal(2, claims.Count(claim => claim is not null));
        var activeRepositories = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll()).OrderedIssues
            .Where(issue => issue.Status == Status.Active)
            .SelectMany(issue => issue.Repositories)
            .ToArray();
        Assert.Equal(2, activeRepositories.Length);
        Assert.Equal(2, activeRepositories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ConcurrentDedicatedClaims_ReserveSameRepositoryInDistinctWorktrees()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var canonical = Assert.IsAssignableFrom<IssueId>(setup.Execute(
            new CreateIssue("Canonical owner", null, Priority.From(1), null, null)).IssueId);
        Assert.True(setup.Execute(new AddLabel(canonical, "repo:shared")).Success);
        Assert.True(setup.Execute(new ChangeStatus(canonical, Status.Active)).Success);
        var candidates = new List<IssueId>();
        foreach (var title in new[] { "Worktree one", "Worktree two" })
        {
            var issueId = Assert.IsAssignableFrom<IssueId>(setup.Execute(
                new CreateIssue(title, null, Priority.From(2), null, null)).IssueId);
            Assert.True(setup.Execute(new AddLabel(issueId, "repo:shared")).Success);
            candidates.Add(issueId);
        }

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext(dedicatedWorktree: true)),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext(dedicatedWorktree: true)));

        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.Equal(candidates.OrderBy(id => id.Value).ToArray(),
            claims.Select(claim => claim!.Issue.Id).OrderBy(id => id.Value).ToArray());
        var active = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll()).OrderedIssues
            .Where(issue => issue.Status == Status.Active)
            .ToArray();
        Assert.Equal(3, active.Length);
        Assert.Equal(CheckoutIdentity.Canonical, active.Single(issue => issue.Id == canonical).Checkout);
        var worktreeIssues = active.Where(issue => issue.Id != canonical).ToArray();
        Assert.All(worktreeIssues, issue =>
        {
            Assert.Equal(["shared"], issue.Repositories);
            Assert.Equal(CheckoutIdentity.ForIssue(issue.Id), issue.Checkout);
        });
        Assert.Equal(2, worktreeIssues.Select(issue => issue.Checkout).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ManualActivation_CannotShareSamePersistedWorktreeReservation()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var first = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("First", null, Priority.From(1), null, null)).IssueId);
        var second = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Second", null, Priority.From(2), null, null)).IssueId);
        foreach (var issueId in new[] { first, second })
        {
            Assert.True(engine.Execute(new AddLabel(issueId, "repo:shared")).Success);
            Assert.True(engine.Execute(new SetCheckout(issueId, "worktree:shared")).Success);
        }

        Assert.True(engine.Execute(new ChangeStatus(first, Status.Active)).Success);
        var secondActivation = engine.Execute(new ChangeStatus(second, Status.Active));

        Assert.False(secondActivation.Success);
        var replayed = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll());
        Assert.Equal(Status.Active, replayed.Issues[first].Status);
        Assert.Equal(Status.Next, replayed.Issues[second].Status);
        Assert.Equal("worktree:shared", replayed.Issues[second].Checkout);
    }

    [Fact]
    public async Task ConcurrentClaims_AllowOnlyOneMissingReservationAndOneBackedReservation()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        for (var index = 0; index < 2; index++)
        {
            _ = Assert.IsAssignableFrom<IssueId>(setup.Execute(
                new CreateIssue($"Missing {index}", null, Priority.From(1), null, null)).IssueId);
        }

        var backed = Assert.IsAssignableFrom<IssueId>(setup.Execute(
            new CreateIssue("Backed", null, Priority.From(2), null, null)).IssueId);
        Assert.True(setup.Execute(new AddLabel(backed, "repo:backed")).Success);

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()));

        Assert.Equal(2, claims.Count(claim => claim is not null));
        Assert.Single(claims.Where(claim => claim is not null), claim => claim!.Issue.Repositories.Count == 0);
        Assert.Single(claims.Where(claim => claim is not null), claim => claim!.Issue.Repositories.SequenceEqual(["backed"]));
        var active = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll()).OrderedIssues
            .Where(issue => issue.Status == Status.Active)
            .ToArray();
        Assert.Equal(2, active.Length);
        Assert.Single(active, issue => issue.Repositories.Count == 0);
    }

    [Fact]
    public async Task ConcurrentClaims_ResetStaleCodexReservationOnlyOnce()
    {
        var now = new DateTime(2026, 2, 14, 12, 0, 0, DateTimeKind.Utc);
        var staleAt = now.AddHours(-24);
        var store = new SqliteEventStore(_dbPath);
        var staleId = new IssueId(Guid.NewGuid());
        var candidateId = new IssueId(Guid.NewGuid());
        store.Append(new IssueCreated(Guid.NewGuid(), staleId, staleAt.AddMinutes(-1), "Stale", null, Status.Backlog, Priority.From(3), null, null));
        store.Append(new LabelAdded(Guid.NewGuid(), staleId, staleAt, "repo:shared"));
        store.Append(new StatusChanged(Guid.NewGuid(), staleId, staleAt, Status.Active));
        store.Append(new CommentAdded(Guid.NewGuid(), staleId, staleAt, "Reservation owner: codexThreadId=stale", "agent"));
        store.Append(new IssueCreated(Guid.NewGuid(), candidateId, staleAt.AddMinutes(-1), "Candidate", null, Status.Next, Priority.From(1), null, null));
        store.Append(new LabelAdded(Guid.NewGuid(), candidateId, staleAt, "repo:shared"));

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), new FrozenClockForSqliteReservations(now)).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), new FrozenClockForSqliteReservations(now)).ClaimNext()));

        Assert.Single(claims, claim => claim is not null);
        Assert.Single(claims, claim => claim is null);
        var events = store.LoadAll();
        Assert.Equal(1, events.Count(issueEvent => issueEvent is StatusChanged status &&
            status.IssueId == staleId && status.NewStatus == Status.Next));
        Assert.Equal(1, events.Count(issueEvent => issueEvent is CommentAdded comment &&
            comment.IssueId == staleId && comment.Comment.Contains("24 hours", StringComparison.Ordinal)));
        Assert.Equal(Status.Active, IssueState.Replay(events).Issues[candidateId].Status);
    }

    [Fact]
    public async Task ConcurrentHierarchicalClaims_SelectDisjointChildrenWithoutOverlap()
    {
        var clock = new FrozenClockForSqliteReservations(new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc));
        var setup = new IssueEngine(new SqliteEventStore(_dbPath), clock);
        var parent = Assert.IsAssignableFrom<IssueId>(setup.Execute(
            new CreateIssue("Parent", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var firstChild = Assert.IsAssignableFrom<IssueId>(setup.Execute(
            new CreateIssue("First child", null, Priority.From(1), parent, null)).IssueId);
        var secondChild = Assert.IsAssignableFrom<IssueId>(setup.Execute(
            new CreateIssue("Second child", null, Priority.From(2), parent, null)).IssueId);
        Assert.True(setup.Execute(new AddLabel(firstChild, "repo:left")).Success);
        Assert.True(setup.Execute(new AddLabel(secondChild, "repo:right")).Success);

        var claims = await Task.WhenAll(
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()),
            Task.Run(() => new IssueEngine(new SqliteEventStore(_dbPath), clock).ClaimNext()));

        Assert.Equal(2, claims.Count(claim => claim is not null));
        Assert.Equal(
            new[] { firstChild, secondChild }.OrderBy(id => id.Value).ToArray(),
            claims.Where(claim => claim is not null).Select(claim => claim!.Issue.Id).OrderBy(id => id.Value).ToArray());
        var activeRepositories = IssueState.Replay(new SqliteEventStore(_dbPath).LoadAll()).OrderedIssues
            .Where(issue => issue.Status == Status.Active)
            .SelectMany(issue => issue.Repositories)
            .ToArray();
        Assert.Equal(["left", "right"], activeRepositories.OrderBy(repository => repository, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Ignore cleanup failures in temp directory.
        }
    }
}

file sealed class FrozenClockForSqliteReservations : IClock
{
    public FrozenClockForSqliteReservations(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; }
}
