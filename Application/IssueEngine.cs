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
        => ExecuteWithLabels(command, []);

    public CommandExecutionResult Execute(CreateIssue command, IReadOnlyList<string> labels)
        => ExecuteWithLabels(command, labels);

    private CommandExecutionResult ExecuteWithLabels(Command command, IReadOnlyList<string> labels)
    {
        try
        {
            return _eventStore.ExecuteAtomic(events =>
            {
                var before = IssueState.Replay(events);
                var plannedEvent = CommandPlanner.Plan(command, before, _clock.UtcNow);
                var plannedEvents = new List<IssueEvent> { plannedEvent };
                foreach (var label in labels.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var state = IssueState.Replay(events.Concat(plannedEvents).ToArray());
                    var normalized = IssueFiltering.NormalizeLabel(label);
                    if (state.Issues[plannedEvent.IssueId].HasLabel(normalized)) continue;
                    plannedEvents.Add(CommandPlanner.Plan(new AddLabel(plannedEvent.IssueId, label), state, _clock.UtcNow));
                }
                return new EventStoreOperation<CommandExecutionResult>(
                    plannedEvents,
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

    public SplitIssueResult ExecuteSplitIssue(SplitIssue command)
    {
        try
        {
            return _eventStore.ExecuteAtomic(events =>
            {
                var state = IssueState.Replay(events);
                if (!state.TryGetIssue(command.IssueId, out var parent))
                    throw new CommandValidationException($"Issue '{command.IssueId}' was not found.");
                if (parent.Status.IsTerminal())
                    throw new CommandValidationException("A terminal issue cannot be split.");
                if (command.Children.Count < 2)
                    throw new CommandValidationException("A split requires at least two child issues.");

                var children = command.Children.Select(child =>
                {
                    var title = child.Title?.Trim() ?? string.Empty;
                    var description = TextNormalization.NormalizeLineBreaks(child.Description?.Trim() ?? string.Empty);
                    var repository = child.Repository?.Trim() ?? string.Empty;
                    if (title.Length == 0) throw new CommandValidationException("Every split child requires a title.");
                    if (description.Length == 0) throw new CommandValidationException("Every split child requires a description.");
                    if (repository.Length == 0) throw new CommandValidationException("Every split child requires exactly one repository.");
                    if (repository.StartsWith(RepositoryLabels.Prefix, StringComparison.OrdinalIgnoreCase) || repository.Any(char.IsControl))
                        throw new CommandValidationException("Child repositories must be names without the 'repo:' prefix.");
                    return new SplitIssueChild(title, description, RepositoryLabels.Normalize(repository));
                }).ToArray();

                var duplicate = children.GroupBy(child => child.Repository, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1)?.Key;
                if (duplicate is not null)
                    throw new CommandValidationException($"Repository '{duplicate}' may belong to only one split child.");

                var requested = children.Select(child => child.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var reserved in state.OrderedIssues.Where(issue => issue.Id != parent.Id && issue.Status.HoldsRepositoryReservation()))
                {
                    var overlap = RepositoryLabels.GetReservationKeys(reserved.Repositories).FirstOrDefault(requested.Contains);
                    if (overlap is not null)
                        throw new CommandValidationException($"Cannot assign repository scope '{overlap}': it is already reserved by reserving issue {reserved.Id}.");
                }

                var timestamp = NormalizeUtc(_clock.UtcNow);
                var planned = new List<IssueEvent>();
                foreach (var child in children)
                {
                    var childId = IssueId.New();
                    planned.Add(new IssueCreated(Guid.NewGuid(), childId, timestamp, child.Title, child.Description, Status.Next, parent.Priority, parent.Id, null));
                    planned.Add(new RepositoryLabelsSet(Guid.NewGuid(), childId, timestamp, [child.Repository]));
                }
                planned.Add(new StatusChanged(Guid.NewGuid(), parent.Id, timestamp, Status.Done));

                var after = IssueState.Replay(events.Concat(planned).ToArray());
                var parentView = new IssueView(after.GetSequence(parent.Id), after.Issues[parent.Id]);
                var childViews = planned.OfType<IssueCreated>()
                    .Select(created => new IssueView(after.GetSequence(created.IssueId), after.Issues[created.IssueId]))
                    .ToArray();
                return new EventStoreOperation<SplitIssueResult>(planned,
                    new SplitIssueResult(true, $"Issue split into {childViews.Length} child issues.", parentView, childViews));
            });
        }
        catch (CommandValidationException exception)
        {
            return new SplitIssueResult(false, exception.Message, null, []);
        }
        catch (Exception exception)
        {
            return new SplitIssueResult(false, $"Unexpected failure: {exception.Message}", null, []);
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

    /// <summary>
    /// Atomically reserve one blocked issue for the research worker by adding
    /// the durable research-attempt comment. The issue remains Blocked until a
    /// worker has applied and validated a complete unblocking plan.
    /// </summary>
    public ResearchClaimResult ResearchClaimBlocked(TimeSpan? cooldown = null, bool dryRun = false)
    {
        var effectiveCooldown = cooldown ?? TimeSpan.FromDays(14);
        if (effectiveCooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "Research cooldown must be positive.");
        }

        return _eventStore.ExecuteAtomic(events =>
        {
            var now = NormalizeUtc(_clock.UtcNow);
            var state = IssueState.Replay(events);
            var candidate = state.HierarchicalIssues()
                .FirstOrDefault(issue => ResearchClaimPolicy.IsEligible(issue, now, effectiveCooldown));

            if (candidate is null)
            {
                return new EventStoreOperation<ResearchClaimResult>(
                    [],
                    new ResearchClaimResult(
                        true,
                        "No eligible blocked issue is available for research.",
                        dryRun,
                        null,
                        null));
            }

            var marker = new CommentAdded(
                Guid.NewGuid(),
                candidate.Id,
                now,
                ResearchClaimPolicy.MarkerComment,
                ResearchClaimPolicy.Actor);

            if (!dryRun)
            {
                candidate.Apply(marker);
            }

            return new EventStoreOperation<ResearchClaimResult>(
                dryRun ? [] : [marker],
                new ResearchClaimResult(
                    true,
                    dryRun
                        ? $"Blocked issue {candidate.Id} is eligible for research."
                        : $"Blocked issue {candidate.Id} claimed for research.",
                    dryRun,
                    new IssueView(state.GetSequence(candidate.Id), candidate),
                    ResearchClaimPolicy.LatestAttemptUtc(candidate)));
            });
    }

    /// <summary>
    /// Complete a research claim only while its source issue is still Blocked.
    /// The claim marker is required so this narrow operation cannot be used as
    /// a general status-change bypass by another agent command.
    /// </summary>
    public ResearchCompletionResult TryCompleteResearch(IssueId issueId, bool dryRun = false, Status completionStatus = Status.Next)
    {
        if (completionStatus is not (Status.Next or Status.Done))
        {
            throw new ArgumentOutOfRangeException(nameof(completionStatus), "Research completion status must be Next or Done.");
        }

        return _eventStore.ExecuteAtomic(events =>
        {
            var state = IssueState.Replay(events);
            if (!state.TryGetIssue(issueId, out var issue))
            {
                return new EventStoreOperation<ResearchCompletionResult>(
                    [],
                    new ResearchCompletionResult(false, $"Issue '{issueId}' was not found.", dryRun, ResearchCompletionStatus.NotFound, null));
            }

            if (!ResearchClaimPolicy.HasAttempt(issue))
            {
                return new EventStoreOperation<ResearchCompletionResult>(
                    [],
                    new ResearchCompletionResult(false, "Issue has no research claim marker.", dryRun, ResearchCompletionStatus.NotResearchClaimed, new IssueView(state.GetSequence(issueId), issue)));
            }

            if (issue.Status != Status.Blocked)
            {
                return new EventStoreOperation<ResearchCompletionResult>(
                    [],
                    new ResearchCompletionResult(false, $"Issue is no longer Blocked (current status: {issue.Status}).", dryRun, ResearchCompletionStatus.NotBlocked, new IssueView(state.GetSequence(issueId), issue)));
            }

            if (dryRun)
            {
                return new EventStoreOperation<ResearchCompletionResult>(
                    [],
                    new ResearchCompletionResult(true, $"Research would move the Blocked issue to {completionStatus}.", true, ResearchCompletionStatus.WouldAdvance, new IssueView(state.GetSequence(issueId), issue)));
            }

            var plannedEvent = new StatusChanged(
                Guid.NewGuid(),
                issueId,
                DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc),
                completionStatus);
            issue.Apply(plannedEvent);
            return new EventStoreOperation<ResearchCompletionResult>(
                [plannedEvent],
                new ResearchCompletionResult(true, $"Research moved the Blocked issue to {completionStatus}.", false, ResearchCompletionStatus.Advanced, new IssueView(state.GetSequence(issueId), issue)));
        });
    }

    public ResearchCompletionResult CompleteResearch(IssueId issueId, bool dryRun = false, Status completionStatus = Status.Next)
        => TryCompleteResearch(issueId, dryRun, completionStatus);

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

                var activeReservationKeys = state.OrderedIssues
                    .Where(issue => issue.Status.HoldsRepositoryReservation())
                    .SelectMany(issue => RepositoryLabels.GetReservationKeys(issue.Repositories))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var candidate = state.SelectHierarchical(
                    issue => issue.Status == Status.Next &&
                             !RepositoryLabels.GetReservationKeys(issue.Repositories)
                                 .Any(activeReservationKeys.Contains));

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

public sealed record SplitIssueResult(bool Success, string Message, IssueView? Parent, IReadOnlyList<IssueView> Children);

