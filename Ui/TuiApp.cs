using Spectre.Console;
using IssueStatus = MaddoxTasks.Domain.Status;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using System.Text;
using System.Threading;

namespace MaddoxTasks.Ui;

public sealed class TuiApp
{
    private const string UserCommentTint = "#9FB7D3";
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
    private ToastMessage? _toast;
    private StatusMessage? _lastStatusMessage;

    public TuiApp(IssueEngine engine)
    {
        _engine = engine;
    }

    public void Run()
    {
        Console.CursorVisible = false;

        while (true)
        {
            ExpireToastIfNeeded();
            var views = GetCurrentViews(out var showDone);
            Render(views, showDone);
            var keyInfo = ReadKeyWithToastRefresh(() =>
            {
                var refreshedViews = GetCurrentViews(out var refreshedShowDone);
                Render(refreshedViews, refreshedShowDone);
            });

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

    private IReadOnlyList<IssueView> GetCurrentViews(out bool showDone)
    {
        showDone = ShouldShowDone();
        var views = _engine.QueryIssues(_activeFilter, includeDone: showDone).ToList();

        if (views.Count == 0)
        {
            _selectedIndex = 0;
        }
        else if (_selectedIndex >= views.Count)
        {
            _selectedIndex = views.Count - 1;
        }

        return views;
    }

    private void Render(IReadOnlyList<IssueView> views, bool showDone)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Maddox Tasks[/]");
        RenderFilterLine();
        AnsiConsole.WriteLine();

        if (views.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No issues.[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Square)
                .AddColumn(new TableColumn("#"))
                .AddColumn(new TableColumn("Title"))
                .AddColumn(new TableColumn("Status"))
                .AddColumn(new TableColumn("Priority").Centered());

            var cursor = 0;
            foreach (var status in StatusOrder)
            {
                if (status == IssueStatus.Done && !showDone)
                {
                    continue;
                }

                foreach (var view in views.Where(v => v.Issue.Status == status))
                {
                    var selected = cursor == _selectedIndex;
                    var id = selected
                        ? $"[bold]> {view.ShortId.EscapeMarkup()}[/]"
                        : view.ShortId.EscapeMarkup();
                    var title = selected
                        ? $"[bold]{view.Issue.Title.EscapeMarkup()}[/]"
                        : view.Issue.Title.EscapeMarkup();

                    table.AddRow(id, title, StatusMarkup(status, selected), PriorityMarkup(view.Issue.Priority.Value, selected));
                    cursor++;
                }
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]up/down navigate   Enter open   n new   / filter   ? help   q quit[/]");
        RenderStatusBar();
    }

    private static string StatusMarkup(IssueStatus status, bool bold)
    {
        var color = status switch
        {
            IssueStatus.Active => "green",
            IssueStatus.Next => "cyan",
            IssueStatus.Blocked => "red",
            IssueStatus.ReadyForReview => "yellow",
            IssueStatus.Done => "grey",
            _ => "grey"
        };
        var text = status.ToDisplayString().EscapeMarkup();
        return bold ? $"[bold {color}]{text}[/]" : $"[{color}]{text}[/]";
    }

    private static string PriorityMarkup(int priority, bool bold)
    {
        var color = priority switch
        {
            1 => "red",
            2 => "darkorange3",
            3 => "yellow",
            _ => ""
        };
        var text = priority.ToString();
        if (color.Length == 0)
        {
            return bold ? $"[bold]{text}[/]" : text;
        }
        return bold ? $"[bold {color}]{text}[/]" : $"[{color}]{text}[/]";
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
            ExpireToastIfNeeded();
            var refreshedIssue = TryGetIssue(issueId);
            if (refreshedIssue is null)
            {
                PauseWithMessage($"Issue '{issueId}' was not found.", success: false);
                return;
            }

            RenderIssueDetail(refreshedIssue);
            var keyInfo = ReadKeyWithToastRefresh(() =>
            {
                var latest = TryGetIssue(issueId);
                if (latest is not null)
                {
                    RenderIssueDetail(latest);
                }
            });
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

        if (!TryPromptText("Title", out var title)
            || !TryPromptText("Description (optional, blank for none)", out var description)
            || !TryPromptText("Priority (1-5, default 3)", out var priorityText, "3")
            || !TryPromptText("Due date (optional, yyyy-MM-dd)", out var dueText))
        {
            Console.CursorVisible = false;
            return;
        }

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
        var statusChoices = Enum.GetValues<IssueStatus>();
        if (!TryPromptChoice("New status", statusChoices, status => status.ToDisplayString(), out var target))
        {
            Console.CursorVisible = false;
            return;
        }

        Console.CursorVisible = false;

        var result = _engine.Execute(new ChangeStatus(issueView.Issue.Id, target));
        PauseWithMessage(result.Message, result.Success);
    }

    private void ChangePriority(IssueView issueView)
    {
        Console.CursorVisible = true;
        if (!TryPromptText("Priority (1-5)", out var priorityText, issueView.Issue.Priority.Value.ToString()))
        {
            Console.CursorVisible = false;
            return;
        }

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
        var modeChoices = new[] { "add", "remove" };
        if (!TryPromptChoice("Label action", modeChoices, mode => mode, out var mode)
            || !TryPromptText("Label", out var label))
        {
            Console.CursorVisible = false;
            return;
        }

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

        var modeChoices = new[] { "replace", "append", "clear", "cancel" };
        if (!TryPromptChoice("Description action", modeChoices, mode => mode, out var mode))
        {
            Console.CursorVisible = false;
            return;
        }

        string nextDescription;
        switch (mode)
        {
            case "replace":
                if (!TryPromptText("New description (blank clears)", out var replaceDescription))
                {
                    Console.CursorVisible = false;
                    return;
                }

                nextDescription = replaceDescription.Trim();
                break;
            case "append":
                if (!TryPromptText("Text to append", out var toAppend))
                {
                    Console.CursorVisible = false;
                    return;
                }

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
        RenderIssueDetail(issueView);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Add comment[/]");
        if (!TryPromptText("Comment", out var comment))
        {
            Console.CursorVisible = false;
            return;
        }

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

    private void RenderIssueDetail(IssueView issueView)
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
                var commentText = comment.Comment.EscapeMarkup();
                if (string.Equals(comment.Actor, "user", StringComparison.OrdinalIgnoreCase))
                {
                    commentText = $"[{UserCommentTint}]{commentText}[/]";
                }

                table.AddRow(comment.Timestamp.ToString("u"), comment.Actor.EscapeMarkup(), commentText);
            }

            AnsiConsole.Write(new Panel(table).Header($"Comments ({comments.Count})"));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Detail actions: c comment   s status   p priority   t label   d description   h desc-history   q/Esc back[/]");
        RenderStatusBar();
    }

    private void ConfigureFilter()
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold]Filter[/]");
        var statusOptions = string.Join(", ", Enum.GetValues<IssueStatus>().Select(status => status.ToDisplayString()));
        if (!TryPromptText($"status equals ({statusOptions}, blank for none)", out var statusEqualsText)
            || !TryPromptText("status not equals (blank for none)", out var statusNotText)
            || !TryPromptText("priority <= (1-5, blank for none)", out var priorityText)
            || !TryPromptText("labels (comma-separated, blank for none)", out var labelsText)
            || !TryPromptText("due on/before (yyyy-MM-dd, blank for none)", out var dueText))
        {
            Console.CursorVisible = false;
            return;
        }

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

    private static bool TryPromptText(string label, out string value, string? defaultValue = null)
    {
        value = string.Empty;
        Console.Write($"{label}: ");
        var initialText = defaultValue ?? string.Empty;
        var startLeft = Console.CursorLeft;
        var startTop = Console.CursorTop;

        if (initialText.Length > 0)
        {
            Console.Write(initialText);
        }

        var promptResult = ReadEditableLine(startLeft, startTop, initialText);
        Console.WriteLine();

        if (!promptResult.Accepted)
        {
            return false;
        }

        value = promptResult.Text;
        if (defaultValue is not null && string.IsNullOrEmpty(value))
        {
            value = defaultValue;
        }

        return true;
    }

    private static TextPromptResult ReadEditableLine(int startLeft, int startTop, string initialText)
    {
        var buffer = new StringBuilder(initialText);
        var cursor = buffer.Length;
        var previousLength = buffer.Length;

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            var handled = true;

            switch (keyInfo.Key)
            {
                case ConsoleKey.Enter:
                    return new TextPromptResult(Accepted: true, Text: buffer.ToString());
                case ConsoleKey.Escape:
                    return new TextPromptResult(Accepted: false, Text: buffer.ToString());
                case ConsoleKey.LeftArrow:
                    if (cursor > 0)
                    {
                        cursor--;
                    }

                    break;
                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length)
                    {
                        cursor++;
                    }

                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = buffer.Length;
                    break;
                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                    }

                    break;
                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                    }

                    break;
                default:
                    if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        if (keyInfo.Key == ConsoleKey.A)
                        {
                            cursor = 0;
                            break;
                        }

                        if (keyInfo.Key == ConsoleKey.E)
                        {
                            cursor = buffer.Length;
                            break;
                        }

                        handled = false;
                        break;
                    }

                    if (!char.IsControl(keyInfo.KeyChar))
                    {
                        buffer.Insert(cursor, keyInfo.KeyChar);
                        cursor++;
                        break;
                    }

                    handled = false;
                    break;
            }

            if (!handled)
            {
                continue;
            }

            RedrawEditableLine(startLeft, startTop, buffer, cursor, ref previousLength);
        }
    }

    private static bool TryPromptChoice<T>(string title, IReadOnlyList<T> choices, Func<T, string> converter, out T selection)
    {
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        var index = 0;
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold]{title.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine("[grey]up/down move   Enter select   Esc cancel[/]");
            AnsiConsole.WriteLine();

            for (var i = 0; i < choices.Count; i++)
            {
                var text = converter(choices[i]).EscapeMarkup();
                if (i == index)
                {
                    AnsiConsole.MarkupLine($"[bold]> {text}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  {text}");
                }
            }

            var keyInfo = Console.ReadKey(intercept: true);
            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    index = (index - 1 + choices.Count) % choices.Count;
                    break;
                case ConsoleKey.DownArrow:
                    index = (index + 1) % choices.Count;
                    break;
                case ConsoleKey.Enter:
                    selection = choices[index];
                    return true;
                case ConsoleKey.Escape:
                    selection = choices[0];
                    return false;
            }
        }
    }

    private static void RedrawEditableLine(int startLeft, int startTop, StringBuilder buffer, int cursor, ref int previousLength)
    {
        SetCursorFromOffset(startLeft, startTop, 0);

        var text = buffer.ToString();
        Console.Write(text);

        if (previousLength > text.Length)
        {
            Console.Write(new string(' ', previousLength - text.Length));
        }

        previousLength = text.Length;
        SetCursorFromOffset(startLeft, startTop, cursor);
    }

    private static void SetCursorFromOffset(int startLeft, int startTop, int offset)
    {
        var bufferWidth = Math.Max(Console.BufferWidth, 1);
        var absolute = startLeft + Math.Max(offset, 0);
        var left = absolute % bufferWidth;
        var top = startTop + (absolute / bufferWidth);
        Console.SetCursorPosition(left, top);
    }

    private ConsoleKeyInfo ReadKeyWithToastRefresh(Action rerender)
    {
        while (true)
        {
            if (Console.KeyAvailable)
            {
                return Console.ReadKey(intercept: true);
            }

            if (ExpireToastIfNeeded())
            {
                rerender();
            }

            Thread.Sleep(50);
        }
    }

    private bool ExpireToastIfNeeded()
    {
        if (_toast is null || DateTime.UtcNow < _toast.ExpiresAtUtc)
        {
            return false;
        }

        _toast = null;
        return true;
    }

    private void RenderStatusBar()
    {
        if (_toast is not null)
        {
            var toastColor = _toast.Success ? "green" : "red";
            AnsiConsole.MarkupLine($"[{toastColor}]Status: {_toast.Message.EscapeMarkup()}[/]");
            return;
        }

        if (_lastStatusMessage is null)
        {
            return;
        }

        var lastColor = _lastStatusMessage.Success ? "grey" : "maroon";
        AnsiConsole.MarkupLine($"[{lastColor}]Last: {_lastStatusMessage.Message.EscapeMarkup()}[/]");
    }

    private void PauseWithMessage(string message, bool success)
    {
        _lastStatusMessage = new StatusMessage(message, success);
        _toast = new ToastMessage(message, success, DateTime.UtcNow.AddSeconds(2));
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
    private sealed record TextPromptResult(bool Accepted, string Text);
    private sealed record ToastMessage(string Message, bool Success, DateTime ExpiresAtUtc);
    private sealed record StatusMessage(string Message, bool Success);
}
