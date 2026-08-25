using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

public interface IEventStore
{
    IReadOnlyList<IssueEvent> LoadAll();
    void Append(IssueEvent issueEvent);

    T ExecuteAtomic<T>(Func<IReadOnlyList<IssueEvent>, EventStoreOperation<T>> operation)
    {
        var result = operation(LoadAll());
        foreach (var issueEvent in result.Events)
        {
            Append(issueEvent);
        }

        return result.Result;
    }
}

public sealed record EventStoreOperation<T>(IReadOnlyList<IssueEvent> Events, T Result);

