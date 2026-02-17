using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public sealed record CommandExecutionResult(
    bool Success,
    string Message,
    IssueId? IssueId,
    Guid? EventId)
{
    public static CommandExecutionResult Succeeded(string message, IssueId issueId, Guid eventId)
        => new(true, message, issueId, eventId);

    public static CommandExecutionResult Failed(string message)
        => new(false, message, null, null);
}

