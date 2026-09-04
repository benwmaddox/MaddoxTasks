using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public abstract record Command;

public sealed record CreateIssue(
    string Title,
    string? Description,
    Priority Priority,
    IssueId? ParentId,
    DateTime? DueDate,
    Status Status = Status.Next
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

