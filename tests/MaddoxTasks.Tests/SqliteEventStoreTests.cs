using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class SqliteEventStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteEventStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"maddoxtasks-tests-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public void AppendAndLoadAll_RoundTripsEvents()
    {
        var issueId = new IssueId(Guid.NewGuid());
        var timestamp = new DateTime(2026, 2, 13, 8, 0, 0, DateTimeKind.Utc);
        var store = new SqliteEventStore(_dbPath);

        store.Append(new IssueCreated(Guid.NewGuid(), issueId, timestamp, "Persist me", null, Status.Backlog, Priority.From(3), null, null));
        store.Append(new StatusChanged(Guid.NewGuid(), issueId, timestamp.AddMinutes(1), Status.Active));

        var loaded = store.LoadAll();

        Assert.Equal(2, loaded.Count);
        Assert.IsType<IssueCreated>(loaded[0]);
        Assert.IsType<StatusChanged>(loaded[1]);

        var replayed = IssueState.Replay(loaded);
        var issue = Assert.Single(replayed.OrderedIssues);
        Assert.Equal("Persist me", issue.Title);
        Assert.Equal(Status.Active, issue.Status);
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

