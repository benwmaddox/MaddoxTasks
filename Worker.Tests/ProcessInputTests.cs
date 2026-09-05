using MaddoxTasks.Worker;
using Xunit;

namespace MaddoxTasks.Worker.Tests;

public sealed class ProcessInputTests
{
    [Fact]
    public void ResearchPrompt_ReferencesBoundedSnapshotWithoutEmbeddingLargeHistory()
    {
        var task = new TaskDto(474, "source-id", "Source", new string('x', 3_000_000), []);
        var path = Path.Combine(Path.GetTempPath(), "research snapshot.json");
        var prompt = WorkerHost.BuildResearchPrompt(task, path);
        Assert.True(prompt.Length < 10_000);
        Assert.Contains("source-id", prompt);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(path), prompt);
        Assert.Contains("Parse the worker-supplied snapshot locally", prompt);
        Assert.Contains("selected source task", prompt);
        Assert.Contains("live web search tools", prompt);
        Assert.Contains("cite source URLs", prompt);
        Assert.Contains("current Blocked task records", prompt);
        Assert.DoesNotContain(task.Description, prompt);
    }

    [Fact]
    public void ResearchSnapshot_IncludesSelectedTaskAndRelatedBlockedTasks()
    {
        var task = new TaskDto(499, "source-id", "Unblock", "Triage three tasks", []);
        var snapshot = WorkerHost.BuildResearchSnapshot(task, "[{\"sequence\":389,\"status\":\"Blocked\"}]");
        using var document = System.Text.Json.JsonDocument.Parse(snapshot);

        Assert.Equal(499, document.RootElement.GetProperty("selectedTask").GetProperty("Sequence").GetInt32());
        Assert.Equal(389, document.RootElement.GetProperty("blockedTasks")[0].GetProperty("sequence").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargePrompt_RoundTripsThroughChildStdin(bool resume)
    {
        var prompt = string.Concat(Enumerable.Repeat("Unicode café 日本語 \"quoted\" \\path\n", 100_000));
        var original = resume ? new[] { "exec", "resume", "thread-id", "--json", prompt } : new[] { "exec", "--json", prompt };
        var input = ProcessArguments.WithPromptOnStandardInput(original);
        Assert.Equal("-", input.Arguments[^1]);
        Assert.Equal(original[..^1], input.Arguments[..^1]);
        Assert.True(string.Join(" ", input.Arguments).Length < 100);
        using var runner = new ProcessRunner(new NoopContainment(), new NullLog());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        var result = await runner.RunAsync(shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::Out.Write([Console]::In.ReadToEnd())"],
            Path.GetTempPath(), timeout.Token, standardInput: input.StandardInput);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(prompt.Replace("\n", Environment.NewLine), result.Output);
    }

    private sealed class NullLog : IRollingLog
    {
        public void Write(string level, string message, object? data = null) { }
    }
}
