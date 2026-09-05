using System.Globalization;
using System.Text.Json;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MaddoxTasks.Web;

internal static class WebEndpoints
{
    public static void Map(WebApplication app, IssueEngine engine)
    {
        app.MapGet("/", ServeIndexAsync);
        app.MapGet("/index.html", ServeIndexAsync);
        app.MapGet("/favicon.ico", context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        app.MapGet("/api", context => WriteJson(context, StatusCodes.Status200OK, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WriteString("name", "MaddoxTasks");
            writer.WriteString("version", "1");
            writer.WriteString("ui", "/");
        }));
        app.MapGet("/api/health", context => WriteJson(context, StatusCodes.Status200OK, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WriteString("status", "ok");
        }));

        app.MapGet("/api/issues", context => GetIssuesAsync(context, engine));
        app.MapGet("/api/repository-locks", context => GetRepositoryLocksAsync(context, engine));
        app.MapGet("/api/issues/{token}", context => GetIssueAsync(context, engine));
        app.MapPost("/api/issues", context => CreateIssueAsync(context, engine));

        app.MapMethods("/api/issues/{token}/status", ["PATCH", "PUT", "POST"],
            context => ChangeStatusAsync(context, engine));
        app.MapMethods("/api/issues/{token}/priority", ["PATCH", "PUT", "POST"],
            context => ChangePriorityAsync(context, engine));
        app.MapMethods("/api/issues/{token}/description", ["PATCH", "PUT", "POST"],
            context => UpdateDescriptionAsync(context, engine));
        app.MapPost("/api/issues/{token}/labels", context => AddLabelAsync(context, engine));
        app.MapPost("/api/issues/{token}/label", context => AddLabelAsync(context, engine));
        app.MapMethods("/api/issues/{token}/labels", ["DELETE"],
            context => RemoveLabelAsync(context, engine));
        app.MapMethods("/api/issues/{token}/label", ["DELETE"],
            context => RemoveLabelAsync(context, engine));
        app.MapDelete("/api/issues/{token}/labels/{label}", context =>
            RemoveLabelFromRouteAsync(context, engine));
        app.MapDelete("/api/issues/{token}/label/{label}", context =>
            RemoveLabelFromRouteAsync(context, engine));
        app.MapPost("/api/issues/{token}/comments", context => AddCommentAsync(context, engine));
        app.MapPost("/api/issues/{token}/comment", context => AddCommentAsync(context, engine));
    }

    private static Task ServeIndexAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(WebAssets.IndexHtml, context.RequestAborted);
    }

    private static async Task GetIssuesAsync(HttpContext context, IssueEngine engine)
    {
        if (!TryBuildFilter(context, out var filter, out var includeDone, out var search, out var error))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, error);
            return;
        }

        IEnumerable<IssueView> issues = engine.QueryIssues(filter, includeDone);
        if (!string.IsNullOrWhiteSpace(search))
        {
            issues = issues.Where(view => MatchesSearch(view.Issue, search));
        }

        var issueViews = issues.ToArray();
        await WriteJson(context, StatusCodes.Status200OK, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WriteNumber("count", issueViews.Length);
            writer.WriteString("generatedAt", DateTime.UtcNow);
            writer.WriteStartArray("issues");
            foreach (var view in issueViews)
            {
                WriteIssue(writer, view, includeDetails: false, includeHistory: false, engine);
            }

            writer.WriteEndArray();
        });
    }

    private static async Task GetIssueAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        var view = FindIssue(engine, issueId);
        if (view is null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, $"Issue '{token}' was not found.");
            return;
        }

        await WriteJson(context, StatusCodes.Status200OK, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WritePropertyName("issue");
            WriteIssue(writer, view, includeDetails: true, includeHistory: true, engine);
        });
    }

    private static async Task GetRepositoryLocksAsync(HttpContext context, IssueEngine engine)
    {
        var locks = engine.QueryIssues(includeDone: true)
            .Where(view => view.Issue.Status.HoldsRepositoryReservation())
            .SelectMany(view => RepositoryLabels.GetReservationKeys(view.Issue.Repositories)
                .Select(repository => (Repository: repository, View: view)))
            .OrderBy(item => item.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.View.Sequence)
            .ToArray();

        await WriteJson(context, StatusCodes.Status200OK, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WriteNumber("count", locks.Length);
            writer.WriteStartArray("locks");
            foreach (var item in locks)
            {
                writer.WriteStartObject();
                writer.WriteString("repository", item.Repository);
                writer.WriteNumber("sequence", item.View.Sequence);
                writer.WriteString("issueId", item.View.Issue.Id.ToString());
                writer.WriteString("shortId", item.View.ShortId);
                writer.WriteString("title", item.View.Issue.Title);
                writer.WriteString("status", item.View.Issue.Status.ToString());
                writer.WriteString("statusLabel", item.View.Issue.Status.ToDisplayString());
                writer.WriteNumber("priority", item.View.Issue.Priority.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static async Task CreateIssueAsync(HttpContext context, IssueEngine engine)
    {
        await HandleJson(context, async root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "Create payload must be a JSON object.");
                return;
            }

            var title = GetOptionalString(root, "title");
            var description = GetOptionalString(root, "description");
            var parentToken = GetOptionalString(root, "parentId") ?? GetOptionalString(root, "parent");
            var dueText = GetOptionalString(root, "dueDate") ?? GetOptionalString(root, "due");
            var statusText = GetOptionalString(root, "status") ?? nameof(Status.Next);
            var priorityRaw = GetOptionalInt(root, "priority") ?? 3;

            if (!TryParsePriority(priorityRaw, out var priority, out var priorityError))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, priorityError);
                return;
            }

            if (!StatusText.TryParse(statusText, out var status) || status is not (Status.Next or Status.Backlog))
            {
                await WriteError(context, StatusCodes.Status400BadRequest,
                    "Initial status must be 'Next' or 'Backlog'.");
                return;
            }

            IssueId? parentId = null;
            if (!string.IsNullOrWhiteSpace(parentToken))
            {
                if (!TryResolveIssue(engine, parentToken, out var resolvedParent, out var parentError))
                {
                    await WriteError(context, StatusCodes.Status404NotFound, parentError);
                    return;
                }

                parentId = resolvedParent;
            }

            if (!TryParseDueDate(dueText, out var dueDate, out var dueError))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, dueError);
                return;
            }

            var result = engine.Execute(new CreateIssue(title ?? string.Empty, description, priority, parentId, dueDate, status));
            await WriteCommandResult(context, result, engine, StatusCodes.Status201Created);
        });
    }

    private static async Task ChangeStatusAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var statusText = GetStringPayload(root, "status", "newStatus");
            if (!StatusText.TryParse(statusText, out var status))
            {
                await WriteError(context, StatusCodes.Status400BadRequest,
                    "Status must be one of Backlog, Next, Active, Blocked, ReadyForReview, Done, or Rejected.");
                return;
            }

            var result = engine.Execute(new ChangeStatus(issueId, status));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task ChangePriorityAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var priorityRaw = GetIntPayload(root, "priority", "newPriority");
            if (!TryParsePriority(priorityRaw, out var priority, out var priorityError))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, priorityError);
                return;
            }

            var result = engine.Execute(new ChangePriority(issueId, priority));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task UpdateDescriptionAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var description = GetStringPayload(root, "description", "text");
            var actor = GetOptionalString(root, "actor") ?? "user";
            var result = engine.Execute(new UpdateDescription(issueId, description, actor));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task AddLabelAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var label = GetStringPayload(root, "label", "name");
            var result = engine.Execute(new AddLabel(issueId, label));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task RemoveLabelAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var label = GetStringPayload(root, "label", "name");
            var result = engine.Execute(new RemoveLabel(issueId, label));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task RemoveLabelFromRouteAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        var label = GetRouteValue(context, "label");
        var result = engine.Execute(new RemoveLabel(issueId, label));
        await WriteCommandResult(context, result, engine);
    }

    private static async Task AddCommentAsync(HttpContext context, IssueEngine engine)
    {
        var token = GetRouteValue(context, "token");
        if (!TryResolveIssue(engine, token, out var issueId, out var error))
        {
            await WriteError(context, StatusCodes.Status404NotFound, error);
            return;
        }

        await HandleJson(context, async root =>
        {
            var comment = GetStringPayload(root, "comment", "text");
            var actor = GetOptionalString(root, "actor") ?? "user";
            var result = engine.Execute(new AddComment(issueId, comment, actor));
            await WriteCommandResult(context, result, engine);
        });
    }

    private static async Task HandleJson(HttpContext context, Func<JsonElement, Task> handler)
    {
        try
        {
            if (context.Request.ContentLength is > 1_000_000)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "Request body is too large.");
                return;
            }

            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                new JsonDocumentOptions { AllowTrailingCommas = false },
                context.RequestAborted);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Number))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "Request body must be a JSON object, string, or number.");
                return;
            }

            await handler(document.RootElement);
        }
        catch (JsonException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, $"Invalid JSON payload: {exception.Message}");
        }
        catch (FormatException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    private static bool TryBuildFilter(
        HttpContext context,
        out IssueFilter? filter,
        out bool includeDone,
        out string? search,
        out string error)
    {
        filter = null;
        error = string.Empty;
        var query = context.Request.Query;
        var statusText = FirstQueryValue(query, "status");
        var statusNotText = FirstQueryValue(query, "notStatus", "statusNot", "not-status");
        var maxPriorityText = FirstQueryValue(query, "maxPriority", "max-priority");
        var labelsText = query["labels"].FirstOrDefault();
        var dueBeforeText = FirstQueryValue(query, "dueBefore", "due-before");
        search = FirstQueryValue(query, "search", "q");
        includeDone = ParseBoolean(FirstQueryValue(query, "includeDone", "include-done"));

        if (!TryParseOptionalStatus(statusText, out var status))
        {
            error = $"Invalid status '{statusText}'.";
            return false;
        }

        if (!TryParseOptionalStatus(statusNotText, out var statusNot))
        {
            error = $"Invalid status '{statusNotText}'.";
            return false;
        }

        int? maxPriority = null;
        if (!string.IsNullOrWhiteSpace(maxPriorityText))
        {
            if (!int.TryParse(maxPriorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority))
            {
                error = "maxPriority must be a number between 1 and 5.";
                return false;
            }

            maxPriority = parsedPriority;
            if (maxPriority is < 1 or > 5)
            {
                error = "maxPriority must be between 1 and 5.";
                return false;
            }
        }

        DateTime? dueBefore = null;
        if (!string.IsNullOrWhiteSpace(dueBeforeText))
        {
            if (!DateTime.TryParse(dueBeforeText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsedDueBefore))
            {
                error = "dueBefore must be a date such as 2026-12-31.";
                return false;
            }

            dueBefore = parsedDueBefore;
        }

        var labels = string.IsNullOrWhiteSpace(labelsText)
            ? null
            : labelsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(IssueFiltering.NormalizeLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var parsedFilter = new IssueFilter
        {
            StatusEquals = status,
            StatusNotEquals = statusNot,
            PriorityLessThanOrEqual = maxPriority,
            MustHaveLabels = labels,
            DueBefore = dueBefore
        };

        if (parsedFilter.StatusEquals.HasValue ||
            parsedFilter.StatusNotEquals.HasValue ||
            parsedFilter.PriorityLessThanOrEqual.HasValue ||
            parsedFilter.MustHaveLabels is { Count: > 0 } ||
            parsedFilter.DueBefore.HasValue)
        {
            filter = parsedFilter;
        }

        return true;
    }

    private static bool MatchesSearch(Issue issue, string search)
    {
        var needle = search.Trim();
        return issue.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               issue.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               issue.Labels.Any(label => label.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstQueryValue(IQueryCollection query, params string[] names)
    {
        foreach (var name in names)
        {
            var value = query[name].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryParseOptionalStatus(string? text, out Status? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!StatusText.TryParse(text, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static bool TryParsePriority(int value, out Priority priority, out string error)
    {
        try
        {
            priority = Priority.From(value);
            error = string.Empty;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            priority = default;
            error = "Priority must be between 1 and 5.";
            return false;
        }
    }

    private static bool TryParseDueDate(string? value, out DateTime? dueDate, out string error)
    {
        dueDate = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed))
        {
            error = "Due date must be a date such as 2026-12-31 or an ISO-8601 date-time.";
            return false;
        }

        dueDate = parsed;
        return true;
    }

    private static bool ParseBoolean(string? value)
        => bool.TryParse(value, out var parsed) && parsed;

    private static string GetRouteValue(HttpContext context, string name)
        => context.Request.RouteValues.TryGetValue(name, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static bool TryResolveIssue(IssueEngine engine, string token, out IssueId issueId, out string error)
        => engine.TryResolveIssueToken(token, out issueId, out error);

    private static IssueView? FindIssue(IssueEngine engine, IssueId issueId)
        => engine.QueryIssues(includeDone: true).FirstOrDefault(view => view.Issue.Id == issueId);

    private static async Task WriteCommandResult(
        HttpContext context,
        CommandExecutionResult result,
        IssueEngine engine,
        int successStatus = StatusCodes.Status200OK)
    {
        if (!result.Success)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, result.Message);
            return;
        }

        var view = result.IssueId.HasValue ? FindIssue(engine, result.IssueId.Value) : null;
        await WriteJson(context, successStatus, writer =>
        {
            writer.WriteBoolean("success", true);
            writer.WriteString("message", result.Message);
            if (result.IssueId.HasValue)
            {
                writer.WriteString("issueId", result.IssueId.Value.ToString());
            }

            if (result.EventId.HasValue)
            {
                writer.WriteString("eventId", result.EventId.Value);
            }

            if (view is not null)
            {
                writer.WritePropertyName("issue");
                WriteIssue(writer, view, includeDetails: true, includeHistory: false, engine);
            }
        });
    }

    private static Task WriteError(HttpContext context, int statusCode, string message)
        => WriteJson(context, statusCode, writer =>
        {
            writer.WriteBoolean("success", false);
            writer.WriteString("error", message);
            writer.WriteString("message", message);
        });

    private static async Task WriteJson(HttpContext context, int statusCode, Action<Utf8JsonWriter> write)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        await context.Response.Body.WriteAsync(stream.ToArray(), context.RequestAborted);
    }

    private static void WriteIssue(
        Utf8JsonWriter writer,
        IssueView view,
        bool includeDetails,
        bool includeHistory,
        IssueEngine engine)
    {
        var issue = view.Issue;
        writer.WriteStartObject();
        writer.WriteNumber("sequence", view.Sequence);
        writer.WriteString("shortId", view.ShortId);
        writer.WriteString("id", issue.Id.ToString());
        writer.WriteString("title", issue.Title);
        writer.WriteString("status", issue.Status.ToString());
        writer.WriteString("statusLabel", issue.Status.ToDisplayString());
        writer.WriteNumber("priority", issue.Priority.Value);
        if (issue.ParentId.HasValue)
        {
            writer.WriteString("parentId", issue.ParentId.Value.ToString());
        }
        else
        {
            writer.WriteNull("parentId");
        }

        if (issue.DueDate.HasValue)
        {
            writer.WriteString("dueDate", issue.DueDate.Value.ToUniversalTime());
        }
        else
        {
            writer.WriteNull("dueDate");
        }

        writer.WriteString("createdAt", issue.CreatedAt.ToUniversalTime());
        writer.WriteString("updatedAt", issue.UpdatedAt.ToUniversalTime());
        writer.WriteStartArray("labels");
        foreach (var label in issue.Labels)
        {
            writer.WriteStringValue(label);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("repositories");
        foreach (var repository in issue.Repositories)
        {
            writer.WriteStringValue(repository);
        }

        writer.WriteEndArray();
        if (includeDetails)
        {
            writer.WriteString("description", issue.Description);
            writer.WriteStartArray("comments");
            foreach (var comment in issue.Comments)
            {
                WriteComment(writer, comment);
            }

            writer.WriteEndArray();
        }

        if (includeHistory)
        {
            writer.WriteStartArray("history");
            foreach (var issueEvent in engine.GetEventLog().Where(item => item.IssueId == issue.Id))
            {
                WriteHistoryEvent(writer, issueEvent);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteComment(Utf8JsonWriter writer, IssueComment comment)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", comment.Timestamp.ToUniversalTime());
        writer.WriteString("comment", comment.Comment);
        writer.WriteString("actor", comment.Actor);
        writer.WriteEndObject();
    }

    private static void WriteHistoryEvent(Utf8JsonWriter writer, IssueEvent issueEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("eventType", issueEvent.GetType().Name);
        writer.WriteString("timestamp", issueEvent.Timestamp.ToUniversalTime());
        switch (issueEvent)
        {
            case StatusChanged statusChanged:
                writer.WriteString("status", statusChanged.NewStatus.ToString());
                break;
            case PriorityChanged priorityChanged:
                writer.WriteNumber("priority", priorityChanged.NewPriority.Value);
                break;
            case LabelAdded labelAdded:
                writer.WriteString("label", labelAdded.Label);
                break;
            case LabelRemoved labelRemoved:
                writer.WriteString("label", labelRemoved.Label);
                break;
            case DescriptionUpdated descriptionUpdated:
                writer.WriteString("description", descriptionUpdated.Description);
                writer.WriteString("actor", descriptionUpdated.Actor);
                break;
            case CommentAdded commentAdded:
                writer.WriteString("comment", commentAdded.Comment);
                writer.WriteString("actor", commentAdded.Actor);
                break;
            case IssueCreated issueCreated:
                writer.WriteString("status", issueCreated.Status.ToString());
                writer.WriteNumber("priority", issueCreated.Priority.Value);
                break;
        }

        writer.WriteEndObject();
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString();
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Property '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private static string GetStringPayload(JsonElement root, string propertyName, params string[] aliases)
    {
        var value = GetOptionalString(root, propertyName);
        if (value is null)
        {
            foreach (var alias in aliases)
            {
                value = GetOptionalString(root, alias);
                if (value is not null)
                {
                    break;
                }
            }
        }

        if (value is null)
        {
            var names = aliases.Length == 0
                ? $"'{propertyName}'"
                : $"'{propertyName}' or '{string.Join("', '", aliases)}'";
            throw new JsonException($"Property {names} is required and must be a string.");
        }

        return value;
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName, params string[] aliases)
    {
        if (root.ValueKind == JsonValueKind.Number)
        {
            if (root.TryGetInt32(out var directValue))
            {
                return directValue;
            }

            throw new JsonException($"{propertyName} must be an integer.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(propertyName, out var property))
        {
            var foundAlias = false;
            foreach (var alias in aliases)
            {
                if (root.TryGetProperty(alias, out property))
                {
                    foundAlias = true;
                    break;
                }
            }

            if (!foundAlias)
            {
                return null;
            }
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new JsonException($"Property '{propertyName}' must be an integer.");
        }

        return value;
    }

    private static int GetIntPayload(JsonElement root, string propertyName, params string[] aliases)
        => GetOptionalInt(root, propertyName, aliases)
           ?? throw new JsonException($"Property '{propertyName}' is required and must be an integer.");
}
