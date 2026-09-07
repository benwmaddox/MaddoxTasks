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

    public static bool IsAttempt(IssueComment comment)
        => string.Equals(comment.Actor, Actor, StringComparison.Ordinal)
            && string.Equals(comment.Comment, MarkerComment, StringComparison.Ordinal);

    public static bool HasAttempt(Issue issue)
        => issue.Comments.Any(IsAttempt);

    public static bool IsFailure(IssueComment comment)
        => string.Equals(comment.Actor, Actor, StringComparison.Ordinal)
            && comment.Comment.StartsWith(FailureMarkerPrefix, StringComparison.Ordinal);

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

    public static DateTime? LatestAttemptFailureUtc(Issue issue)
    {
        var comments = issue.Comments;
        var latestAttemptIndex = -1;
        for (var index = comments.Count - 1; index >= 0; index--)
        {
            if (IsAttempt(comments[index]))
            {
                latestAttemptIndex = index;
                break;
            }
        }

        if (latestAttemptIndex < 0)
        {
            return null;
        }

        DateTime? latestFailure = null;
        for (var index = latestAttemptIndex + 1; index < comments.Count; index++)
        {
            if (IsFailure(comments[index]))
            {
                latestFailure = NormalizeUtc(comments[index].Timestamp);
            }
        }

        return latestFailure;
    }

    public static bool IsEligible(Issue issue, DateTime nowUtc, TimeSpan cooldown, TimeSpan failureCooldown)
    {
        if (issue.Status != Status.Blocked)
        {
            return false;
        }

        var latestAttempt = LatestAttemptUtc(issue);
        if (latestAttempt is null)
        {
            return true;
        }

        var latestFailure = LatestAttemptFailureUtc(issue);
        var effectiveCooldown = latestFailure is null ? cooldown : failureCooldown;
        var cooldownStartedAt = latestFailure ?? latestAttempt.Value;
        return cooldownStartedAt <= NormalizeUtc(nowUtc) - effectiveCooldown;
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
