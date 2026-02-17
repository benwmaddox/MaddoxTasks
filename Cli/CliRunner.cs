using System.CommandLine;
using CliCommand = System.CommandLine.Command;
using DomainCommand = MaddoxTasks.Application.Command;
using IssueStatus = MaddoxTasks.Domain.Status;
using Spectre.Console;
using MaddoxTasks.Agent;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;
using MaddoxTasks.Ui;

namespace MaddoxTasks.Cli;

public static class CliRunner
{
    public static Task<int> InvokeAsync(string[] args)
    {
        var defaultDbPath = AppPaths.ResolveDatabasePath();
        var dbOption = new Option<string>("--db", () => defaultDbPath, $"Path to SQLite database file. Default: {defaultDbPath}");
        var root = new RootCommand("Maddox Tasks - personal deterministic task engine");
        root.AddGlobalOption(dbOption);

        root.SetHandler((string dbPath) => RunTui(dbPath), dbOption);
        root.AddCommand(BuildTuiCommand(dbOption));
        root.AddCommand(BuildListCommand(dbOption));
        root.AddCommand(BuildCreateCommand(dbOption));
        root.AddCommand(BuildStatusCommand(dbOption));
        root.AddCommand(BuildPriorityCommand(dbOption));
        root.AddCommand(BuildLabelCommand(dbOption));
        root.AddCommand(BuildDescribeCommand(dbOption));
        root.AddCommand(BuildCommentCommand(dbOption));
        root.AddCommand(BuildSummaryCommand(dbOption));
        root.AddCommand(BuildAgentCommand(dbOption));

        return root.InvokeAsync(args);
    }

    private static CliCommand BuildTuiCommand(Option<string> dbOption)
    {
        var command = new CliCommand("tui", "Run interactive terminal UI.");
        command.SetHandler((string dbPath) => RunTui(dbPath), dbOption);
        return command;
    }

    private static CliCommand BuildListCommand(Option<string> dbOption)
    {
        var statusOption = new Option<string?>("--status", "Include only this status.");
        var statusNotOption = new Option<string?>("--not-status", "Exclude this status.");
        var maxPriorityOption = new Option<int?>("--max-priority", "Include only issues with priority <= value.");
        var labelsOption = new Option<string?>("--labels", "Comma-separated labels (all required).");
        var dueBeforeOption = new Option<string?>("--due-before", "Include issues due on/before date (yyyy-MM-dd).");
        var includeDoneOption = new Option<bool>("--include-done", () => false, "Include done issues.");

        var command = new CliCommand("list", "List issues.");
        command.AddOption(statusOption);
        command.AddOption(statusNotOption);
        command.AddOption(maxPriorityOption);
        command.AddOption(labelsOption);
        command.AddOption(dueBeforeOption);
        command.AddOption(includeDoneOption);

        command.SetHandler((string dbPath, string? statusText, string? statusNotText, int? maxPriority, string? labelsText, string? dueBeforeText, bool includeDone) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryBuildFilter(statusText, statusNotText, maxPriority, labelsText, dueBeforeText, out var filter, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                return;
            }

            var issues = engine.QueryIssues(filter, includeDone).ToArray();
            if (issues.Length == 0)
            {
                AnsiConsole.MarkupLine("[grey]No issues found.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("GUID");
            table.AddColumn("Status");
            table.AddColumn("P");
            table.AddColumn("Due");
            table.AddColumn("Labels");
            table.AddColumn("Title");

            foreach (var issue in issues)
            {
                table.AddRow(
                    issue.ShortId,
                    issue.GuidPrefix,
                    issue.Issue.Status.ToString(),
                    issue.Issue.Priority.Value.ToString(),
                    issue.Issue.DueDate?.ToString("yyyy-MM-dd") ?? "-",
                    issue.Issue.Labels.Count == 0 ? "-" : string.Join(",", issue.Issue.Labels),
                    issue.Issue.Title);
            }

            AnsiConsole.Write(table);
        }, dbOption, statusOption, statusNotOption, maxPriorityOption, labelsOption, dueBeforeOption, includeDoneOption);

        return command;
    }

    private static CliCommand BuildCreateCommand(Option<string> dbOption)
    {
        var titleArgument = new Argument<string>("title", "Issue title.");
        var descriptionOption = new Option<string?>("--description", "Issue description.");
        var priorityOption = new Option<int>("--priority", () => 3, "Priority between 1 and 5.");
        var parentOption = new Option<string?>("--parent", "Parent issue token (sequence, guid, or guid prefix).");
        var dueOption = new Option<string?>("--due", "Due date (yyyy-MM-dd).");

        var command = new CliCommand("create", "Create a new issue.");
        command.AddArgument(titleArgument);
        command.AddOption(descriptionOption);
        command.AddOption(priorityOption);
        command.AddOption(parentOption);
        command.AddOption(dueOption);

        command.SetHandler((string dbPath, string title, string? description, int priorityRaw, string? parentToken, string? dueText) =>
        {
            var engine = CreateEngine(dbPath);
            Priority priority;
            try
            {
                priority = Priority.From(priorityRaw);
            }
            catch (ArgumentOutOfRangeException)
            {
                AnsiConsole.MarkupLine("[red]Priority must be between 1 and 5.[/]");
                return;
            }

            IssueId? parentId = null;
            if (!string.IsNullOrWhiteSpace(parentToken))
            {
                if (!engine.TryResolveIssueToken(parentToken, out var resolvedParent, out var parentError))
                {
                    AnsiConsole.MarkupLine($"[red]{parentError.EscapeMarkup()}[/]");
                    return;
                }

                parentId = resolvedParent;
            }

            DateTime? dueDate = null;
            if (!string.IsNullOrWhiteSpace(dueText))
            {
                if (!DateTime.TryParse(dueText, out var parsedDue))
                {
                    AnsiConsole.MarkupLine("[red]Invalid due date.[/]");
                    return;
                }

                dueDate = parsedDue;
            }

            var result = engine.Execute(new CreateIssue(title, description, priority, parentId, dueDate));
            PrintCommandResult(result);
        }, dbOption, titleArgument, descriptionOption, priorityOption, parentOption, dueOption);

        return command;
    }

    private static CliCommand BuildStatusCommand(Option<string> dbOption)
    {
        var issueArgument = new Argument<string>("issue", "Issue token (sequence, guid, or guid prefix).");
        var statusArgument = new Argument<string>("status", "New status.");

        var command = new CliCommand("status", "Change issue status.");
        command.AddArgument(issueArgument);
        command.AddArgument(statusArgument);

        command.SetHandler((string dbPath, string issueToken, string statusText) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryResolveIssue(engine, issueToken, out var issueId))
            {
                return;
            }

            if (!TryParseStatus(statusText, out var status))
            {
                AnsiConsole.MarkupLine($"[red]Invalid status '{statusText}'.[/]");
                return;
            }

            var result = engine.Execute(new ChangeStatus(issueId, status));
            PrintCommandResult(result);
        }, dbOption, issueArgument, statusArgument);

        return command;
    }

    private static CliCommand BuildPriorityCommand(Option<string> dbOption)
    {
        var issueArgument = new Argument<string>("issue", "Issue token (sequence, guid, or guid prefix).");
        var priorityArgument = new Argument<int>("priority", "New priority between 1 and 5.");
        var command = new CliCommand("priority", "Change issue priority.");
        command.AddArgument(issueArgument);
        command.AddArgument(priorityArgument);

        command.SetHandler((string dbPath, string issueToken, int priorityRaw) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryResolveIssue(engine, issueToken, out var issueId))
            {
                return;
            }

            Priority priority;
            try
            {
                priority = Priority.From(priorityRaw);
            }
            catch (ArgumentOutOfRangeException)
            {
                AnsiConsole.MarkupLine("[red]Priority must be between 1 and 5.[/]");
                return;
            }

            var result = engine.Execute(new ChangePriority(issueId, priority));
            PrintCommandResult(result);
        }, dbOption, issueArgument, priorityArgument);

        return command;
    }

    private static CliCommand BuildLabelCommand(Option<string> dbOption)
    {
        var issueArgument = new Argument<string>("issue", "Issue token (sequence, guid, or guid prefix).");
        var labelArgument = new Argument<string>("label", "Label value.");
        var removeOption = new Option<bool>("--remove", () => false, "Remove label instead of add.");

        var command = new CliCommand("label", "Add or remove issue label.");
        command.AddArgument(issueArgument);
        command.AddArgument(labelArgument);
        command.AddOption(removeOption);

        command.SetHandler((string dbPath, string issueToken, string label, bool remove) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryResolveIssue(engine, issueToken, out var issueId))
            {
                return;
            }

            DomainCommand commandToExecute = remove
                ? new RemoveLabel(issueId, label)
                : new AddLabel(issueId, label);

            var result = engine.Execute(commandToExecute);
            PrintCommandResult(result);
        }, dbOption, issueArgument, labelArgument, removeOption);

        return command;
    }

    private static CliCommand BuildDescribeCommand(Option<string> dbOption)
    {
        var issueArgument = new Argument<string>("issue", "Issue token (sequence, guid, or guid prefix).");
        var descriptionArgument = new Argument<string>("description", "Updated description.");
        var command = new CliCommand("describe", "Update issue description.");
        command.AddArgument(issueArgument);
        command.AddArgument(descriptionArgument);

        command.SetHandler((string dbPath, string issueToken, string description) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryResolveIssue(engine, issueToken, out var issueId))
            {
                return;
            }

            var result = engine.Execute(new UpdateDescription(issueId, description, "user"));
            PrintCommandResult(result);
        }, dbOption, issueArgument, descriptionArgument);

        return command;
    }

    private static CliCommand BuildCommentCommand(Option<string> dbOption)
    {
        var issueArgument = new Argument<string>("issue", "Issue token (sequence, guid, or guid prefix).");
        var commentArgument = new Argument<string>("comment", "Comment text.");
        var command = new CliCommand("comment", "Add an issue comment.");
        command.AddArgument(issueArgument);
        command.AddArgument(commentArgument);

        command.SetHandler((string dbPath, string issueToken, string comment) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryResolveIssue(engine, issueToken, out var issueId))
            {
                return;
            }

            var result = engine.Execute(new AddComment(issueId, comment, "user"));
            PrintCommandResult(result);
        }, dbOption, issueArgument, commentArgument);

        return command;
    }

    private static CliCommand BuildSummaryCommand(Option<string> dbOption)
    {
        var windowArgument = new Argument<string>("window", () => "week", "Summary window: day, week, month, all.");
        var command = new CliCommand("summary", "Print status summary.");
        command.AddArgument(windowArgument);

        command.SetHandler((string dbPath, string window) =>
        {
            var engine = CreateEngine(dbPath);
            var now = DateTime.UtcNow;
            var cutoff = window.ToLowerInvariant() switch
            {
                "day" => now.AddDays(-1),
                "week" => now.AddDays(-7),
                "month" => now.AddDays(-30),
                "all" => DateTime.MinValue,
                _ => DateTime.MinValue
            };

            if (window is not ("day" or "week" or "month" or "all"))
            {
                AnsiConsole.MarkupLine($"[red]Unknown summary window '{window}'. Use day/week/month/all.[/]");
                return;
            }

            var issues = engine.QueryIssues(includeDone: true)
                .Where(view => cutoff == DateTime.MinValue || view.Issue.UpdatedAt >= cutoff)
                .ToArray();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Metric");
            table.AddColumn("Value");
            table.AddRow("Window", window);
            table.AddRow("Total Issues", issues.Length.ToString());

            foreach (var status in Enum.GetValues<IssueStatus>())
            {
                table.AddRow(status.ToString(), issues.Count(issue => issue.Issue.Status == status).ToString());
            }

            var overdue = issues.Count(issue => issue.Issue.DueDate.HasValue && issue.Issue.DueDate.Value.Date < now.Date);
            table.AddRow("Overdue", overdue.ToString());
            AnsiConsole.Write(table);
        }, dbOption, windowArgument);

        return command;
    }

    private static CliCommand BuildAgentCommand(Option<string> dbOption)
    {
        var command = new CliCommand("agent", "Agent-friendly JSON interface.");
        command.AddCommand(BuildAgentIssuesCommand(dbOption));
        command.AddCommand(BuildAgentCommandCommand(dbOption));
        return command;
    }

    private static CliCommand BuildAgentIssuesCommand(Option<string> dbOption)
    {
        var statusOption = new Option<string?>("--status");
        var statusNotOption = new Option<string?>("--not-status");
        var maxPriorityOption = new Option<int?>("--max-priority");
        var labelsOption = new Option<string?>("--labels");
        var dueBeforeOption = new Option<string?>("--due-before");
        var includeDoneOption = new Option<bool>("--include-done", () => true);

        var command = new CliCommand("issues", "Return issues as JSON.");
        command.AddOption(statusOption);
        command.AddOption(statusNotOption);
        command.AddOption(maxPriorityOption);
        command.AddOption(labelsOption);
        command.AddOption(dueBeforeOption);
        command.AddOption(includeDoneOption);

        command.SetHandler((string dbPath, string? statusText, string? statusNotText, int? maxPriority, string? labelsText, string? dueBeforeText, bool includeDone) =>
        {
            var engine = CreateEngine(dbPath);
            if (!TryBuildFilter(statusText, statusNotText, maxPriority, labelsText, dueBeforeText, out var filter, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                return;
            }

            Console.WriteLine(AgentRunner.GetIssuesJson(engine, filter, includeDone));
        }, dbOption, statusOption, statusNotOption, maxPriorityOption, labelsOption, dueBeforeOption, includeDoneOption);

        return command;
    }

    private static CliCommand BuildAgentCommandCommand(Option<string> dbOption)
    {
        var jsonOption = new Option<string?>("--json", "Raw command JSON.");
        var fileOption = new Option<FileInfo?>("--file", "Path to JSON payload file.");
        var actorOption = new Option<string?>("--actor", "Default actor for UpdateDescription/AddComment when payload omits actor.");
        var command = new CliCommand("command", "Execute JSON command.");
        command.AddOption(jsonOption);
        command.AddOption(fileOption);
        command.AddOption(actorOption);

        command.SetHandler((string dbPath, string? json, FileInfo? file, string? actor) =>
        {
            var engine = CreateEngine(dbPath);
            var payload = json;

            if (string.IsNullOrWhiteSpace(payload) && file is not null)
            {
                payload = File.ReadAllText(file.FullName);
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = Console.In.ReadToEnd();
            }

            Console.WriteLine(AgentRunner.ExecuteCommandJson(engine, payload, actor));
        }, dbOption, jsonOption, fileOption, actorOption);

        return command;
    }

    private static IssueEngine CreateEngine(string dbPath)
        => new(new SqliteEventStore(AppPaths.ResolveDatabasePath(dbPath)), new SystemClock());

    private static void RunTui(string dbPath)
    {
        var app = new TuiApp(CreateEngine(dbPath));
        app.Run();
    }

    private static bool TryResolveIssue(IssueEngine engine, string issueToken, out IssueId issueId)
    {
        if (!engine.TryResolveIssueToken(issueToken, out issueId, out var error))
        {
            AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
            return false;
        }

        return true;
    }

    private static bool TryBuildFilter(
        string? statusText,
        string? statusNotText,
        int? maxPriority,
        string? labelsText,
        string? dueBeforeText,
        out IssueFilter? filter,
        out string error)
    {
        filter = null;
        error = string.Empty;

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

        if (maxPriority.HasValue && (maxPriority.Value < 1 || maxPriority.Value > 5))
        {
            error = "max-priority must be between 1 and 5.";
            return false;
        }

        DateTime? dueBefore = null;
        if (!string.IsNullOrWhiteSpace(dueBeforeText))
        {
            if (!DateTime.TryParse(dueBeforeText, out var parsedDueBefore))
            {
                error = "Invalid --due-before date.";
                return false;
            }

            dueBefore = parsedDueBefore;
        }

        var labels = string.IsNullOrWhiteSpace(labelsText)
            ? null
            : labelsText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
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

        if (!parsedFilter.StatusEquals.HasValue &&
            !parsedFilter.StatusNotEquals.HasValue &&
            !parsedFilter.PriorityLessThanOrEqual.HasValue &&
            (parsedFilter.MustHaveLabels is null || parsedFilter.MustHaveLabels.Count == 0) &&
            !parsedFilter.DueBefore.HasValue)
        {
            filter = null;
            return true;
        }

        filter = parsedFilter;
        return true;
    }

    private static bool TryParseStatus(string value, out IssueStatus status)
        => Enum.TryParse(value, ignoreCase: true, out status);

    private static bool TryParseOptionalStatus(string? value, out IssueStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<IssueStatus>(value, ignoreCase: true, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static void PrintCommandResult(CommandExecutionResult result)
    {
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]{result.Message.EscapeMarkup()}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]{result.Message.EscapeMarkup()}[/]");
        if (result.IssueId.HasValue)
        {
            AnsiConsole.MarkupLine($"Issue: {result.IssueId.Value}");
        }

        if (result.EventId.HasValue)
        {
            AnsiConsole.MarkupLine($"Event: {result.EventId.Value}");
        }
    }
}

