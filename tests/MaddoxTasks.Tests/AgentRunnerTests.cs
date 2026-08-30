using System.Text.Json;
using MaddoxTasks.Agent;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public void ExecuteCommandJson_RequeueBlockedSerializesDedicatedStableResponse()
    {
        var timestamp = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var store = new InMemoryEventStoreForAgentTests();
        var first = IssueId.New();
        var skipped = IssueId.New();
        var second = IssueId.New();
        store.Append(new IssueCreated(Guid.NewGuid(), first, timestamp, "First", null, Status.Blocked, Priority.From(3), null, null));
        store.Append(new IssueCreated(Guid.NewGuid(), skipped, timestamp, "Skipped", null, Status.Next, Priority.From(3), null, null));
        store.Append(new IssueCreated(Guid.NewGuid(), second, timestamp, "Second", null, Status.Blocked, Priority.From(3), null, null));
        var engine = new IssueEngine(store, new FrozenClockForAgentTests(timestamp.AddHours(1)));

        using var preview = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"RequeueBlocked","dryRun":true}"""));

        Assert.Equal(["success", "message", "dryRun", "changedIssueIds", "skippedIssueIds"],
            preview.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(preview.RootElement.GetProperty("success").GetBoolean());
        Assert.True(preview.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal([first.ToString(), second.ToString()], preview.RootElement.GetProperty("changedIssueIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal([skipped.ToString()], preview.RootElement.GetProperty("skippedIssueIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(Status.Blocked, engine.GetState().Issues[first].Status);

        using var applied = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(engine, """{"type":"requeueblocked"}"""));
        Assert.True(applied.RootElement.GetProperty("success").GetBoolean());
        Assert.False(applied.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(Status.Next, engine.GetState().Issues[first].Status);
        Assert.Equal(Status.Next, engine.GetState().Issues[second].Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public void ExecuteCommandJson_RequeueBlockedRejectsNonBooleanDryRun(string dryRunJson)
    {
        var engine = new IssueEngine(
            new InMemoryEventStoreForAgentTests(),
            new FrozenClockForAgentTests(new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)));

        using var response = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(
            engine,
            $$"""{"type":"RequeueBlocked","dryRun":{{dryRunJson}}}"""));

        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Field 'dryRun' must be a boolean.", response.RootElement.GetProperty("message").GetString());
        Assert.Empty(response.RootElement.GetProperty("changedIssueIds").EnumerateArray());
        Assert.Empty(engine.GetEventLog());
    }

    [Fact]
    public void ExecuteCommandJson_CreateDefaultsToNext_ReportsStatus_AndSupportsBacklog()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStoreForAgentTests(), clock);

        using var defaultResponse = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"CreateIssue","title":"Next by default"}"""));
        Assert.True(defaultResponse.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Next", defaultResponse.RootElement.GetProperty("status").GetString());

        using var backlogResponse = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"CreateIssue","title":"Explicit backlog","status":"Backlog"}"""));
        Assert.True(backlogResponse.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Backlog", backlogResponse.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            [Status.Next, Status.Backlog],
            engine.QueryIssues(includeDone: true).Select(view => view.Issue.Status).ToArray());

        using var issues = JsonDocument.Parse(AgentRunner.GetIssuesJson(engine, null, includeDone: true));
        Assert.Equal("Next", issues.RootElement[0].GetProperty("status").GetString());
        Assert.Equal("Backlog", issues.RootElement[1].GetProperty("status").GetString());
        using var next = JsonDocument.Parse(AgentRunner.GetNextTaskJson(engine));
        Assert.Equal("Next", next.RootElement.GetProperty("status").GetString());
        Assert.Equal("Next by default", next.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void ExecuteCommandJson_CreateRejectsUnsupportedInitialStatusDeterministically()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStoreForAgentTests(), clock);

        foreach (var status in new[] { "Active", "0", "Backlog, Next" })
        {
            using var response = JsonDocument.Parse(AgentRunner.ExecuteCommandJson(
                engine,
                $$"""{"type":"CreateIssue","title":"Invalid","status":"{{status}}"}"""));
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("status").ValueKind);
            Assert.Equal(["success", "message", "issueId", "eventId", "status"], response.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        }

        Assert.Empty(engine.GetEventLog());
    }

    [Fact]
    public void GetNextTaskJson_SelectsChildBeforeActiveParent()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStoreForAgentTests(), clock);
        var parent = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Parent", null, Priority.From(1), null, null, Status.Backlog)).IssueId);
        var child = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Child", null, Priority.From(5), parent, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(parent, "repo:parent")).Success);
        Assert.True(engine.Execute(new AddLabel(child, "repo:child")).Success);
        Assert.True(engine.Execute(new ChangeStatus(parent, Status.Active)).Success);

        using var document = JsonDocument.Parse(AgentRunner.GetNextTaskJson(engine));

        Assert.Equal(child.ToString(), document.RootElement.GetProperty("issueId").GetString());
        Assert.Equal("Next", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void GetNextTaskJson_PreservesActivePreferenceForEqualRootPriority()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var engine = new IssueEngine(new InMemoryEventStoreForAgentTests(), clock);
        var next = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Next", null, Priority.From(2), null, null)).IssueId);
        var active = Assert.IsAssignableFrom<IssueId>(engine.Execute(
            new CreateIssue("Active", null, Priority.From(2), null, null)).IssueId);
        Assert.True(engine.Execute(new AddLabel(next, "repo:next")).Success);
        Assert.True(engine.Execute(new AddLabel(active, "repo:active")).Success);
        Assert.True(engine.Execute(new ChangeStatus(active, Status.Active)).Success);

        using var document = JsonDocument.Parse(AgentRunner.GetNextTaskJson(engine));

        Assert.Equal(active.ToString(), document.RootElement.GetProperty("issueId").GetString());
        Assert.Equal("Active", document.RootElement.GetProperty("status").GetString());
    }

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
        Assert.True(engine.Execute(new AddLabel(Assert.IsAssignableFrom<IssueId>(createResult.IssueId), "repo:review")).Success);

        var response = AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"ChangeStatus","issueId":"1","newStatus":"Ready for Review"}""");

        Assert.True(ResponseSuccess(response));
        Assert.Equal(Status.ReadyForReview, engine.QueryIssues(includeDone: true).Single().Issue.Status);
    }

    [Fact]
    public void ExecuteCommandJson_ParsesRejectedStatusAndSerializesIt()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var createResult = engine.Execute(new CreateIssue("Task", "Desc", Priority.From(3), null, null));
        Assert.True(createResult.Success);

        var response = AgentRunner.ExecuteCommandJson(
            engine,
            """{"type":"ChangeStatus","issueId":"1","newStatus":"Rejected"}""");

        Assert.True(ResponseSuccess(response));
        Assert.Equal(Status.Rejected, engine.QueryIssues(includeDone: true).Single().Issue.Status);
        var issuesJson = AgentRunner.GetIssuesJson(engine, new IssueFilter { StatusEquals = Status.Rejected }, includeDone: false);
        using var document = JsonDocument.Parse(issuesJson);
        Assert.Equal("Rejected", document.RootElement[0].GetProperty("status").GetString());
    }

    [Fact]
    public void GetIssuesJson_IncludesDeterministicRepositories()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var result = engine.Execute(new CreateIssue("Task", null, Priority.From(3), null, null, Status.Backlog));
        var issueId = Assert.IsAssignableFrom<IssueId>(result.IssueId);
        Assert.True(engine.Execute(new AddLabel(issueId, "Repo:Zeta")).Success);
        Assert.True(engine.Execute(new AddLabel(issueId, "repo:alpha")).Success);

        using var document = JsonDocument.Parse(AgentRunner.GetIssuesJson(engine, null, includeDone: true));
        var repositories = document.RootElement[0].GetProperty("repositories").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["alpha", "zeta"], repositories);
    }

    [Fact]
    public void ClaimJsonReturnsClaimAndNullWhenUnavailable()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var result = engine.Execute(new CreateIssue("Task", null, Priority.From(3), null, null, Status.Backlog));
        var issueId = Assert.IsAssignableFrom<IssueId>(result.IssueId);
        Assert.True(engine.Execute(new AddLabel(issueId, "repo:alpha")).Success);
        Assert.True(engine.Execute(new ChangeStatus(issueId, Status.Next)).Success);

        using var claimed = JsonDocument.Parse(AgentRunner.GetClaimJson(engine));
        Assert.Equal("Active", claimed.RootElement.GetProperty("status").GetString());
        Assert.Equal("alpha", claimed.RootElement.GetProperty("repositories")[0].GetString());
        Assert.Equal("null", AgentRunner.GetClaimJson(engine));
    }

    [Fact]
    public void ClaimDryRunDoesNotMutateTask()
    {
        var clock = new FrozenClockForAgentTests(new DateTime(2026, 2, 17, 15, 0, 0, DateTimeKind.Utc));
        var store = new InMemoryEventStoreForAgentTests();
        var engine = new IssueEngine(store, clock);
        var result = engine.Execute(new CreateIssue("Task", null, Priority.From(3), null, null, Status.Backlog));
        var issueId = Assert.IsAssignableFrom<IssueId>(result.IssueId);
        Assert.True(engine.Execute(new AddLabel(issueId, "repo:alpha")).Success);
        Assert.True(engine.Execute(new ChangeStatus(issueId, Status.Next)).Success);

        using var preview = JsonDocument.Parse(AgentRunner.GetClaimJson(engine, dryRun: true));
        Assert.Equal("Next", preview.RootElement.GetProperty("status").GetString());
        Assert.Equal(Status.Next, engine.QueryIssues(includeDone: true).Single().Issue.Status);
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
