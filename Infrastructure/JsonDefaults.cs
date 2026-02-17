using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaddoxTasks.Domain;

namespace MaddoxTasks.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = BuildOptions();
    internal static readonly MaddoxTasksJsonContext Context = new(Options);

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter<Status>());
        options.Converters.Add(new IssueIdJsonConverter());
        options.Converters.Add(new PriorityJsonConverter());

        return options;
    }
}

public sealed class IssueIdJsonConverter : JsonConverter<IssueId>
{
    public override IssueId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("IssueId must be a string.");
        }

        var value = reader.GetString();
        if (!IssueId.TryParse(value, out var issueId))
        {
            throw new JsonException($"Invalid IssueId '{value}'.");
        }

        return issueId;
    }

    public override void Write(Utf8JsonWriter writer, IssueId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public sealed class PriorityJsonConverter : JsonConverter<Priority>
{
    public override Priority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Priority must be numeric.");
        }

        var value = reader.GetInt32();
        return Priority.From(value);
    }

    public override void Write(Utf8JsonWriter writer, Priority value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}

