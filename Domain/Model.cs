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
    ReadyForReview,
    Done,
    Rejected
}

public static class StatusText
{
    public static bool HoldsRepositoryReservation(this Status status)
        => status is Status.Active or Status.ReadyForReview;

    public static bool IsTerminal(this Status status)
        => status is Status.Done or Status.Rejected;

    public static string ToDisplayString(this Status status)
        => status switch
        {
            Status.ReadyForReview => "Ready for Review",
            _ => status.ToString()
        };

    public static bool TryParse(string? input, out Status status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (Enum.TryParse<Status>(trimmed, ignoreCase: true, out status))
        {
            return true;
        }

        var normalizedInput = Normalize(trimmed);
        foreach (var candidate in Enum.GetValues<Status>())
        {
            if (Normalize(candidate.ToString()) == normalizedInput)
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_')
            {
                continue;
            }

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer, 0, index);
    }
}

public static class RepositoryLabels
{
    public const string Prefix = "repo:";
    public const string MissingReservation = "missing";

    public static string Normalize(string repository)
    {
        var normalized = repository.Trim().ToLowerInvariant();
        if (normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            normalized = normalized[Prefix.Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Repository name cannot be empty.", nameof(repository));
        }

        return normalized;
    }

    public static string ToLabel(string repository) => Prefix + Normalize(repository);

    public static bool TryGetRepository(string label, out string repository)
    {
        repository = string.Empty;
        var normalized = label.Trim().ToLowerInvariant();
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = normalized[Prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        repository = value;
        return true;
    }

    public static bool Overlaps(IEnumerable<string> left, IEnumerable<string> right)
        => left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any();

    public static IReadOnlyList<string> GetReservationKeys(IEnumerable<string> repositories)
    {
        var keys = repositories
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static repository => repository, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return keys.Length == 0 ? [MissingReservation] : keys;
    }
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
    private readonly List<IssueComment> _comments = [];

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
    public IReadOnlyList<string> Repositories => _labels
        .Select(label => RepositoryLabels.TryGetRepository(label, out var repository) ? repository : null)
        .Where(static repository => repository is not null)
        .Select(static repository => repository!)
        .OrderBy(static repository => repository, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<IssueComment> Comments => _comments.ToArray();
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
            case CommentAdded commentAdded:
                _comments.Add(new IssueComment(commentAdded.Timestamp, commentAdded.Comment, commentAdded.Actor));
                UpdatedAt = commentAdded.Timestamp;
                break;
            default:
                throw new InvalidOperationException($"Unknown event type '{issueEvent.GetType().Name}'.");
        }
    }
}

public sealed record IssueComment(DateTime Timestamp, string Comment, string Actor);

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

