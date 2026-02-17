using Spectre.Console;
using IssueStatus = MaddoxTasks.Domain.Status;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;

namespace MaddoxTasks.Ui;

public sealed class TuiApp
{
    private static readonly IssueStatus[] StatusOrder =
    [
        IssueStatus.Active,
        IssueStatus.Next,
        IssueStatus.Blocked,
        IssueStatus.ReadyForReview,
        IssueStatus.Backlog,
        IssueStatus.Done
    ];

    private readonly IssueEngine _engine;
    private int _selectedIndex;
    private IssueFilter? _activeFilter;
    private IssueFilter? _lastFilter;

    public TuiApp(IssueEngine engine)
    {
        _engine = engine;
    }

    public void Run()
    {
        Console.CursorVisible = false;

        while (true)
        {
            var showDone = ShouldShowDone();
            var views = _engine.QueryIssues(_activeFilter, includeDone: showDone).ToList();

            if (views.Count == 0)
            {
                _selectedIndex = 0;
            }
            else if (_selectedIndex >= views.Count)
            {
                _selectedIndex = views.Count - 1;
            }

            Render(views, showDone);
            var keyInfo = Console.ReadKey(intercept: true);

            if (HandleNavigationKey(keyInfo, views.Count))
            {
                continue;
            }

            if (HandleGlobalKey(keyInfo))
            {
                break;
            }

            var selectedIssue = views.Count == 0 ? null : views[_selectedIndex];
            HandleActionKey(keyInfo, selectedIssue);
        }
    }

    private void Render(IReadOnlyList<IssueView> views, bool showDone)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Maddox Tasks[/]");
        RenderFilterLine();
        AnsiConsole.WriteLine();

        var cursor = 0;
        foreach (var status in StatusOrder)
        {
            if (status == IssueStatus.Done && !showDone)
            {
                continue;
            }

            var group = views.Where(view => view.Issue.Status == status).ToList();
            AnsiConsole.MarkupLine($"[bold]{status.ToDisplayString().ToUpperInvariant()} ({group.Count})[/]");

            if (group.Count == 0)
            {
                AnsiConsole.MarkupLine("  [grey]-[/]");
                continue;
            }

            foreach (var view in group)
            {
                var marker = cursor == _selectedIndex ? ">" : " ";
                var due = RenderDue(view.Issue.DueDate);
                var labels = view.Issue.Labels.Count == 0
                    ? "-"
                    : string.Join(",", view.Issue.Labels);

                var line = $"{marker} {view.ShortId,-4} {view.GuidPrefix}  P{view.Issue.Priority.Value}  {due,-12}  {view.Issue.Title}";
                if (labels != "-")
                {
                    line += $" [labels:{labels}]";
                }

                AnsiConsole.MarkupLine(line.EscapeMarkup());
                cursor++;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]up/down navigate   Enter open   n new   / filter   ? help   q quit[/]");
    }

    private void RenderFilterLine()
    {
        if (_activeFilter is null)
        {
            AnsiConsole.MarkupLine("[grey]Filter: none[/]");
            return;
        }

        var parts = new List<string>();
        if (_activeFilter.StatusEquals.HasValue)
        {
            parts.Add($"status={_activeFilter.StatusEquals.Value.ToDisplayString()}");
        }

        if (_activeFilter.StatusNotEquals.HasValue)
        {
            parts.Add($"status!={_activeFilter.StatusNotEquals.Value.ToDisplayString()}");
        }

        if (_activeFilter.PriorityLessThanOrEqual.HasValue)
        {
            parts.Add($"priority<={_activeFilter.PriorityLessThanOrEqual.Value}");
        }

        if (_activeFilter.MustHaveLabels is { Count: > 0 })
        {
            parts.Add($"labels={string.Join(",", _activeFilter.MustHaveLabels)}");
        }

        if (_activeFilter.DueBefore.HasValue)
        {
            parts.Add($"due<={_activeFilter.DueBefore.Value:yyyy-MM-dd}");
        }

        AnsiConsole.MarkupLine($"[grey]Filter: {string.Join(" ", parts)}[/]");
    }

    private bool HandleNavigationKey(ConsoleKeyInfo keyInfo, int itemCount)
    {
        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            if (itemCount > 0)
            {
                _selectedIndex = (_selectedIndex - 1 + itemCount) % itemCount;
            }

            return true;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            if (itemCount > 0)
            {
                _selectedIndex = (_selectedIndex + 1) % itemCount;
            }

            return true;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            _activeFilter = null;
            return true;
        }

        if (keyInfo.Key == ConsoleKey.R && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (_lastFilter is not null)
            {
                _activeFilter = _lastFilter;
            }

            return true;
        }

        return false;
    }

    private bool HandleGlobalKey(ConsoleKeyInfo keyInfo)
    {
        var keyChar = char.ToLowerInvariant(keyInfo.KeyChar);
        if (keyChar == 'q')
        {
            AnsiConsole.Clear();
            return true;
        }

        if (keyChar == '?')
        {
            RenderHelpOverlay();
            return false;
        }

        return false;
    }

    private void HandleActionKey(ConsoleKeyInfo keyInfo, IssueView? selectedIssue)
    {
        var keyChar = char.ToLowerInvariant(keyInfo.KeyChar);

        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
                if (selectedIssue is not null)
                {
                    OpenIssue(selectedIssue);
                }

                return;
        }

        switch (keyChar)
        {
            case '/':
                ConfigureFilter();
                return;
            case 'n':
                CreateIssue();
                return;
            case 's':
                if (selectedIssue is not null)
                {
                    ChangeStatus(selectedIssue);
                }

                return;
            case 'p':
                if (selectedIssue is not null)
                {
                    ChangePriority(selectedIssue);
                }

                return;
            case 't':
                if (selectedIssue is not null)
                {
                    ToggleLabel(selectedIssue);
                }

                return;
            case 'd':
                if (selectedIssue is not null)
                {
                    MarkDone(selectedIssue);
                }

                return;
        }
    }

    private bool ShouldShowDone()
    {
        if (_activeFilter?.StatusEquals == IssueStatus.Done)
        {
            return true;
        }

        if (_activeFilter?.StatusNotEquals == IssueStatus.Done)
        {
            return false;
        }

        return false;
    }

    private void OpenIssue(IssueView issueView)
    {
        var issueId = issueView.Issue.Id;

        while (true)
        {
            var refreshedIssue = TryGetIssue(issueId);
            if (refreshedIssue is null)
            {
                PauseWithMessage($"Issue '{issueId}' was not found.", success: false);
                return;
            }

            RenderIssueDetail(refreshedIssue);
            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Escape)
            {
                return;
            }

            var keyChar = char.ToLowerInvariant(keyInfo.KeyChar);
            switch (keyChar)
            {
                case 'q':
                    return;
                case 'c':
                    AddIssueComment(refreshedIssue);
                    break;
                case 's':
                    ChangeStatus(refreshedIssue);
                    break;
                case 'p':
                    ChangePriority(refreshedIssue);
                    break;
                case 't':
                    ToggleLabel(refreshedIssue);
                    break;
                case 'd':
                    EditDescription(refreshedIssue);
                    break;
                case 'h':
                    ShowDescriptionHistory(refreshedIssue);
                    break;
            }
        }
    }

    private void CreateIssue()
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Create issue[/]");

        var title = AnsiConsole.Ask<string>("Title:");
        var description = AnsiConsole.Ask<string>("Description (optional, blank for none):", string.Empty);
        var priorityText = AnsiConsole.Ask<string>("Priority (1-5, default 3):", "3");
        var dueText = AnsiConsole.Ask<string>("Due date (optional, yyyy-MM-dd):", string.Empty);

        Console.CursorVisible = false;

        if (!int.TryParse(priorityText, out var priorityRaw))
        {
            PauseWithMessage("Invalid priority value.", success: false);
            return;
        }

        Priority priority;
        try
        {
            priority = Priority.From(priorityRaw);
        }
        catch (ArgumentOutOfRangeException)
        {
            PauseWithMessage("Priority must be between 1 and 5.", success: false);
            return;
        }

        DateTime? dueDate = null;
        if (!string.IsNullOrWhiteSpace(dueText))
        {
            if (!DateTime.TryParse(dueText, out var parsedDueDate))
            {
                PauseWithMessage("Invalid due date format.", success: false);
                return;
            }

            dueDate = parsedDueDate;
        }

        var command = new CreateIssue(
            title,
            string.IsNullOrWhiteSpace(description) ? null : description,
            priority,
            ParentId: null,
            dueDate);

        var result = _engine.Execute(command);
        PauseWithMessage(result.Message, result.Success);
    }

    private void ChangeStatus(IssueView issueView)
    {
        Console.CursorVisible = true;
        var target = AnsiConsole.Prompt(
            new SelectionPrompt<IssueStatus>()
                .Title("New status")
                .AddChoices(Enum.GetValues<IssueStatus>())
                .UseConverter(status => status.ToDisplayString()));
        Console.CursorVisible = false;

        var result = _engine.Execute(new ChangeStatus(issueView.Issue.Id, target));
        PauseWithMessage(result.Message, result.Success);
    }

    private void ChangePriority(IssueView issueView)
    {
        Console.CursorVisible = true;
        var priorityText = AnsiConsole.Ask<string>("Priority (1-5):", issueView.Issue.Priority.Value.ToString());
        Console.CursorVisible = false;

        if (!int.TryParse(priorityText, out var parsedPriority))
        {
            PauseWithMessage("Invalid priority value.", success: false);
            return;
        }

        Priority priority;
        try
        {
            priority = Priority.From(parsedPriority);
        }
        catch (ArgumentOutOfRangeException)
        {
            PauseWithMessage("Priority must be between 1 and 5.", success: false);
            return;
        }

        var result = _engine.Execute(new ChangePriority(issueView.Issue.Id, priority));
        PauseWithMessage(result.Message, result.Success);
    }

    private void ToggleLabel(IssueView issueView)
    {
        Console.CursorVisible = true;
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Label action")
                .AddChoices("add", "remove"));
        var label = AnsiConsole.Ask<string>("Label:");
        Console.CursorVisible = false;

        Command command = mode == "add"
            ? new AddLabel(issueView.Issue.Id, label)
            : new RemoveLabel(issueView.Issue.Id, label);

        var result = _engine.Execute(command);
        PauseWithMessage(result.Message, result.Success);
    }

    private void MarkDone(IssueView issueView)
    {
        var result = _engine.Execute(new ChangeStatus(issueView.Issue.Id, IssueStatus.Done));
        PauseWithMessage(result.Message, result.Success);
    }

    private void EditDescription(IssueView issueView)
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Edit description[/]");

        var currentDescription = issueView.Issue.Description ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentDescription))
        {
            AnsiConsole.MarkupLine($"[grey]Current: {currentDescription.EscapeMarkup()}[/]");
        }

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Description action")
                .AddChoices("replace", "append", "clear", "cancel"));

        string nextDescription;
        switch (mode)
        {
            case "replace":
                nextDescription = AnsiConsole.Ask<string>("New description (blank clears):", string.Empty).Trim();
                break;
            case "append":
                var toAppend = AnsiConsole.Ask<string>("Text to append:");
                if (string.IsNullOrWhiteSpace(toAppend))
                {
                    Console.CursorVisible = false;
                    PauseWithMessage("Description append text cannot be empty.", success: false);
                    return;
                }

                var existing = currentDescription.Trim();
                nextDescription = string.IsNullOrWhiteSpace(existing)
                    ? toAppend.Trim()
                    : $"{existing}{Environment.NewLine}{toAppend.Trim()}";
                break;
            case "clear":
                nextDescription = string.Empty;
                break;
            default:
                Console.CursorVisible = false;
                return;
        }

        Console.CursorVisible = false;
        if (string.Equals(nextDescription, currentDescription, StringComparison.Ordinal))
        {
            PauseWithMessage("Description is unchanged.", success: false);
            return;
        }

        var result = _engine.Execute(new UpdateDescription(issueView.Issue.Id, nextDescription, "user"));
        PauseWithMessage(result.Message, result.Success);
    }

    private void AddIssueComment(IssueView issueView)
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Add comment[/]");
        var comment = AnsiConsole.Ask<string>("Comment:");
        Console.CursorVisible = false;

        var result = _engine.Execute(new AddComment(issueView.Issue.Id, comment, "user"));
        PauseWithMessage(result.Message, result.Success);
    }

    private void ShowDescriptionHistory(IssueView issueView)
    {
        var history = _engine.GetEventLog()
            .Where(issueEvent => issueEvent.IssueId == issueView.Issue.Id)
            .Select(issueEvent => issueEvent switch
            {
                IssueCreated created => new DescriptionHistoryEntry(created.Timestamp, "Created", created.Description ?? string.Empty, "n/a"),
                DescriptionUpdated updated => new DescriptionHistoryEntry(updated.Timestamp, "Updated", updated.Description, updated.Actor),
                _ => null
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();

        AnsiConsole.Clear();
        if (history.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]No description history found for this issue.[/]");
            AnsiConsole.MarkupLine("[grey]Press any key to return[/]");
            Console.ReadKey(intercept: true);
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("When (UTC)");
        table.AddColumn("Event");
        table.AddColumn("By");
        table.AddColumn("Description");

        for (var i = 0; i < history.Length; i++)
        {
            var entry = history[i];
            var description = string.IsNullOrWhiteSpace(entry.Description) ? "-" : entry.Description;
            table.AddRow(
                (i + 1).ToString(),
                entry.Timestamp.ToString("u"),
                entry.Source,
                entry.Actor.EscapeMarkup(),
                description.EscapeMarkup());
        }

        AnsiConsole.Write(new Panel(table).Header("Description history"));
        AnsiConsole.MarkupLine("[grey]Press any key to return[/]");
        Console.ReadKey(intercept: true);
    }

    private IssueView? TryGetIssue(IssueId issueId)
        => _engine.QueryIssues(includeDone: true).FirstOrDefault(view => view.Issue.Id == issueId);

    private static void RenderIssueDetail(IssueView issueView)
    {
        var issue = issueView.Issue;

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("Short ID", issueView.ShortId);
        grid.AddRow("GUID", issue.Id.ToString());
        grid.AddRow("Status", issue.Status.ToDisplayString());
        grid.AddRow("Priority", issue.Priority.Value.ToString());
        grid.AddRow("Created", issue.CreatedAt.ToString("u"));
        grid.AddRow("Updated", issue.UpdatedAt.ToString("u"));
        grid.AddRow("Due", issue.DueDate?.ToString("u") ?? "-");
        grid.AddRow("Labels", (issue.Labels.Count == 0 ? "-" : string.Join(",", issue.Labels)).EscapeMarkup());
        grid.AddRow("Title", issue.Title.EscapeMarkup());
        grid.AddRow("Description", (string.IsNullOrWhiteSpace(issue.Description) ? "-" : issue.Description).EscapeMarkup());

        AnsiConsole.Clear();
        AnsiConsole.Write(new Panel(grid).Header("Issue detail"));
        AnsiConsole.WriteLine();

        var comments = issue.Comments;
        if (comments.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Comments: none[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.ShowRowSeparators = true;
            table.AddColumn("When (UTC)");
            table.AddColumn("By");
            table.AddColumn("Comment");

            foreach (var comment in comments.TakeLast(8))
            {
                table.AddRow(comment.Timestamp.ToString("u"), comment.Actor.EscapeMarkup(), comment.Comment.EscapeMarkup());
            }

            AnsiConsole.Write(new Panel(table).Header($"Comments ({comments.Count})"));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Detail actions: c comment   s status   p priority   t label   d description   h desc-history   q/Esc back[/]");
    }

    private void ConfigureFilter()
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Filter[/]");
        var statusOptions = string.Join(", ", Enum.GetValues<IssueStatus>().Select(status => status.ToDisplayString()));
        var statusEqualsText = AnsiConsole.Ask<string>($"status equals ({statusOptions}, blank for none):", string.Empty);
        var statusNotText = AnsiConsole.Ask<string>("status not equals (blank for none):", string.Empty);
        var priorityText = AnsiConsole.Ask<string>("priority <= (1-5, blank for none):", string.Empty);
        var labelsText = AnsiConsole.Ask<string>("labels (comma-separated, blank for none):", string.Empty);
        var dueText = AnsiConsole.Ask<string>("due on/before (yyyy-MM-dd, blank for none):", string.Empty);
        Console.CursorVisible = false;

        if (!TryParseOptionalStatus(statusEqualsText, out var statusEquals))
        {
            PauseWithMessage($"Invalid status '{statusEqualsText}'.", success: false);
            return;
        }

        if (!TryParseOptionalStatus(statusNotText, out var statusNot))
        {
            PauseWithMessage($"Invalid status '{statusNotText}'.", success: false);
            return;
        }

        int? priorityMax = null;
        if (!string.IsNullOrWhiteSpace(priorityText))
        {
            if (!int.TryParse(priorityText, out var parsedPriority))
            {
                PauseWithMessage("Invalid priority filter.", success: false);
                return;
            }

            if (parsedPriority < 1 || parsedPriority > 5)
            {
                PauseWithMessage("Priority filter must be between 1 and 5.", success: false);
                return;
            }

            priorityMax = parsedPriority;
        }

        DateTime? dueBefore = null;
        if (!string.IsNullOrWhiteSpace(dueText))
        {
            if (!DateTime.TryParse(dueText, out var parsedDueDate))
            {
                PauseWithMessage("Invalid due date filter.", success: false);
                return;
            }

            dueBefore = parsedDueDate;
        }

        var labels = string.IsNullOrWhiteSpace(labelsText)
            ? null
            : labelsText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(IssueFiltering.NormalizeLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var filter = new IssueFilter
        {
            StatusEquals = statusEquals,
            StatusNotEquals = statusNot,
            PriorityLessThanOrEqual = priorityMax,
            MustHaveLabels = labels,
            DueBefore = dueBefore
        };

        if (FilterIsEmpty(filter))
        {
            _activeFilter = null;
            return;
        }

        _activeFilter = filter;
        _lastFilter = filter;
    }

    private static bool TryParseOptionalStatus(string input, out IssueStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (!StatusText.TryParse(input, out var parsed))
        {
            return false;
        }

        status = parsed;
        return true;
    }

    private static bool FilterIsEmpty(IssueFilter filter)
        => !filter.StatusEquals.HasValue
           && !filter.StatusNotEquals.HasValue
           && !filter.PriorityLessThanOrEqual.HasValue
           && (filter.MustHaveLabels is null || filter.MustHaveLabels.Count == 0)
           && !filter.DueBefore.HasValue;

    private void RenderHelpOverlay()
    {
        while (true)
        {
            AnsiConsole.Clear();
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Context").AddColumn("Key").AddColumn("Action");

            foreach (var binding in KeyBindingRegistry.All)
            {
                table.AddRow(binding.Context.ToString(), binding.Key, binding.Description);
            }

            var panel = new Panel(table)
                .Header("Keyboard shortcuts")
                .Border(BoxBorder.Rounded)
                .Expand();
            AnsiConsole.Write(panel);
            AnsiConsole.MarkupLine("[grey]Help overlay open. Press Esc, ?, or q to close.[/]");

            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Escape)
            {
                break;
            }

            var keyChar = char.ToLowerInvariant(keyInfo.KeyChar);
            if (keyChar is '?' or 'q')
            {
                break;
            }
        }
    }

    private void PauseWithMessage(string message, bool success)
    {
        AnsiConsole.Clear();
        var color = success ? "green" : "red";
        AnsiConsole.MarkupLine($"[{color}]{message.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("[grey]Press any key to continue[/]");
        Console.ReadKey(intercept: true);
    }

    private static string RenderDue(DateTime? dueDate)
    {
        if (!dueDate.HasValue)
        {
            return "-";
        }

        var today = DateTime.UtcNow.Date;
        if (dueDate.Value.Date < today)
        {
            return "OVERDUE";
        }

        return dueDate.Value.ToString("yyyy-MM-dd");
    }

    private sealed record DescriptionHistoryEntry(DateTime Timestamp, string Source, string Description, string Actor);
}
