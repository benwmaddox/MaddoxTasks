using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public sealed class IssueState
{
    private readonly Dictionary<IssueId, Issue> _issues;
    private readonly List<IssueId> _creationOrder;
    private readonly Dictionary<IssueId, int> _sequenceById;

    private IssueState(Dictionary<IssueId, Issue> issues, List<IssueId> creationOrder)
    {
        _issues = issues;
        _creationOrder = creationOrder;
        _sequenceById = creationOrder
            .Select((id, index) => new { id, sequence = index + 1 })
            .ToDictionary(item => item.id, item => item.sequence);
    }

    public IReadOnlyDictionary<IssueId, Issue> Issues => _issues;

    public IReadOnlyList<Issue> OrderedIssues => _creationOrder.Select(id => _issues[id]).ToArray();

    public static IssueState Replay(IEnumerable<IssueEvent> events)
    {
        var issues = new Dictionary<IssueId, Issue>();
        var creationOrder = new List<IssueId>();

        foreach (var issueEvent in events)
        {
            if (!issues.TryGetValue(issueEvent.IssueId, out var issue))
            {
                if (issueEvent is not IssueCreated)
                {
                    throw new InvalidOperationException($"Issue '{issueEvent.IssueId}' does not exist for event '{issueEvent.GetType().Name}'.");
                }

                issue = Issue.CreateShell(issueEvent.IssueId);
                issues.Add(issueEvent.IssueId, issue);
                creationOrder.Add(issueEvent.IssueId);
            }

            issue.Apply(issueEvent);
        }

        return new IssueState(issues, creationOrder);
    }

    public bool TryGetIssue(IssueId issueId, out Issue issue) => _issues.TryGetValue(issueId, out issue!);

    public int GetSequence(IssueId issueId) => _sequenceById.TryGetValue(issueId, out var sequence) ? sequence : 0;

    public bool TryResolveIssueToken(string token, out IssueId issueId, out string error)
    {
        issueId = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Issue id token is empty.";
            return false;
        }

        token = token.Trim();

        if (int.TryParse(token, out var sequence))
        {
            if (sequence < 1 || sequence > _creationOrder.Count)
            {
                error = $"No issue exists with sequence '{sequence}'.";
                return false;
            }

            issueId = _creationOrder[sequence - 1];
            return true;
        }

        if (IssueId.TryParse(token, out var parsedIssueId))
        {
            if (!_issues.ContainsKey(parsedIssueId))
            {
                error = $"Issue '{token}' was not found.";
                return false;
            }

            issueId = parsedIssueId;
            return true;
        }

        var matches = _creationOrder
            .Where(id =>
                id.Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture).StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                id.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            error = $"Issue token '{token}' did not match any issue.";
            return false;
        }

        if (matches.Length > 1)
        {
            error = $"Issue token '{token}' is ambiguous.";
            return false;
        }

        issueId = matches[0];
        return true;
    }
}

public sealed record IssueView(int Sequence, Issue Issue)
{
    public string ShortId => $"#{Sequence}";
    public string GuidPrefix => Issue.Id.ToShortCode();
}

