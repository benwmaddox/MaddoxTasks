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
            UpdateDescription updateDescription => PlanDescriptionUpdate(updateDescription, state, timestamp),
            AddComment addComment => PlanCommentAdd(addComment, state, timestamp),
            _ => throw new CommandValidationException($"Unsupported command '{command.GetType().Name}'.")
        };
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

        return new IssueCreated(
            Guid.NewGuid(),
            IssueId.New(),
            timestamp,
            title,
            command.Description?.Trim() ?? string.Empty,
            Status.Backlog,
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

        return new LabelRemoved(Guid.NewGuid(), command.IssueId, timestamp, normalized);
    }

    private static IssueEvent PlanDescriptionUpdate(UpdateDescription command, IssueState state, DateTime timestamp)
    {
        var issue = RequireIssue(command.IssueId, state);
        var normalized = command.Description?.Trim() ?? string.Empty;

        if (string.Equals(issue.Description, normalized, StringComparison.Ordinal))
        {
            throw new CommandValidationException("Description is unchanged.");
        }

        return new DescriptionUpdated(Guid.NewGuid(), command.IssueId, timestamp, normalized);
    }

    private static IssueEvent PlanCommentAdd(AddComment command, IssueState state, DateTime timestamp)
    {
        _ = RequireIssue(command.IssueId, state);
        var normalized = command.Comment?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandValidationException("Comment cannot be empty.");
        }

        return new CommentAdded(Guid.NewGuid(), command.IssueId, timestamp, normalized);
    }

    private static Issue RequireIssue(IssueId issueId, IssueState state)
    {
        if (!state.TryGetIssue(issueId, out var issue))
        {
            throw new CommandValidationException($"Issue '{issueId}' was not found.");
        }

        return issue;
    }

    private static string NormalizeLabel(string label)
    {
        var normalized = IssueFiltering.NormalizeLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandValidationException("Label cannot be empty.");
        }

        return normalized;
    }

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

