using System.Globalization;
using MaddoxTasks.Application;

namespace MaddoxTasks.Domain;

public readonly record struct IssueId(Guid Value)
{
    public static IssueId New() => new(Guid.NewGuid());

    public static bool TryParse(string? input, out IssueId issueId)
    {
        if (Guid.TryParse(input, out var guid))
        {
            issueId = new IssueId(guid);
            return true;
        }

        issueId = default;
        return false;
    }

    public string ToShortCode() => Value.ToString("N", CultureInfo.InvariantCulture)[..8];

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public enum Status
{
    Backlog,
    Next,
    Active,
    Blocked,
    Done
}

public readonly record struct Priority(int Value)
{
    public static Priority From(int value)
    {
        if (value < 1 || value > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Priority must be between 1 and 5.");
        }

        return new Priority(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class Issue
{
    private readonly HashSet<string> _labels = new(StringComparer.OrdinalIgnoreCase);

    private Issue(IssueId id)
    {
        Id = id;
        Title = string.Empty;
        Description = string.Empty;
        Status = Status.Backlog;
        Priority = Priority.From(3);
        CreatedAt = DateTime.MinValue;
        UpdatedAt = DateTime.MinValue;
    }

    public IssueId Id { get; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public Status Status { get; private set; }
    public Priority Priority { get; private set; }
    public IssueId? ParentId { get; private set; }
    public IReadOnlyCollection<string> Labels => _labels.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DueDate { get; private set; }

    internal static Issue CreateShell(IssueId id) => new(id);

    public bool HasLabel(string label) => _labels.Contains(label);

    public void Apply(IssueEvent issueEvent)
    {
        switch (issueEvent)
        {
            case IssueCreated created:
                Title = created.Title;
                Description = created.Description ?? string.Empty;
                Status = created.Status;
                Priority = created.Priority;
                ParentId = created.ParentId;
                DueDate = created.DueDate;
                CreatedAt = created.Timestamp;
                UpdatedAt = created.Timestamp;
                break;
            case StatusChanged statusChanged:
                Status = statusChanged.NewStatus;
                UpdatedAt = statusChanged.Timestamp;
                break;
            case PriorityChanged priorityChanged:
                Priority = priorityChanged.NewPriority;
                UpdatedAt = priorityChanged.Timestamp;
                break;
            case LabelAdded labelAdded:
                _labels.Add(labelAdded.Label);
                UpdatedAt = labelAdded.Timestamp;
                break;
            case LabelRemoved labelRemoved:
                _labels.Remove(labelRemoved.Label);
                UpdatedAt = labelRemoved.Timestamp;
                break;
            case DescriptionUpdated descriptionUpdated:
                Description = descriptionUpdated.Description;
                UpdatedAt = descriptionUpdated.Timestamp;
                break;
            default:
                throw new InvalidOperationException($"Unknown event type '{issueEvent.GetType().Name}'.");
        }
    }
}

public sealed class IssueFilter
{
    public Status? StatusEquals { get; init; }
    public Status? StatusNotEquals { get; init; }
    public int? PriorityLessThanOrEqual { get; init; }
    public IReadOnlyList<string>? MustHaveLabels { get; init; }
    public DateTime? DueBefore { get; init; }
}

public static class IssueFiltering
{
    public static IEnumerable<Issue> ApplyFilter(IEnumerable<Issue> issues, IssueFilter filter)
    {
        var query = issues;

        if (filter.StatusEquals.HasValue)
        {
            query = query.Where(issue => issue.Status == filter.StatusEquals.Value);
        }

        if (filter.StatusNotEquals.HasValue)
        {
            query = query.Where(issue => issue.Status != filter.StatusNotEquals.Value);
        }

        if (filter.PriorityLessThanOrEqual.HasValue)
        {
            query = query.Where(issue => issue.Priority.Value <= filter.PriorityLessThanOrEqual.Value);
        }

        if (filter.MustHaveLabels is { Count: > 0 })
        {
            foreach (var label in filter.MustHaveLabels)
            {
                var normalized = NormalizeLabel(label);
                query = query.Where(issue => issue.HasLabel(normalized));
            }
        }

        if (filter.DueBefore.HasValue)
        {
            var cutoff = filter.DueBefore.Value.Date;
            query = query.Where(issue => issue.DueDate.HasValue && issue.DueDate.Value.Date <= cutoff);
        }

        return query;
    }

    internal static string NormalizeLabel(string label) => label.Trim().ToLowerInvariant();
}

