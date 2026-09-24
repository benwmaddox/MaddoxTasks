using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public abstract record Command;

public sealed record CreateIssue(
    string Title,
    string? Description,
    Priority Priority,
    IssueId? ParentId,
    DateTime? DueDate,
    Status Status = Status.Next,
    IReadOnlyList<string>? BlockedBy = null
) : Command;

public sealed record ChangeStatus(
    IssueId IssueId,
    Status NewStatus
) : Command;

public sealed record ChangePriority(
    IssueId IssueId,
    Priority NewPriority
) : Command;

public sealed record AddLabel(
    IssueId IssueId,
    string Label
) : Command;

public sealed record RemoveLabel(
    IssueId IssueId,
    string Label
) : Command;

public sealed record SetRepositoryLabels(IssueId IssueId, IReadOnlyList<string> Repositories) : Command;

public sealed record SetCheckout(IssueId IssueId, string Checkout) : Command;

/// <summary>Replaces every blocker dependency using task tokens resolved in the command snapshot.</summary>
public sealed record SetBlockers(IssueId IssueId, IReadOnlyList<string> BlockedBy) : Command;

public sealed record SplitIssueChild(string Title, string Description, string Repository);

public sealed record SplitIssue(IssueId IssueId, IReadOnlyList<SplitIssueChild> Children);

public sealed record UpdateDescription(
    IssueId IssueId,
    string Description,
    string Actor = "user"
) : Command;

public sealed record AddComment(
    IssueId IssueId,
    string Comment,
    string Actor = "user"
) : Command;
