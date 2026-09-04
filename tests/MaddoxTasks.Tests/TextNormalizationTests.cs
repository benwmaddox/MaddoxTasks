using MaddoxTasks.Application;
using MaddoxTasks.Domain;

namespace MaddoxTasks.Tests;

public sealed class TextNormalizationTests
{
    private static readonly DateTime Timestamp = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> LineBreakVariants => new()
    {
        "first\nsecond",
        "first\r\nsecond",
        "first\rsecond"
    };

    [Theory]
    [MemberData(nameof(LineBreakVariants))]
    public void CreateIssue_DescriptionIsTrimmedAndNormalizedDuringPlanningAndReplay(string description)
    {
        var planned = Assert.IsType<IssueCreated>(CommandPlanner.Plan(
            new CreateIssue("Task", $"  {description}  ", Priority.From(3), null, null),
            IssueState.Replay([]),
            Timestamp));

        Assert.Equal("first\r\nsecond", planned.Description);

        var issue = Assert.Single(IssueState.Replay([planned]).Issues.Values);
        Assert.Equal("first\r\nsecond", issue.Description);
    }

    [Theory]
    [MemberData(nameof(LineBreakVariants))]
    public void UpdateDescription_IsTrimmedAndNormalizedDuringPlanningAndReplay(string description)
    {
        var issueId = IssueId.New();
        var created = CreateEvent(issueId);
        var state = IssueState.Replay([created]);

        var planned = Assert.IsType<DescriptionUpdated>(CommandPlanner.Plan(
            new UpdateDescription(issueId, $"  {description}  "),
            state,
            Timestamp.AddMinutes(1)));

        Assert.Equal("first\r\nsecond", planned.Description);

        var issue = IssueState.Replay([created, planned]).Issues[issueId];
        Assert.Equal("first\r\nsecond", issue.Description);
    }

    [Theory]
    [MemberData(nameof(LineBreakVariants))]
    public void AddComment_IsTrimmedAndNormalizedDuringPlanningAndReplay(string comment)
    {
        var issueId = IssueId.New();
        var created = CreateEvent(issueId);
        var state = IssueState.Replay([created]);

        var planned = Assert.IsType<CommentAdded>(CommandPlanner.Plan(
            new AddComment(issueId, $"  {comment}  "),
            state,
            Timestamp.AddMinutes(1)));

        Assert.Equal("first\r\nsecond", planned.Comment);

        var issue = IssueState.Replay([created, planned]).Issues[issueId];
        Assert.Equal("first\r\nsecond", Assert.Single(issue.Comments).Comment);
    }

    [Fact]
    public void Replay_NormalizesHistoricalTextEventsWithoutChangingTheirWhitespace()
    {
        var createdOnlyId = IssueId.New();
        var updatedId = IssueId.New();
        var commentedId = IssueId.New();
        var events = new IssueEvent[]
        {
            new IssueCreated(Guid.NewGuid(), createdOnlyId, Timestamp, "Created", "  first\nsecond  ", Status.Backlog, Priority.From(3), null, null),
            CreateEvent(updatedId),
            new DescriptionUpdated(Guid.NewGuid(), updatedId, Timestamp.AddMinutes(1), "  first\rsecond  "),
            CreateEvent(commentedId),
            new CommentAdded(Guid.NewGuid(), commentedId, Timestamp.AddMinutes(1), "  first\r\nsecond  ")
        };

        var state = IssueState.Replay(events);

        Assert.Equal("  first\r\nsecond  ", state.Issues[createdOnlyId].Description);
        Assert.Equal("  first\r\nsecond  ", state.Issues[updatedId].Description);
        Assert.Equal("  first\r\nsecond  ", Assert.Single(state.Issues[commentedId].Comments).Comment);
    }

    private static IssueCreated CreateEvent(IssueId issueId) => new(
        Guid.NewGuid(),
        issueId,
        Timestamp,
        "Task",
        string.Empty,
        Status.Backlog,
        Priority.From(3),
        null,
        null);
}
