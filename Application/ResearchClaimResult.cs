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

    public static bool IsAttempt(IssueComment comment)
        => string.Equals(comment.Actor, Actor, StringComparison.Ordinal)
            && string.Equals(comment.Comment, MarkerComment, StringComparison.Ordinal);

    public static bool HasAttempt(Issue issue)
        => issue.Comments.Any(IsAttempt);

    public static DateTime? LatestAttemptUtc(Issue issue)
    {
        var latest = issue.Comments
            .Where(IsAttempt)
            .Select(comment => NormalizeUtc(comment.Timestamp))
            .OrderByDescending(timestamp => timestamp)
            .FirstOrDefault();
        return latest == default ? null : latest;
    }

    public static bool IsEligible(Issue issue, DateTime nowUtc, TimeSpan cooldown)
    {
        if (issue.Status != Status.Blocked)
        {
            return false;
        }

        var cutoff = NormalizeUtc(nowUtc) - cooldown;
        var latestAttempt = LatestAttemptUtc(issue);
        return latestAttempt is null || latestAttempt.Value <= cutoff;
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
