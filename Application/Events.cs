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

public sealed record RepositoryLabelsSet(
    Guid EventId, IssueId IssueId, DateTime Timestamp, string[] Repositories
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record CheckoutSet(
    Guid EventId, IssueId IssueId, DateTime Timestamp, string Checkout
) : IssueEvent(EventId, IssueId, Timestamp);

/// <summary>
/// Replaces the complete set of task dependencies. SchemaVersion is persisted
/// with the event so future dependency representations can replay old ledgers.
/// </summary>
public sealed record IssueBlockersSet(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    int SchemaVersion,
    IssueId[] BlockerIds
) : IssueEvent(EventId, IssueId, Timestamp)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record DescriptionUpdated(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Description,
    string Actor = "user"
) : IssueEvent(EventId, IssueId, Timestamp);

public sealed record CommentAdded(
    Guid EventId,
    IssueId IssueId,
    DateTime Timestamp,
    string Comment,
    string Actor = "user"
) : IssueEvent(EventId, IssueId, Timestamp);
