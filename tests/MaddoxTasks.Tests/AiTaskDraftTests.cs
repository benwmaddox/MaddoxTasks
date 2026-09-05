using System.Text.Json;
using MaddoxTasks.Web;

namespace MaddoxTasks.Tests;

public sealed class AiTaskDraftTests
{
    [Fact]
    public void ParseDraftRequiresAndClonesTheBoundedContract()
    {
        var draft = CodexTaskDraftGenerator.ParseDraft("""
            {
              "title": "Prepare release notes",
              "description": "Summarize the changes for the next release.",
              "status": "Next",
              "priority": 3,
              "parentId": null,
              "dueDate": "2026-09-30",
              "labels": ["docs", "repo:website"]
            }
            """);

        Assert.Equal("Prepare release notes", draft.GetProperty("title").GetString());
        Assert.Equal("Next", draft.GetProperty("status").GetString());
        Assert.Equal(3, draft.GetProperty("priority").GetInt32());
        Assert.True(draft.GetProperty("parentId").ValueKind == JsonValueKind.Null);
        Assert.Equal("2026-09-30", draft.GetProperty("dueDate").GetString());
        Assert.Equal(["docs", "repo:website"], draft.GetProperty("labels").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData("{\"description\":\"x\",\"status\":\"Next\",\"priority\":3,\"parentId\":null,\"dueDate\":null,\"labels\":[]}", "title")]
    [InlineData("{\"title\":\"x\",\"description\":\"x\",\"status\":\"Active\",\"priority\":3,\"parentId\":null,\"dueDate\":null,\"labels\":[]}", "status")]
    [InlineData("{\"title\":\"x\",\"description\":\"x\",\"status\":\"Next\",\"priority\":0,\"parentId\":null,\"dueDate\":null,\"labels\":[]}", "priority")]
    [InlineData("{\"title\":\"x\",\"description\":\"x\",\"status\":\"Next\",\"priority\":3,\"parentId\":false,\"dueDate\":null,\"labels\":[]}", "parentId")]
    [InlineData("{\"title\":\"x\",\"description\":\"x\",\"status\":\"Next\",\"priority\":3,\"parentId\":null,\"dueDate\":\"2026-02-30\",\"labels\":[]}", "dueDate")]
    [InlineData("{\"title\":\"x\",\"description\":\"x\",\"status\":\"Next\",\"priority\":3,\"parentId\":null,\"dueDate\":null,\"labels\":[\"repo:\"]}", "repo")]
    public void ParseDraftRejectsInvalidFields(string json, string field)
    {
        var exception = Assert.Throws<InvalidDataException>(() => CodexTaskDraftGenerator.ParseDraft(json));
        Assert.Contains(field, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDraftRejectsUnknownFieldsAndOverlongTitles()
    {
        var unknown = """
            {"title":"x","description":"x","status":"Next","priority":3,"parentId":null,"dueDate":null,"labels":[],"extra":true}
            """;
        Assert.Contains("unknown", Assert.Throws<InvalidDataException>(() => CodexTaskDraftGenerator.ParseDraft(unknown)).Message,
            StringComparison.OrdinalIgnoreCase);

        var title = new string('x', 501);
        var overlong = $$"""
            {"title":"{{title}}","description":"x","status":"Next","priority":3,"parentId":null,"dueDate":null,"labels":[]}
            """;
        Assert.Contains("500", Assert.Throws<InvalidDataException>(() => CodexTaskDraftGenerator.ParseDraft(overlong)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsUseReadOnlyEphemeralStdinAndOutputFiles()
    {
        var arguments = CodexTaskDraftGenerator.BuildArguments(
            @"C:\temp\schema.json",
            @"C:\temp\last-message.json",
            @"C:\temp\isolated",
            "test-model");

        Assert.Equal("exec", arguments[0]);
        Assert.Contains("--ephemeral", arguments);
        Assert.Contains("--ignore-user-config", arguments);
        Assert.Contains("--sandbox", arguments);
        Assert.Contains("read-only", arguments);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Contains("--output-schema", arguments);
        Assert.Contains(@"C:\temp\schema.json", arguments);
        Assert.Contains("--output-last-message", arguments);
        Assert.Contains(@"C:\temp\last-message.json", arguments);
        Assert.Contains("-m", arguments);
        Assert.Contains("test-model", arguments);
        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public void BuildPromptStatesSafeDefaultsAndCurrentDate()
    {
        var prompt = CodexTaskDraftGenerator.BuildPrompt("Write a task for the parser", new DateOnly(2026, 9, 5));

        Assert.Contains("structure-only", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not use tools", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status Next", prompt, StringComparison.Ordinal);
        Assert.Contains("priority 3", prompt, StringComparison.Ordinal);
        Assert.Contains("parentId null", prompt, StringComparison.Ordinal);
        Assert.Contains("dueDate null", prompt, StringComparison.Ordinal);
        Assert.Contains("empty labels", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-09-05", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not invent a due date", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Write a task for the parser", prompt, StringComparison.Ordinal);
    }
}
