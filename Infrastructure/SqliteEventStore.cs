using Microsoft.Data.Sqlite;
using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

public sealed class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;

    public SqliteEventStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, DefaultTimeout = 30 }.ToString();
        Initialize();
    }

    public IReadOnlyList<IssueEvent> LoadAll()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        return LoadAll(connection);
    }

    public T ExecuteAtomic<T>(Func<IReadOnlyList<IssueEvent>, EventStoreOperation<T>> operation)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // BEGIN IMMEDIATE acquires the SQLite writer lock before reading state,
        // so planning and appending cannot race another process.
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        try
        {
            var result = operation(LoadAll(connection));
            foreach (var issueEvent in result.Events)
            {
                Append(connection, issueEvent);
            }

            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
            return result.Result;
        }
        catch
        {
            using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            throw;
        }
    }

    private static IReadOnlyList<IssueEvent> LoadAll(SqliteConnection connection)
    {
        var events = new List<IssueEvent>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventType, Payload
            FROM Events
            ORDER BY rowid;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var eventType = reader.GetString(0);
            var payload = reader.GetString(1);
            events.Add(EventSerializer.Deserialize(eventType, payload));
        }

        return events;
    }

    public void Append(IssueEvent issueEvent)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Append(connection, issueEvent);
    }

    private static void Append(SqliteConnection connection, IssueEvent issueEvent)
    {

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Events (EventId, IssueId, EventType, Payload, Timestamp)
            VALUES ($eventId, $issueId, $eventType, $payload, $timestamp);
            """;

        command.Parameters.AddWithValue("$eventId", issueEvent.EventId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$issueId", issueEvent.IssueId.ToString());
        command.Parameters.AddWithValue("$eventType", issueEvent.GetType().Name);
        command.Parameters.AddWithValue("$payload", EventSerializer.Serialize(issueEvent));
        command.Parameters.AddWithValue("$timestamp", issueEvent.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Events
            (
                EventId TEXT PRIMARY KEY,
                IssueId TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Payload TEXT NOT NULL,
                Timestamp TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Events_IssueId ON Events(IssueId);
            """;
        command.ExecuteNonQuery();
    }
}

