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

    /// <summary>
    /// Returns issues in deterministic program order. A program is a root issue
    /// and all of its descendants. Programs are ordered by root priority and
    /// sequence, and each program is traversed child-first. Missing and cyclic
    /// parent links are treated as deterministic roots so malformed data cannot
    /// make selection recurse forever.
    /// </summary>
    public IReadOnlyList<Issue> HierarchicalIssues(bool preferActive = false)
    {
        var issues = OrderedIssues;
        var issuesById = issues.ToDictionary(issue => issue.Id);
        var sequenceById = _sequenceById;
        var childrenByParent = issues
            .Where(issue => issue.ParentId.HasValue && issuesById.ContainsKey(issue.ParentId.Value))
            .GroupBy(issue => issue.ParentId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(issue => issue.Priority.Value)
                    .ThenBy(issue => preferActive && issue.Status == Status.Active ? 0 : 1)
                    .ThenBy(issue => sequenceById[issue.Id])
                    .ToArray());

        var rootById = new Dictionary<IssueId, IssueId>();
        foreach (var issue in issues)
        {
            AssignRoot(issue, issuesById, rootById, sequenceById);
        }

        var roots = rootById.Values
            .Distinct()
            .Select(rootId => issuesById[rootId])
            .OrderBy(issue => issue.Priority.Value)
            .ThenBy(issue => preferActive && issue.Status == Status.Active ? 0 : 1)
            .ThenBy(issue => sequenceById[issue.Id])
            .ToArray();

        var result = new List<Issue>(issues.Count);
        var visited = new HashSet<IssueId>();
        foreach (var root in roots)
        {
            var stack = new Stack<(Issue Issue, bool Expanded)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                var (current, expanded) = stack.Pop();
                if (expanded)
                {
                    result.Add(current);
                    continue;
                }

                if (!visited.Add(current.Id))
                {
                    continue;
                }

                stack.Push((current, true));
                if (childrenByParent.TryGetValue(current.Id, out var children))
                {
                    for (var index = children.Length - 1; index >= 0; index--)
                    {
                        stack.Push((children[index], false));
                    }
                }
            }
        }

        // This is only a defensive fallback for malformed state. Every normal
        // issue is reachable from one of the roots above.
        foreach (var issue in issues
                     .Where(issue => !visited.Contains(issue.Id))
                     .OrderBy(issue => issue.Priority.Value)
                     .ThenBy(issue => sequenceById[issue.Id]))
        {
            result.Add(issue);
        }

        return result;
    }

    public Issue? SelectHierarchical(Func<Issue, bool> predicate, bool preferActive = false)
        => HierarchicalIssues(preferActive).FirstOrDefault(predicate);

    private static void AssignRoot(
        Issue issue,
        IReadOnlyDictionary<IssueId, Issue> issuesById,
        IDictionary<IssueId, IssueId> rootById,
        IReadOnlyDictionary<IssueId, int> sequenceById)
    {
        if (rootById.ContainsKey(issue.Id))
        {
            return;
        }

        var path = new List<Issue>();
        var pathIndex = new Dictionary<IssueId, int>();
        var current = issue;
        IssueId rootId;

        while (true)
        {
            if (rootById.TryGetValue(current.Id, out rootId))
            {
                break;
            }

            if (pathIndex.TryGetValue(current.Id, out var cycleStart))
            {
                rootId = path
                    .Skip(cycleStart)
                    .Append(current)
                    .OrderBy(candidate => candidate.Priority.Value)
                    .ThenBy(candidate => sequenceById[candidate.Id])
                    .First()
                    .Id;
                break;
            }

            pathIndex.Add(current.Id, path.Count);
            path.Add(current);
            if (!current.ParentId.HasValue ||
                !issuesById.TryGetValue(current.ParentId.Value, out current!))
            {
                rootId = path[^1].Id;
                break;
            }
        }

        foreach (var pathIssue in path)
        {
            rootById[pathIssue.Id] = rootId;
        }
    }

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

