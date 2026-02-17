using System.Text.Json;
using MaddoxTasks.Agent;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public void ExecuteCommandJson_UsesDefaultActorWhenPayloadOmitsActor()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
        Assert.True(createResult.Success);

        var response = AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"AddComment","issueId":"1","comment":"Automated note"}""",
            "gpt-5.2");

        Assert.True(ResponseSuccess(response));
        Assert.Equal("gpt-5.2", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
    }

    [Fact]
    public void ExecuteCommandJson_PayloadActorOverridesDefaultActor()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
        Assert.True(createResult.Success);

        var response = AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"AddComment","issueId":"1","comment":"Automated note","actor":"claude-sonnet"}""",
            "gpt-5.2");

        Assert.True(ResponseSuccess(response));
        Assert.Equal("claude-sonnet", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
    }

    [Fact]
    public void ExecuteCommandJson_AcceptsBomPrefixedJson()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
        Assert.True(createResult.Success);

        const string bom = "\uFEFF";
        var response = AgentRunner.ExecuteCommandJson(
            engine,
            bom + """{"type":"AddComment","issueId":"1","comment":"Automated note"}""",
            "gpt-5.2");

        Assert.True(ResponseSuccess(response));
        Assert.Equal("gpt-5.2", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
    }

    private static bool ResponseSuccess(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        return document.RootElement.GetProperty("success").GetBoolean();
    }
}

file sealed class InMemoryEventStoreForAgentTests : IEventStore
{
    private readonly List<IssueEvent> _events = [];

    public IReadOnlyList<IssueEvent> LoadAll() => _events.ToArray();

    public void Append(IssueEvent issueEvent) => _events.Add(issueEvent);
}

file sealed class FrozenClockForAgentTests : IClock
{
    public FrozenClockForAgentTests(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; }
}
