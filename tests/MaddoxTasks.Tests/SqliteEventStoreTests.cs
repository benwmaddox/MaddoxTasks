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

