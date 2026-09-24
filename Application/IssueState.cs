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

    public IssueView GetView(IssueId issueId)
    {
        var issue = _issues[issueId];
        var blockers = issue.BlockerIds
            .Select(blockerId => _issues.TryGetValue(blockerId, out var blocker)
                ? new IssueBlockerView(
                    GetSequence(blockerId),
                    $"#{GetSequence(blockerId)}",
                    blocker.Id.ToShortCode(),
                    blocker.Id.ToString(),
                    blocker.Title,
                    blocker.Status)
                : new IssueBlockerView(0, "missing", blockerId.ToShortCode(), blockerId.ToString(), null, null))
            .ToArray();
        return new IssueView(GetSequence(issueId), issue, blockers);
    }

    public bool HasUnresolvedBlockers(Issue issue)
        => issue.BlockerIds.Any(blockerId =>
            !_issues.TryGetValue(blockerId, out var blocker) || blocker.Status != Status.Done);

    public bool TryFindDependencyCycle(IssueId issueId, IReadOnlyList<IssueId> proposedBlockerIds, out string cycle)
    {
        foreach (var blockerId in proposedBlockerIds)
        {
            if (!TryFindDependencyPath(blockerId, issueId, out var path)) continue;
            cycle = string.Join(" -> ", new[] { issueId }.Concat(path).Select(id =>
            {
                var sequence = GetSequence(id);
                return sequence > 0 ? $"#{sequence}" : id.ToString();
            }));
            return true;
        }

        cycle = string.Empty;
        return false;
    }

    private bool TryFindDependencyPath(IssueId start, IssueId target, out IReadOnlyList<IssueId> path)
    {
        var parentById = new Dictionary<IssueId, IssueId?> { [start] = null };
        var pending = new Stack<IssueId>();
        pending.Push(start);

        while (pending.TryPop(out var current))
        {
            if (current == target)
            {
                var reversed = new List<IssueId>();
                IssueId? cursor = current;
                while (cursor.HasValue)
                {
                    reversed.Add(cursor.Value);
                    cursor = parentById[cursor.Value];
                }

                reversed.Reverse();
                path = reversed;
                return true;
            }

            if (!_issues.TryGetValue(current, out var issue)) continue;
            foreach (var dependency in issue.BlockerIds.OrderByDescending(GetSequence))
            {
                if (parentById.TryAdd(dependency, current)) pending.Push(dependency);
            }
        }

        path = [];
        return false;
    }

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

public sealed record IssueView(
    int Sequence,
    Issue Issue,
    IReadOnlyList<IssueBlockerView>? BlockedBy = null)
{
    public string ShortId => $"#{Sequence}";
    public string GuidPrefix => Issue.Id.ToShortCode();
}

public sealed record IssueBlockerView(
    int Sequence,
    string ShortId,
    string GuidPrefix,
    string IssueId,
    string? Title,
    Status? Status)
{
    public bool IsSatisfied => Status == Domain.Status.Done;

    public string Reason => Status switch
    {
        null => $"Blocker {IssueId} is missing from the task ledger; remove it or restore the missing task.",
        Domain.Status.Done => string.Empty,
        Domain.Status.Rejected => $"Blocker {ShortId} '{Title}' is Rejected; resolve or remove this dependency.",
        _ => $"Blocker {ShortId} '{Title}' is {Status.Value.ToDisplayString()}; complete it or remove this dependency."
    };
}
