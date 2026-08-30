using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Application;

public sealed class IssueEngine
{
    private static readonly TimeSpan StaleCodexReservationAge = TimeSpan.FromHours(24);
    private const string ReservationOwnerCommentPrefix = "Reservation owner: codexThreadId=";
    private const string StaleCodexReservationResetComment =
        "Automatic reset: Active Codex reservation had no task change for 24 hours; returned to Next.";

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

    public RequeueBlockedResult RequeueBlocked(bool dryRun = false)
    {
        try
        {
            return _eventStore.ExecuteAtomic(events =>
            {
                var state = IssueState.Replay(events);
                var changedIssueIds = state.OrderedIssues
                    .Where(issue => issue.Status == Status.Blocked)
                    .Select(issue => issue.Id)
                    .ToArray();
                var skippedIssueIds = state.OrderedIssues
                    .Where(issue => issue.Status != Status.Blocked)
                    .Select(issue => issue.Id)
                    .ToArray();
                var timestamp = NormalizeUtc(_clock.UtcNow);
                var plannedEvents = changedIssueIds
                    .Select(issueId => (IssueEvent)new StatusChanged(Guid.NewGuid(), issueId, timestamp, Status.Next))
                    .ToArray();
                var action = dryRun ? "would be requeued" : "requeued";
                var result = new RequeueBlockedResult(
                    true,
                    $"{changedIssueIds.Length} blocked issue(s) {action} to Next.",
                    dryRun,
                    changedIssueIds,
                    skippedIssueIds);

                return new EventStoreOperation<RequeueBlockedResult>(dryRun ? [] : plannedEvents, result);
            });
        }
        catch (Exception exception)
        {
            return new RequeueBlockedResult(
                false,
                $"Unexpected failure: {exception.Message}",
                dryRun,
                [],
                []);
        }
    }

    public IssueView? ClaimNext(bool dryRun = false)
    {
        return _eventStore.ExecuteAtomic(events =>
            {
                var now = NormalizeUtc(_clock.UtcNow);
                var state = IssueState.Replay(events);

                var cleanupEvents = new List<IssueEvent>();
                var staleCutoff = now - StaleCodexReservationAge;
                foreach (var issue in state.OrderedIssues
                             .Where(issue => issue.Status == Status.Active)
                             .Where(issue => NormalizeUtc(issue.UpdatedAt) <= staleCutoff)
                             .Where(issue => HasCurrentCodexReservation(issue, events)))
                {
                    var resetEvent = new StatusChanged(Guid.NewGuid(), issue.Id, now, Status.Next);
                    var auditEvent = new CommentAdded(
                        Guid.NewGuid(),
                        issue.Id,
                        now,
                        StaleCodexReservationResetComment,
                        "agent");
                    cleanupEvents.Add(resetEvent);
                    cleanupEvents.Add(auditEvent);
                    issue.Apply(resetEvent);
                    issue.Apply(auditEvent);
                }

                var activeRepositories = state.OrderedIssues
                    .Where(issue => issue.Status.HoldsRepositoryReservation())
                    .SelectMany(issue => issue.Repositories)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var candidate = state.SelectHierarchical(
                    issue => issue.Status == Status.Next &&
                             issue.Repositories.Count > 0 &&
                             !issue.Repositories.Any(activeRepositories.Contains));

                if (candidate is null)
                {
                    return new EventStoreOperation<IssueView?>(dryRun ? [] : cleanupEvents, null);
                }

                var plannedEvent = new StatusChanged(
                    Guid.NewGuid(),
                    candidate.Id,
                    now,
                    Status.Active);
                if (!dryRun)
                {
                    candidate.Apply(plannedEvent);
                }

                return new EventStoreOperation<IssueView?>(
                    dryRun ? [] : [.. cleanupEvents, plannedEvent],
                    new IssueView(state.GetSequence(candidate.Id), candidate));
            });
    }

    private static bool HasCurrentCodexReservation(Issue issue, IReadOnlyList<IssueEvent> events)
    {
        var latestActivation = events
            .Where(issueEvent => issueEvent.IssueId == issue.Id)
            .OfType<StatusChanged>()
            .Where(statusChanged => statusChanged.NewStatus == Status.Active)
            .Select(statusChanged => NormalizeUtc(statusChanged.Timestamp))
            .LastOrDefault();

        if (latestActivation == default)
        {
            return false;
        }

        return issue.Comments.Any(comment =>
            NormalizeUtc(comment.Timestamp) >= latestActivation &&
            IsCodexReservationComment(comment.Comment));
    }

    private static bool IsCodexReservationComment(string comment)
    {
        if (!comment.StartsWith(ReservationOwnerCommentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = comment[ReservationOwnerCommentPrefix.Length..];
        var separatorIndex = value.IndexOf(';');
        if (separatorIndex >= 0)
        {
            value = value[..separatorIndex];
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.Kind == DateTimeKind.Local
                ? timestamp.ToUniversalTime()
                : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

    public ConditionalStatusChangeResult TryCompleteReadyForReview(IssueId issueId, bool dryRun = false)
    {
        return _eventStore.ExecuteAtomic(events =>
        {
            var state = IssueState.Replay(events);
            if (!state.TryGetIssue(issueId, out var issue))
            {
                return new EventStoreOperation<ConditionalStatusChangeResult>([], ConditionalStatusChangeResult.NotFound);
            }

            if (issue.Status != Status.ReadyForReview)
            {
                return new EventStoreOperation<ConditionalStatusChangeResult>([], ConditionalStatusChangeResult.AlreadyChanged);
            }

            var plannedEvent = new StatusChanged(
                Guid.NewGuid(),
                issueId,
                DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc),
                Status.Done);
            if (dryRun)
            {
                return new EventStoreOperation<ConditionalStatusChangeResult>([], ConditionalStatusChangeResult.WouldClose);
            }

            return new EventStoreOperation<ConditionalStatusChangeResult>([plannedEvent], ConditionalStatusChangeResult.Closed);
        });
    }
}

public enum ConditionalStatusChangeResult
{
    Closed,
    WouldClose,
    AlreadyChanged,
    NotFound
}

