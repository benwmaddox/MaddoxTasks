using System.Text.Json;
using System.Text.Json.Serialization;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Agent;

public static partial class AgentRunner
{
    public static string ExecuteCommandJson(IssueEngine engine, string json, string? defaultActor = null)
    {
        var resolvedDefaultActor = ResolveDefaultActor(defaultActor);
        var normalizedJson = NormalizeIncomingJson(json);

        if (!TryParseCommand(normalizedJson, engine, resolvedDefaultActor, out var command, out var error))
        {
            return SerializeResponse(new AgentCommandResponse(false, error, null, null));
        }

        var result = engine.Execute(command!);
        return SerializeResponse(
            new AgentCommandResponse(
                result.Success,
                result.Message,
                result.IssueId?.ToString(),
                result.EventId?.ToString()));
    }

    public static string GetIssuesJson(IssueEngine engine, IssueFilter? filter, bool includeDone)
    {
        var issues = engine.QueryIssues(filter, includeDone)
            .Select(ToAgentIssueDto)
            .ToArray();

        return JsonSerializer.Serialize(issues, PrettyJsonContext.AgentIssueDtoArray);
    }

    public static string GetNextTaskJson(IssueEngine engine)
    {
        IssueView? nextTask = engine.QueryIssues(includeDone: false)
            .Where(view => view.Issue.Status is Status.Active or Status.Next)
            .OrderBy(view => view.Issue.Priority.Value)
            .ThenBy(view => view.Issue.Status == Status.Active ? 0 : 1)
            .ThenBy(view => view.Sequence)
            .FirstOrDefault();

        var dto = nextTask is null ? null : ToAgentIssueDto(nextTask);
        return JsonSerializer.Serialize(dto, PrettyJsonContext.AgentIssueDto);
    }

    private static bool TryParseCommand(string json, IssueEngine engine, string defaultActor, out Command? command, out string error)
    {
        command = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Command JSON is empty.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException jsonException)
        {
            error = $"Invalid JSON: {jsonException.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryGetProperty(root, "type", out var typeElement))
            {
                error = "Command JSON must contain 'type'.";
                return false;
            }

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
            {
                error = "Command 'type' cannot be empty.";
                return false;
            }

            switch (type.Trim().ToLowerInvariant())
            {
                case "createissue":
                    return TryBuildCreateIssue(root, out command, out error);
                case "changestatus":
                    return TryBuildStatusChange(root, engine, out command, out error);
                case "changepriority":
                    return TryBuildPriorityChange(root, engine, out command, out error);
                case "addlabel":
                    return TryBuildLabelAdd(root, engine, out command, out error);
                case "removelabel":
                    return TryBuildLabelRemove(root, engine, out command, out error);
                case "updatedescription":
                    return TryBuildDescriptionUpdate(root, engine, defaultActor, out command, out error);
                case "addcomment":
                    return TryBuildCommentAdd(root, engine, defaultActor, out command, out error);
                default:
                    error = $"Unsupported command type '{type}'.";
                    return false;
            }
        }
    }

    private static bool TryBuildCreateIssue(JsonElement root, out Command? command, out string error)
    {
        command = null;
        error = string.Empty;

        if (!TryGetString(root, "title", required: true, out var title, out error))
        {
            return false;
        }

        TryGetString(root, "description", required: false, out var description, out _);
        TryGetString(root, "parentId", required: false, out var parentText, out _);
        TryGetString(root, "dueDate", required: false, out var dueDateText, out _);

        var priority = 3;
        if (TryGetProperty(root, "priority", out var priorityElement))
        {
            if (priorityElement.ValueKind != JsonValueKind.Number || !priorityElement.TryGetInt32(out priority))
            {
                error = "Priority must be an integer between 1 and 5.";
                return false;
            }
        }

        Priority priorityValue;
        try
        {
            priorityValue = Priority.From(priority);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Priority must be an integer between 1 and 5.";
            return false;
        }

        IssueId? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentText))
        {
            if (!IssueId.TryParse(parentText, out var parsedParent))
            {
                error = "parentId must be a valid issue id GUID.";
                return false;
            }

            parentId = parsedParent;
        }

        DateTime? dueDate = null;
        if (!string.IsNullOrWhiteSpace(dueDateText))
        {
            if (!DateTime.TryParse(dueDateText, out var parsedDueDate))
            {
                error = "dueDate must be a valid date/time value.";
                return false;
            }

            dueDate = parsedDueDate;
        }

        command = new CreateIssue(title!, description, priorityValue, parentId, dueDate);
        return true;
    }

    private static bool TryBuildStatusChange(JsonElement root, IssueEngine engine, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetString(root, "newStatus", required: true, out var statusText, out error))
        {
            return false;
        }

        if (!TryParseStatus(statusText!, out var status))
        {
            error = $"Invalid status '{statusText}'.";
            return false;
        }

        command = new ChangeStatus(issueId, status);
        return true;
    }

    private static bool TryBuildPriorityChange(JsonElement root, IssueEngine engine, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetProperty(root, "newPriority", out var priorityElement) ||
            priorityElement.ValueKind != JsonValueKind.Number ||
            !priorityElement.TryGetInt32(out var newPriority))
        {
            error = "newPriority must be an integer between 1 and 5.";
            return false;
        }

        try
        {
            command = new ChangePriority(issueId, Priority.From(newPriority));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "newPriority must be an integer between 1 and 5.";
            return false;
        }
    }

    private static bool TryBuildLabelAdd(JsonElement root, IssueEngine engine, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetString(root, "label", required: true, out var label, out error))
        {
            return false;
        }

        command = new AddLabel(issueId, label!);
        return true;
    }

    private static bool TryBuildLabelRemove(JsonElement root, IssueEngine engine, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetString(root, "label", required: true, out var label, out error))
        {
            return false;
        }

        command = new RemoveLabel(issueId, label!);
        return true;
    }

    private static bool TryBuildDescriptionUpdate(JsonElement root, IssueEngine engine, string defaultActor, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetString(root, "description", required: true, out var description, out error))
        {
            return false;
        }

        TryGetString(root, "actor", required: false, out var actorText, out _);
        var actor = string.IsNullOrWhiteSpace(actorText) ? defaultActor : actorText!;
        command = new UpdateDescription(issueId, description!, actor);
        return true;
    }

    private static bool TryBuildCommentAdd(JsonElement root, IssueEngine engine, string defaultActor, out Command? command, out string error)
    {
        command = null;
        if (!TryResolveIssue(root, engine, out var issueId, out error))
        {
            return false;
        }

        if (!TryGetString(root, "comment", required: true, out var comment, out error))
        {
            return false;
        }

        TryGetString(root, "actor", required: false, out var actorText, out _);
        var actor = string.IsNullOrWhiteSpace(actorText) ? defaultActor : actorText!;
        command = new AddComment(issueId, comment!, actor);
        return true;
    }

    private static bool TryResolveIssue(JsonElement root, IssueEngine engine, out IssueId issueId, out string error)
    {
        issueId = default;
        if (!TryGetString(root, "issueId", required: true, out var issueToken, out error))
        {
            return false;
        }

        if (!engine.TryResolveIssueToken(issueToken!, out issueId, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseStatus(string input, out Status status)
        => StatusText.TryParse(input, out status);

    private static string NormalizeIncomingJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        var span = json.AsSpan();
        if (span.Length > 0 && span[0] == '\uFEFF')
        {
            span = span[1..];
        }

        return span.ToString();
    }

    private static string ResolveDefaultActor(string? explicitActor)
    {
        if (!string.IsNullOrWhiteSpace(explicitActor))
        {
            return explicitActor.Trim();
        }

        var keys = new[]
        {
            "MADDOX_TASKS_AGENT_ACTOR",
            "MADDOX_TASKS_ACTOR",
            "CODEX_MODEL",
            "OPENAI_MODEL",
            "ANTHROPIC_MODEL",
            "CLAUDE_MODEL",
            "MODEL"
        };

        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        var claudeSettingsActor = TryResolveActorFromClaudeSettings();
        if (!string.IsNullOrWhiteSpace(claudeSettingsActor))
        {
            return claudeSettingsActor;
        }

        var configActor = TryResolveActorFromCodexConfig();
        if (!string.IsNullOrWhiteSpace(configActor))
        {
            return configActor;
        }

        return "agent";
    }

    private static string? TryResolveActorFromCodexConfig()
    {
        try
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                var userHome = GetUserHomeDirectory();
                if (string.IsNullOrWhiteSpace(userHome))
                {
                    return null;
                }

                codexHome = Path.Combine(userHome, ".codex");
            }

            var configPath = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(configPath))
            {
                return null;
            }

            string? model = null;
            string? effort = null;

            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                {
                    value = value[1..^1];
                }

                if (key.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    model = value;
                    continue;
                }

                if (key.Equals("model_reasoning_effort", StringComparison.OrdinalIgnoreCase))
                {
                    effort = value;
                }
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(effort) ? model : $"{model} {effort}";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveActorFromClaudeSettings()
    {
        try
        {
            var cwd = Environment.CurrentDirectory;
            var userHome = GetUserHomeDirectory();

            var candidates = new[]
            {
                string.IsNullOrWhiteSpace(cwd) ? null : Path.Combine(cwd, ".claude", "settings.local.json"),
                string.IsNullOrWhiteSpace(cwd) ? null : Path.Combine(cwd, ".claude", "settings.json"),
                string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".claude", "settings.json")
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                using var stream = File.OpenRead(candidate);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetProperty(root, "model", out var modelElement) || modelElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var model = modelElement.GetString();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    return model.Trim();
                }
            }
        }
        catch
        {
            // Ignore unreadable config and continue fallback chain.
        }

        return null;
    }

    private static string? GetUserHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    private static bool TryGetString(JsonElement root, string propertyName, bool required, out string? value, out string error)
    {
        value = null;
        error = string.Empty;

        if (!TryGetProperty(root, propertyName, out var element))
        {
            if (required)
            {
                error = $"Missing required field '{propertyName}'.";
                return false;
            }

            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"Field '{propertyName}' cannot be null.";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"Field '{propertyName}' must be a string.";
            return false;
        }

        value = element.GetString();
        if (required && string.IsNullOrWhiteSpace(value))
        {
            error = $"Field '{propertyName}' cannot be empty.";
            return false;
        }

        return true;
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string SerializeResponse(AgentCommandResponse response)
        => JsonSerializer.Serialize(response, PrettyJsonContext.AgentCommandResponse);

    private static AgentIssueDto ToAgentIssueDto(IssueView view)
        => new(
            view.Sequence,
            view.ShortId,
            view.GuidPrefix,
            view.Issue.Id.ToString(),
            view.Issue.Title,
            view.Issue.Description,
            view.Issue.Status,
            view.Issue.Priority.Value,
            view.Issue.ParentId?.ToString(),
            view.Issue.Labels.ToArray(),
            view.Issue.Comments
                .Select(comment => new AgentIssueCommentDto(comment.Timestamp, comment.Comment, comment.Actor))
                .ToArray(),
            view.Issue.CreatedAt,
            view.Issue.UpdatedAt,
            view.Issue.DueDate);

    private static readonly AgentJsonContext PrettyJsonContext = new(new JsonSerializerOptions(JsonDefaults.Options)
    {
        WriteIndented = true
    });

    private sealed record AgentCommandResponse(bool Success, string Message, string? IssueId, string? EventId);

    private sealed record AgentIssueDto(
        int Sequence,
        string ShortId,
        string GuidPrefix,
        string IssueId,
        string Title,
        string Description,
        Status Status,
        int Priority,
        string? ParentId,
        string[] Labels,
        AgentIssueCommentDto[] Comments,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? DueDate);

    private sealed record AgentIssueCommentDto(DateTime Timestamp, string Comment, string Actor);

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(AgentIssueDto))]
    [JsonSerializable(typeof(AgentIssueDto[]))]
    [JsonSerializable(typeof(AgentCommandResponse))]
    private sealed partial class AgentJsonContext : JsonSerializerContext;
}

