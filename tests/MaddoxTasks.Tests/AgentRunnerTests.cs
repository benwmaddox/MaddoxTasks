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

    [Fact]
    public void ExecuteCommandJson_UsesCodexConfigWhenActorNotProvided()
    {
        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var previousCurrentDirectory = Environment.CurrentDirectory;
        var previousCodexModel = Environment.GetEnvironmentVariable("CODEX_MODEL");
        var previousOpenAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var previousAnthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
        var previousClaudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL");
        var previousGenericModel = Environment.GetEnvironmentVariable("MODEL");
        var previousMaddoxActor = Environment.GetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR");
        var previousMaddoxActorAlt = Environment.GetEnvironmentVariable("MADDOX_TASKS_ACTOR");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"maddox-agent-tests-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(tempRoot, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), """
model = "gpt-5.3-codex"
model_reasoning_effort = "high"
""");

        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("HOME", tempRoot);
            Environment.SetEnvironmentVariable("USERPROFILE", tempRoot);
            Environment.CurrentDirectory = tempRoot;
            Environment.SetEnvironmentVariable("CODEX_MODEL", null);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", null);
            Environment.SetEnvironmentVariable("CLAUDE_MODEL", null);
            Environment.SetEnvironmentVariable("MODEL", null);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR", null);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_ACTOR", null);

            var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
            var store = new InMemoryEventStoreForAgentTests();
            var engine = new IssueEngine(store, clock);
            var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
            Assert.True(createResult.Success);

            var response = AgentRunner.ExecuteCommandJson(
                engine,
                """{"type":"AddComment","issueId":"1","comment":"Automated note"}""");

            Assert.True(ResponseSuccess(response));
            Assert.Equal("gpt-5.3-codex high", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUserProfile);
            Environment.CurrentDirectory = previousCurrentDirectory;
            Environment.SetEnvironmentVariable("CODEX_MODEL", previousCodexModel);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", previousOpenAiModel);
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", previousAnthropicModel);
            Environment.SetEnvironmentVariable("CLAUDE_MODEL", previousClaudeModel);
            Environment.SetEnvironmentVariable("MODEL", previousGenericModel);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR", previousMaddoxActor);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_ACTOR", previousMaddoxActorAlt);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ExecuteCommandJson_UsesClaudeSettingsModelWhenActorNotProvided()
    {
        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var previousCurrentDirectory = Environment.CurrentDirectory;
        var previousCodexModel = Environment.GetEnvironmentVariable("CODEX_MODEL");
        var previousOpenAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        var previousAnthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
        var previousClaudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL");
        var previousGenericModel = Environment.GetEnvironmentVariable("MODEL");
        var previousMaddoxActor = Environment.GetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR");
        var previousMaddoxActorAlt = Environment.GetEnvironmentVariable("MADDOX_TASKS_ACTOR");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"maddox-agent-tests-{Guid.NewGuid():N}");
        var claudeDir = Path.Combine(tempRoot, ".claude");
        var codexHome = Path.Combine(tempRoot, ".codex-empty");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"), """
{
  "model": "claude-sonnet-4-5"
}
""");

        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("HOME", tempRoot);
            Environment.SetEnvironmentVariable("USERPROFILE", tempRoot);
            Environment.CurrentDirectory = tempRoot;
            Environment.SetEnvironmentVariable("CODEX_MODEL", null);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", null);
            Environment.SetEnvironmentVariable("CLAUDE_MODEL", null);
            Environment.SetEnvironmentVariable("MODEL", null);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR", null);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_ACTOR", null);

            var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
            var store = new InMemoryEventStoreForAgentTests();
            var engine = new IssueEngine(store, clock);
            var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
            Assert.True(createResult.Success);

            var response = AgentRunner.ExecuteCommandJson(
                engine,
                """{"type":"AddComment","issueId":"1","comment":"Automated note"}""");

            Assert.True(ResponseSuccess(response));
            Assert.Equal("claude-sonnet-4-5", engine.QueryIssues(includeDone: true).Single().Issue.Comments[0].Actor);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUserProfile);
            Environment.CurrentDirectory = previousCurrentDirectory;
            Environment.SetEnvironmentVariable("CODEX_MODEL", previousCodexModel);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", previousOpenAiModel);
            Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", previousAnthropicModel);
            Environment.SetEnvironmentVariable("CLAUDE_MODEL", previousClaudeModel);
            Environment.SetEnvironmentVariable("MODEL", previousGenericModel);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_AGENT_ACTOR", previousMaddoxActor);
            Environment.SetEnvironmentVariable("MADDOX_TASKS_ACTOR", previousMaddoxActorAlt);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ExecuteCommandJson_ParsesReadyForReviewStatusWithSpaces()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
        Assert.True(createResult.Success);

        var response = AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"ChangeStatus","issueId":"1","newStatus":"Ready for Review"}""");

        Assert.True(ResponseSuccess(response));
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void GetNextTaskJson_SelectsLowestPriorityAcrossActiveAndNext()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);

        var first = engine.Execute(new CreateIssue("First", null, Priority.From(3), null, null));
        var second = engine.Execute(new CreateIssue("Second", null, Priority.From(1), null, null));
        var third = engine.Execute(new CreateIssue("Third", null, Priority.From(2), null, null));
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(third.Success);

        Assert.True(engine.TryResolveIssueToken("1", out var firstId, out _));
        Assert.True(engine.TryResolveIssueToken("2", out var secondId, out _));
        Assert.True(engine.TryResolveIssueToken("3", out var thirdId, out _));
        Assert.True(engine.Execute(new ChangeStatus(firstId, Status.Active)).Success);
        Assert.True(engine.Execute(new ChangeStatus(secondId, Status.Next)).Success);
        Assert.True(engine.Execute(new ChangeStatus(thirdId, Status.Active)).Success);

        var response = AgentRunner.GetNextTaskJson(engine);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("sequence").GetInt32());
        Assert.Equal("Next", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("priority").GetInt32());
    }

    [Fact]
    public void GetNextTaskJson_PrefersActiveWhenPriorityTies()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);

        var first = engine.Execute(new CreateIssue("First", null, Priority.From(2), null, null));
        var second = engine.Execute(new CreateIssue("Second", null, Priority.From(2), null, null));
        Assert.True(first.Success);
        Assert.True(second.Success);

        Assert.True(engine.TryResolveIssueToken("1", out var firstId, out _));
        Assert.True(engine.TryResolveIssueToken("2", out var secondId, out _));
        Assert.True(engine.Execute(new ChangeStatus(firstId, Status.Next)).Success);
        Assert.True(engine.Execute(new ChangeStatus(secondId, Status.Active)).Success);

        var response = AgentRunner.GetNextTaskJson(engine);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("sequence").GetInt32());
        Assert.Equal("Active", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
    }

    [Fact]
    public void GetNextTaskJson_ReturnsNullWhenNoActiveOrNextIssuesExist()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);

        var create = engine.Execute(new CreateIssue("Backlog only", null, Priority.From(2), null, null));
        Assert.True(create.Success);

        var response = AgentRunner.GetNextTaskJson(engine);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(JsonValueKind.Null, document.RootElement.ValueKind);
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
