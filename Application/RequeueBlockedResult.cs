using MaddoxTasks.Domain;

namespace MaddoxTasks.Application;

public sealed record RequeueBlockedResult(
    bool Success,
    string Message,
    bool DryRun,
    IReadOnlyList<IssueId> ChangedIssueIds,
    IReadOnlyList<IssueId> SkippedIssueIds);
