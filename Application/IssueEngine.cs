using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Application;

public sealed class IssueEngine
{
    private static readonly Status[] StatusSortOrder =
    [
        Status.Active,
        Status.Next,
        Status.Blocked,
        Status.ReadyForReview,
        Status.Backlog,
        Status.Done,
        Status.Rejected
    ];

    private readonly IEventStore _eventStore;
    private readonly IClock _clock;

    public IssueEngine(IEventStore eventStore, IClock clock)
    {
        _eventStore = eventStore;
        _clock = clock;
    }

    public IssueState GetState() => IssueState.Replay(_eventStore.LoadAll());

    public IReadOnlyList<IssueEvent> GetEventLog() => _eventStore.LoadAll();

    public IReadOnlyList<IssueView> QueryIssues(IssueFilter? filter = null, bool includeDone = true)
    {
        var state = GetState();
        IEnumerable<Issue> issues = state.OrderedIssues;

        if (!includeDone && !(filter?.StatusEquals is { } requestedStatus && requestedStatus.IsTerminal()))
        {
            issues = issues.Where(issue => !issue.Status.IsTerminal());
        }

        if (filter is not null)
        {
            issues = IssueFiltering.ApplyFilter(issues, filter);
        }

        var statusIndex = StatusSortOrder
            .Select((status, index) => new { status, index })
            .ToDictionary(item => item.status, item => item.index);

        return issues
            .OrderBy(issue => statusIndex[issue.Status])
            .ThenBy(issue => state.GetSequence(issue.Id))
            .Select(issue => new IssueView(state.GetSequence(issue.Id), issue))
            .ToArray();
    }

    public bool TryResolveIssueToken(string token, out IssueId issueId, out string error)
        => GetState().TryResolveIssueToken(token, out issueId, out error);

    public CommandExecutionResult Execute(Command command)
    {
        try
        {
            return _eventStore.ExecuteAtomic(events =>
            {
                var before = IssueState.Replay(events);
                var plannedEvent = CommandPlanner.Plan(command, before, _clock.UtcNow);
                return new EventStoreOperation<CommandExecutionResult>(
                    [plannedEvent],
                    CommandExecutionResult.Succeeded(
                        $"{command.GetType().Name} applied.",
                        plannedEvent.IssueId,
                        plannedEvent.EventId));
            });
        }
        catch (CommandValidationException exception)
        {
            return CommandExecutionResult.Failed(exception.Message);
        }
        catch (Exception exception)
        {
            return CommandExecutionResult.Failed($"Unexpected failure: {exception.Message}");
        }
    }

    public IssueView? ClaimNext(bool dryRun = false)
    {
        return _eventStore.ExecuteAtomic(events =>
            {
                var state = IssueState.Replay(events);
                var activeRepositories = state.OrderedIssues
                    .Where(issue => issue.Status.HoldsRepositoryReservation())
                    .SelectMany(issue => issue.Repositories)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var candidate = state.OrderedIssues
                    .Where(issue => issue.Status == Status.Next && issue.Repositories.Count > 0)
                    .Where(issue => !issue.Repositories.Any(activeRepositories.Contains))
                    .OrderBy(issue => issue.Priority.Value)
                    .ThenBy(issue => state.GetSequence(issue.Id))
                    .FirstOrDefault();

                if (candidate is null)
                {
                    return new EventStoreOperation<IssueView?>([], null);
                }

                var plannedEvent = new StatusChanged(
                    Guid.NewGuid(),
                    candidate.Id,
                    DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc),
                    Status.Active);
                if (!dryRun)
                {
                    candidate.Apply(plannedEvent);
                }

                return new EventStoreOperation<IssueView?>(
                    dryRun ? [] : [plannedEvent],
                    new IssueView(state.GetSequence(candidate.Id), candidate));
            });
    }
}

