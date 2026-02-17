using System.Text.Json.Serialization;
using MaddoxTasks.Application;

namespace MaddoxTasks.Infrastructure;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(MaddoxTasksSettings))]
[JsonSerializable(typeof(IssueCreated))]
[JsonSerializable(typeof(StatusChanged))]
[JsonSerializable(typeof(PriorityChanged))]
[JsonSerializable(typeof(LabelAdded))]
[JsonSerializable(typeof(LabelRemoved))]
[JsonSerializable(typeof(DescriptionUpdated))]
[JsonSerializable(typeof(CommentAdded))]
internal sealed partial class MaddoxTasksJsonContext : JsonSerializerContext;
