namespace MaddoxTasks.Ui;

public sealed record KeyBinding(
    string Key,
    string Description,
    KeyContext Context);

public enum KeyContext
{
    Navigation,
    IssueAction,
    Filtering,
    Global
}

public static class KeyBindingRegistry
{
    public static IReadOnlyList<KeyBinding> All { get; } =
    [
        new("up/down", "Navigate", KeyContext.Navigation),
        new("Enter", "Open issue", KeyContext.Navigation),
        new("Esc", "Clear filter / close overlay", KeyContext.Navigation),
        new("n", "Create new issue", KeyContext.IssueAction),
        new("s", "Change status", KeyContext.IssueAction),
        new("p", "Change priority", KeyContext.IssueAction),
        new("t", "Add/remove label", KeyContext.IssueAction),
        new("d", "Mark done", KeyContext.IssueAction),
        new("e", "Edit description", KeyContext.IssueAction),
        new("/", "Open filter bar", KeyContext.Filtering),
        new("Ctrl+r", "Reapply last filter", KeyContext.Filtering),
        new("?", "Show this help", KeyContext.Global),
        new("q", "Quit", KeyContext.Global)
    ];
}

