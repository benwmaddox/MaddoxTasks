using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

public interface IEventStore
{
    IReadOnlyList<IssueEvent> LoadAll();
    void Append(IssueEvent issueEvent);
}

