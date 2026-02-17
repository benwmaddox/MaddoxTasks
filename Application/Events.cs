using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public abstract record IssueEvent(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp
);

public sealed record IssueCreated(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Title,
    string? Description,
    Status Status,
    Priority Priority,
    IssueId? ParentId,
    DateTime? DueDate
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record StatusChanged(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    Status NewStatus
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record PriorityChanged(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    Priority NewPriority
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record LabelAdded(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Label
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record LabelRemoved(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Label
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record DescriptionUpdated(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Description
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record CommentAdded(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Comment
) : IssueEvent(EventId, IssueId, Timestamp);

