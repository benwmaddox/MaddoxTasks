using System.Text.Json;
using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

public static class EventSerializer
{
    public static string Serialize(IssueEvent issueEvent)
        => JsonSerializer.Serialize(issueEvent, issueEvent.GetType(), JsonDefaults.Options);

    public static IssueEvent Deserialize(string eventType, string payload)
    {
        return eventType switch
        {
            nameof(IssueCreated) => DeserializeTyped<IssueCreated>(payload),
            nameof(StatusChanged) => DeserializeTyped<StatusChanged>(payload),
            nameof(PriorityChanged) => DeserializeTyped<PriorityChanged>(payload),
            nameof(LabelAdded) => DeserializeTyped<LabelAdded>(payload),
            nameof(LabelRemoved) => DeserializeTyped<LabelRemoved>(payload),
            nameof(DescriptionUpdated) => DeserializeTyped<DescriptionUpdated>(payload),
            _ => throw new InvalidOperationException($"Unknown event type '{eventType}'.")
        };
    }

    private static T DeserializeTyped<T>(string payload) where T : IssueEvent
    {
        var value = JsonSerializer.Deserialize<T>(payload, JsonDefaults.Options);
        return value ?? throw new InvalidOperationException($"Failed to deserialize event payload for '{typeof(T).Name}'.");
    }
}

