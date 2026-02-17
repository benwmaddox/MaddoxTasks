using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

public static class EventSerializer
{
    public static string Serialize(IssueEvent issueEvent)
    {
        return issueEvent switch
        {
            IssueCreated value => JsonSerializer.Serialize(value, JsonDefaults.Context.IssueCreated),
            StatusChanged value => JsonSerializer.Serialize(value, JsonDefaults.Context.StatusChanged),
            PriorityChanged value => JsonSerializer.Serialize(value, JsonDefaults.Context.PriorityChanged),
            LabelAdded value => JsonSerializer.Serialize(value, JsonDefaults.Context.LabelAdded),
            LabelRemoved value => JsonSerializer.Serialize(value, JsonDefaults.Context.LabelRemoved),
            DescriptionUpdated value => JsonSerializer.Serialize(value, JsonDefaults.Context.DescriptionUpdated),
            CommentAdded value => JsonSerializer.Serialize(value, JsonDefaults.Context.CommentAdded),
            _ => throw new InvalidOperationException($"Unknown event type '{issueEvent.GetType().Name}'.")
        };
    }

    public static IssueEvent Deserialize(string eventType, string payload)
    {
        return eventType switch
        {
            nameof(IssueCreated) => DeserializeTyped(payload, JsonDefaults.Context.IssueCreated),
            nameof(StatusChanged) => DeserializeTyped(payload, JsonDefaults.Context.StatusChanged),
            nameof(PriorityChanged) => DeserializeTyped(payload, JsonDefaults.Context.PriorityChanged),
            nameof(LabelAdded) => DeserializeTyped(payload, JsonDefaults.Context.LabelAdded),
            nameof(LabelRemoved) => DeserializeTyped(payload, JsonDefaults.Context.LabelRemoved),
            nameof(DescriptionUpdated) => DeserializeTyped(payload, JsonDefaults.Context.DescriptionUpdated),
            nameof(CommentAdded) => DeserializeTyped(payload, JsonDefaults.Context.CommentAdded),
            _ => throw new InvalidOperationException($"Unknown event type '{eventType}'.")
        };
    }

    private static T DeserializeTyped<T>(string payload, JsonTypeInfo<T> typeInfo) where T : IssueEvent
    {
        var value = JsonSerializer.Deserialize(payload, typeInfo);
        return value ?? throw new InvalidOperationException($"Failed to deserialize event payload for '{typeof(T).Name}'.");
    }
}

