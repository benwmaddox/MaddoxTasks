using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public static class CommandPlanner
{
    public static IssueEvent Plan(Command command, IssueState state, DateTime timestampUtc)
    {
        var timestamp = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);

        return command switch
        {
            CreateIssue createIssue => PlanCreate(createIssue, state, timestamp),
            ChangeStatus changeStatus => PlanStatusChange(changeStatus, state, timestamp),
            ChangePriority changePriority => PlanPriorityChange(changePriority, state, timestamp),
            AddLabel addLabel => PlanLabelAdd(addLabel, state, timestamp),
            RemoveLabel removeLabel => PlanLabelRemove(removeLabel, state, timestamp),
            SetRepositoryLabels setRepositoryLabels => PlanRepositoryLabelsSet(setRepositoryLabels, state, timestamp),
            UpdateDescription updateDescription => PlanDescriptionUpdate(updateDescription, state, timestamp),
            AddComment addComment => PlanCommentAdd(addComment, state, timestamp),
            _ => throw new CommandValidationException($"Unsupported command '{command.GetType().Name}'.")
        };
    }

    private static IssueEvent PlanRepositoryLabelsSet(SetRepositoryLabels command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);
        var repositories = command.Repositories
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (repositories.Length == 0) throw new CommandValidationException("At least one repository is required.");
        if (repositories.Any(static value => value.StartsWith(RepositoryLabels.Prefix, StringComparison.OrdinalIgnoreCase) || value.Any(char.IsControl)))
            throw new CommandValidationException("Repositories must be names without the 'repo:' prefix.");
        if (issue.Status.HoldsRepositoryReservation()) ValidateActiveReservation(issue, repositories, state);
        return new RepositoryLabelsSet(Guid.NewGuid(), command.IssueId, timestamp, repositories);
    }

    private static IssueEvent PlanCreate(CreateIssue command, IssueState state, DateTime timestamp)
    {
        var title = command.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new CommandValidationException("Issue title is required.");
        }

        if (command.ParentId.HasValue && !state.TryGetIssue(command.ParentId.Value, out _))
        {
            throw new CommandValidationException($"Parent issue '{command.ParentId.Value}' does not exist.");
        }

        if (command.Status is not (Status.Next or Status.Backlog))
        {
            throw new CommandValidationException(
                $"New issues may only start in 'Next' or explicit 'Backlog' status, not '{command.Status}'.");
        }

        return new IssueCreated(
            Guid.NewGuid(),
            IssueId.New(),
            timestamp,
            title,
            command.Description?.Trim() ?? string.Empty,
            command.Status,
            command.Priority,
            command.ParentId,
            NormalizeDueDate(command.DueDate));
    }

    private static IssueEvent PlanStatusChange(ChangeStatus command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);

        if (issue.Status == command.NewStatus)
        {
            throw new CommandValidationException($"Issue {command.IssueId} already has status '{command.NewStatus}'.");
        }

        if (command.NewStatus.HoldsRepositoryReservation())
        {
            ValidateActiveReservation(issue, issue.Repositories, state);
        }

        return new StatusChanged(Guid.NewGuid(), command.IssueId, timestamp, command.NewStatus);
    }

    private static IssueEvent PlanPriorityChange(ChangePriority command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);

        if (issue.Priority == command.NewPriority)
        {
            throw new CommandValidationException($"Issue {command.IssueId} already has priority '{command.NewPriority.Value}'.");
        }

        return new PriorityChanged(Guid.NewGuid(), command.IssueId, timestamp, command.NewPriority);
    }

    private static IssueEvent PlanLabelAdd(AddLabel command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);
        var normalized = NormalizeLabel(command.Label);

        if (issue.HasLabel(normalized))
        {
            throw new CommandValidationException($"Issue {command.IssueId} already has label '{normalized}'.");
        }

        if (issue.Status.HoldsRepositoryReservation() && RepositoryLabels.TryGetRepository(normalized, out var repository))
        {
            var repositories = issue.Repositories.Append(repository).ToArray();
            ValidateActiveReservation(issue, repositories, state);
        }

        return new LabelAdded(Guid.NewGuid(), command.IssueId, timestamp, normalized);
    }

    private static IssueEvent PlanLabelRemove(RemoveLabel command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);
        var normalized = NormalizeLabel(command.Label);

        if (!issue.HasLabel(normalized))
        {
            throw new CommandValidationException($"Issue {command.IssueId} does not have label '{normalized}'.");
        }

        if (issue.Status.HoldsRepositoryReservation() && RepositoryLabels.TryGetRepository(normalized, out var repository))
        {
            var repositories = issue.Repositories
                .Where(candidate => !string.Equals(candidate, repository, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ValidateActiveReservation(issue, repositories, state);
        }

        return new LabelRemoved(Guid.NewGuid(), command.IssueId, timestamp, normalized);
    }

    private static IssueEvent PlanDescriptionUpdate(UpdateDescription command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);
        var normalized = command.Description?.Trim() ?? string.Empty;
        var actor = NormalizeActor(command.Actor);

        if (string.Equals(issue.Description, normalized, StringComparison.Ordinal))
        {
            throw new CommandValidationException("Description is unchanged.");
        }

        return new DescriptionUpdated(Guid.NewGuid(), command.IssueId, timestamp, normalized, actor);
    }

    private static IssueEvent PlanCommentAdd(AddComment command, IssueState state, DateTime timestamp)
    {
        _ = RequireIssue(command.IssueId, state);
        var normalized = command.Comment?.Trim() ?? string.Empty;
        var actor = NormalizeActor(command.Actor);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandValidationException("Comment cannot be empty.");
        }

        return new CommentAdded(Guid.NewGuid(), command.IssueId, timestamp, normalized, actor);
    }

    private static Issue RequireIssue(IssueId issueId, IssueState state)
    {
        if (!state.TryGetIssue(issueId, out var issue))
        {
            throw new CommandValidationException($"Issue '{issueId}' was not found.");
        }

        return issue;
    }

    private static void ValidateActiveReservation(Issue issue, IEnumerable<string> repositories, IssueState state)
    {
        var reservationKeys = RepositoryLabels.GetReservationKeys(repositories);

        foreach (var activeIssue in state.Issues.Values
                     .Where(candidate => candidate.Id != issue.Id && candidate.Status.HoldsRepositoryReservation())
                     .OrderBy(candidate => state.GetSequence(candidate.Id)))
        {
            var overlap = reservationKeys
                .Intersect(RepositoryLabels.GetReservationKeys(activeIssue.Repositories), StringComparer.OrdinalIgnoreCase)
                .OrderBy(static repository => repository, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (overlap is not null)
            {
                throw new CommandValidationException(
                    $"Cannot reserve repository scope '{overlap}': it is already reserved by reserving issue {activeIssue.Id}.");
            }
        }
    }

    private static string NormalizeLabel(string label)
    {
        var normalized = IssueFiltering.NormalizeLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandValidationException("Label cannot be empty.");
        }

        if (normalized.StartsWith(RepositoryLabels.Prefix, StringComparison.Ordinal)
            && !RepositoryLabels.TryGetRepository(normalized, out _))
        {
            throw new CommandValidationException("Repository label must use the form 'repo:<name>'.");
        }

        return normalized;
    }

    private static string NormalizeActor(string actor)
    {
        var normalized = actor?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "user";
        }

        if (string.Equals(normalized, "user", StringComparison.OrdinalIgnoreCase))
        {
            return "user";
        }

        if (string.Equals(normalized, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return "agent";
        }

        if (normalized.Length > 64 || normalized.Any(static ch => !IsAllowedActorChar(ch)))
        {
            throw new CommandValidationException(
                "Actor must be 'user', 'agent', or a model id (letters/digits and . _ - : / +).");
        }

        return normalized;
    }

    private static bool IsAllowedActorChar(char ch)
        => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' or '/' or '+' or ' ';

    private static DateTime? NormalizeDueDate(DateTime? dueDate)
    {
        if (!dueDate.HasValue)
        {
            return null;
        }

        var value = dueDate.Value;
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return value.ToUniversalTime();
    }
}

