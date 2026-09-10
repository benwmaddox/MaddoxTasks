using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

/// <summary>
/// The durable marker used by the blocked-task research worker. The marker is
/// deliberately a normal task comment so every process that can read the task
/// ledger observes the same cooldown state.
/// </summary>
public static class ResearchClaimPolicy
{
    public const string Actor = "maddox-research-worker";
    public const string MarkerComment = "Research attempt recorded by maddox-research-worker.";
    public const string FailureMarkerPrefix = "Research worker could not complete: ";
    public static readonly TimeSpan InitialBlockedDelay = TimeSpan.FromHours(12);
    public static readonly TimeSpan SubsequentBlockedDelay = TimeSpan.FromDays(14);

    public static bool IsAttempt(IssueComment comment)
        => string.Equals(comment.Actor, Actor, StringComparison.Ordinal)
            && string.Equals(comment.Comment, MarkerComment, StringComparison.Ordinal);

    public static bool HasAttempt(Issue issue)
        => issue.Comments.Any(IsAttempt);

    public static DateTime? LatestAttemptUtc(Issue issue)
    {
        for (var index = issue.Comments.Count - 1; index >= 0; index--)
        {
            if (IsAttempt(issue.Comments[index]))
            {
                return NormalizeUtc(issue.Comments[index].Timestamp);
            }
        }

        return null;
    }

    public static bool IsEligible(Issue issue, IEnumerable<IssueEvent> events, DateTime nowUtc)
    {
        if (issue.Status != Status.Blocked)
        {
            return false;
        }

        var issueEvents = events.Where(item => item.IssueId == issue.Id).ToArray();
        var blockedEntries = issueEvents
            .Where(item => item is IssueCreated { Status: Status.Blocked } or StatusChanged { NewStatus: Status.Blocked })
            .ToArray();
        if (blockedEntries.Length == 0) return false;

        var latestBlockedUtc = NormalizeUtc(blockedEntries[^1].Timestamp);
        var latestAttemptUtc = LatestAttemptUtc(issue);
        var hasAttemptInCurrentBlockedPeriod = latestAttemptUtc >= latestBlockedUtc;
        var anchorUtc = hasAttemptInCurrentBlockedPeriod ? latestAttemptUtc!.Value : latestBlockedUtc;
        var delay = blockedEntries.Length == 1 && !hasAttemptInCurrentBlockedPeriod
            ? InitialBlockedDelay
            : SubsequentBlockedDelay;

        return anchorUtc <= NormalizeUtc(nowUtc) - delay;
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.Kind == DateTimeKind.Local
                ? timestamp.ToUniversalTime()
                : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
}

public sealed record ResearchClaimResult(
    bool Success,
    string Message,
    bool DryRun,
    IssueView? Task,
    DateTime? LastAttemptUtc);

public enum ResearchCompletionStatus
{
    Advanced,
    WouldAdvance,
    NotFound,
    NotBlocked,
    NotResearchClaimed
}

public sealed record ResearchCompletionResult(
    bool Success,
    string Message,
    bool DryRun,
    ResearchCompletionStatus Status,
    IssueView? Task);
