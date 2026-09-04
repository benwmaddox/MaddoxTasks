using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaddoxTasks.Worker;

public interface IClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record WorkerConfig(
    int SchemaVersion,
    TimeSpan ClaimInterval,
    int MaxConcurrentCodexProcesses,
    TimeSpan PrPollInterval,
    TimeSpan ClarificationTimeout,
    string PromptFile,
    string Model,
    string ReasoningEffort,
    int RepairMaxAttempts,
    TimeSpan RepairMaxElapsed,
    TimeSpan ReviewQuietPeriod,
    string[] IgnoredChecks,
    string[] AutoMergeRepositories,
    string AutoMergeMethod,
    string MaddoxExe,
    string CodexExe,
    string GhExe,
    string RepoRoot,
    string WorktreeRoot)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static WorkerConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Worker configuration is empty.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Unsupported schemaVersion {SchemaVersion}.");
        if (ClaimInterval <= TimeSpan.Zero) throw new InvalidDataException("claimInterval must be positive.");
        if (PrPollInterval <= TimeSpan.Zero) throw new InvalidDataException("prPollInterval must be positive.");
        if (ClarificationTimeout <= TimeSpan.Zero) throw new InvalidDataException("clarificationTimeout must be positive.");
        if (MaxConcurrentCodexProcesses < 1) throw new InvalidDataException("maxConcurrentCodexProcesses must be at least 1.");
        if (RepairMaxAttempts < 1 || RepairMaxElapsed <= TimeSpan.Zero) throw new InvalidDataException("Repair bounds must be positive.");
        if (ReviewQuietPeriod <= TimeSpan.Zero) throw new InvalidDataException("reviewQuietPeriod must be positive.");
        if (!string.Equals(AutoMergeMethod, "squash", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only squash auto-merge is supported.");
        foreach (var value in new[] { PromptFile, Model, ReasoningEffort, MaddoxExe, CodexExe, GhExe, RepoRoot, WorktreeRoot })
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Required configuration values cannot be blank.");
        if (!Directory.Exists(RepoRoot)) throw new InvalidDataException($"repoRoot does not exist: {RepoRoot}");
    }
}

public sealed class ConfigState
{
    private readonly object gate = new();
    private WorkerConfig current;
    public ConfigState(WorkerConfig initial) => current = initial;
    public WorkerConfig Current { get { lock (gate) return current; } }
    public bool TryReload(string path, out string? error)
    {
        try { var replacement = WorkerConfig.Load(path); lock (gate) current = replacement; error = null; return true; }
        catch (Exception exception) { error = exception.Message; return false; }
    }
}

public sealed record TaskDto(int Sequence, string IssueId, string Title, string Description, string[] Repositories);
public sealed record Workspace(string Repository, string Directory, string Branch, string Remote, string BaseRef = "");
public sealed record PullRequestState(string Url, string Repository);
public sealed record ReviewFeedback(string ThreadId, string CommentNodeId, long CommentDatabaseId, string Body, string Url);
public sealed record ReviewDisposition(string ThreadId, bool Addressed, string ReplyBody);
public sealed record CheckDisposition(string CheckId, bool Addressed, string Summary);

public sealed class ReviewWindow
{
    public DateTime? GreenSinceUtc { get; set; }
    public bool Closed { get; set; }

    public bool Update(bool green, bool newActionableFeedback, DateTime now, TimeSpan quietPeriod)
    {
        if (Closed) return true;
        if (!green) { GreenSinceUtc = null; return false; }
        if (GreenSinceUtc is null || newActionableFeedback) GreenSinceUtc = now;
        if (now - GreenSinceUtc.Value >= quietPeriod) Closed = true;
        return Closed;
    }
}

public sealed class Job
{
    public required TaskDto Task { get; set; }
    public string Phase { get; set; } = JobPhases.Claimed;
    public DateTime StartedUtc { get; set; }
    public string[] Latest { get; set; } = [];
    public string? ThreadId { get; set; }
    public bool ReservationOwnerRecorded { get; set; }
    public bool ExactReservationOwnerRecorded { get; set; }
    public List<Workspace> Workspaces { get; set; } = [];
    public List<PullRequestState> PullRequests { get; set; } = [];
    public required string Prompt { get; set; }
    public required string Model { get; set; }
    public required string Effort { get; set; }
    public int RepairAttempts { get; set; }
    public DateTime? RepairStartedUtc { get; set; }
    public Dictionary<string, int> RepairAttemptsByPullRequest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> RepairStartedUtcByPullRequest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ProcessedFeedbackIds { get; set; } = new(StringComparer.Ordinal);
    public List<ReviewFeedback> PendingFeedback { get; set; } = [];
    public HashSet<string> ProcessedCheckIds { get; set; } = new(StringComparer.Ordinal);
    public List<CheckState> PendingCheckFailures { get; set; } = [];
    public ReviewWindow ReviewWindow { get; set; } = new();
    public bool ReadyForReviewRecorded { get; set; }
    public string? PendingResultJson { get; set; }
    public bool PendingResultIsRepair { get; set; }
    public Dictionary<string, PublicationProgress> Publication { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExecutionStartHeads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PullRequestCommentRecorded { get; set; }
    public bool CodexResultCommentRecorded { get; set; }
}

public sealed class PublicationProgress
{
    public bool CommitCreated { get; set; }
    public bool Pushed { get; set; }
    public string? PullRequestUrl { get; set; }
}

public static class JobPhases
{
    public const string Claimed = "Claimed";
    public const string Clarifying = "Clarifying repository";
    public const string Implementing = "Implementing";
    public const string Repairing = "Repairing";
    public const string Publishing = "Publishing";
    public const string Monitoring = "Monitoring";
    public const string Blocked = "Blocked";
    public const string Done = "Done";
}

public sealed class Journal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public List<Job> Jobs { get; set; } = [];
    public static Journal Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json) ?? new Journal()
        : new Journal();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, true);
    }
}

public static class RecoveryPlanner
{
    public static IReadOnlyList<Job> JobsToRequeue(Journal journal) => journal.Jobs
        .Where(job => job.Phase is not (JobPhases.Done or JobPhases.Blocked or JobPhases.Monitoring))
        .OrderBy(job => job.StartedUtc)
        .ToArray();

    public static RecoveryMode ModeFor(Job job) => job.Phase switch
    {
        JobPhases.Publishing when !string.IsNullOrWhiteSpace(job.PendingResultJson) => RecoveryMode.Publish,
        JobPhases.Repairing => RecoveryMode.ResumeRepair,
        JobPhases.Implementing when !string.IsNullOrWhiteSpace(job.ThreadId) => RecoveryMode.ResumeInitial,
        JobPhases.Publishing => RecoveryMode.UnrecoverablePublication,
        _ => RecoveryMode.Initial
    };
}

public enum RecoveryMode { Initial, ResumeInitial, ResumeRepair, Publish, UnrecoverablePublication }

public sealed record ClaimSnapshot(WorkerConfig Config, string Prompt);
public static class ClaimAdmission
{
    public static bool TrySnapshot(WorkerConfig config, string configPath, out ClaimSnapshot? snapshot, out string? error)
    {
        try
        {
            var promptPath = Path.IsPathRooted(config.PromptFile) ? config.PromptFile : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, config.PromptFile);
            var prompt = File.ReadAllText(promptPath);
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Prompt file is empty: " + promptPath);
            snapshot = new ClaimSnapshot(config, prompt);
            error = null;
            return true;
        }
        catch (Exception exception) { snapshot = null; error = exception.Message; return false; }
    }
}

public static class ReservationAttribution
{
    public const string Pending = "Reservation owner: codexThreadId=pending";
    public static string Exact(string threadId) => "Reservation owner: codexThreadId=" + threadId;
    public static bool NeedsPending(Job job) => !job.ReservationOwnerRecorded;
    public static bool NeedsExact(Job job) => !job.ExactReservationOwnerRecorded && !string.IsNullOrWhiteSpace(job.ThreadId);
}

public static class PublicationPolicy
{
    public static bool HasTaskCommit(PublicationProgress progress, bool hasUncommittedChanges, bool headChanged) => progress.CommitCreated || hasUncommittedChanges || headChanged;
    public static bool NeedsPush(PublicationProgress progress, string localHead, string? remoteHead) => !string.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase);
    public static bool NeedsPullRequest(PublicationProgress progress, string? recoveredUrl) => string.IsNullOrWhiteSpace(progress.PullRequestUrl) && string.IsNullOrWhiteSpace(recoveredUrl);
}

public sealed class ConcurrencyGate
{
    private readonly Func<int> capacity;
    private int active;
    public ConcurrencyGate(Func<int> capacity) => this.capacity = capacity;
    public int Active => Volatile.Read(ref active);
    public bool TryReserve()
    {
        while (true)
        {
            var observed = Active;
            if (observed >= capacity()) return false;
            if (Interlocked.CompareExchange(ref active, observed + 1, observed) == observed) return true;
        }
    }
    public void Release()
    {
        if (Interlocked.Decrement(ref active) < 0) throw new InvalidOperationException("Concurrency reservation underflow.");
    }
}

public static class FeedbackPolicy
{
    public static IReadOnlyList<ReviewFeedback> AddNew(Job job, IEnumerable<ReviewFeedback> incoming)
    {
        var pending = job.PendingFeedback.Select(item => item.CommentNodeId).ToHashSet(StringComparer.Ordinal);
        var additions = incoming.Where(item => !job.ProcessedFeedbackIds.Contains(item.CommentNodeId) && pending.Add(item.CommentNodeId)).ToArray();
        job.PendingFeedback.AddRange(additions);
        return additions;
    }

    public static IReadOnlyList<ReviewDisposition> ActionsFor(Job job, IEnumerable<ReviewDisposition> dispositions)
    {
        var pendingThreads = job.PendingFeedback.Select(item => item.ThreadId).ToHashSet(StringComparer.Ordinal);
        return dispositions
            .Where(item => item.Addressed && !string.IsNullOrWhiteSpace(item.ReplyBody) && pendingThreads.Contains(item.ThreadId))
            .GroupBy(item => item.ThreadId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(item => !job.ProcessedFeedbackIds.Contains(ActionKey(item.ThreadId)))
            .ToArray();
    }

    public static string ActionKey(string threadId) => "action:" + threadId;
}

public static class ReviewActionLedger
{
    public static string ReplyKey(string threadId) => "reply:" + threadId;
    public static string ResolveKey(string threadId) => "resolve:" + threadId;
    public static bool NeedsReply(Job job, string threadId) => !job.ProcessedFeedbackIds.Contains(ReplyKey(threadId));
    public static bool NeedsResolve(Job job, string threadId) => !job.ProcessedFeedbackIds.Contains(ResolveKey(threadId));
}

public static class DashboardFormatter
{
    private static readonly Regex EscapeSequence = new("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.Compiled);
    private static readonly Regex Controls = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.Compiled);

    public static string[] LatestLines(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : Controls.Replace(EscapeSequence.Replace(text.Replace("\r", string.Empty), string.Empty), string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(3)
            .ToArray();

    public static string Truncate(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length <= width) return text;
        if (width <= 3) return "..."[..width];
        return text[..(width - 3)] + "...";
    }
}

public static class CodexEventParser
{
    public static (string? ThreadId, string? Text) Parse(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;
        var threadId = root.TryGetProperty("thread_id", out var thread) ? thread.GetString() : null;
        string? text = root.TryGetProperty("text", out var directText) ? directText.GetString() : null;
        if (text is null && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var itemText)) text = itemText.GetString();
        if (text is null && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) text = message.GetString();
        return (threadId, text);
    }
}
