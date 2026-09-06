using System.Reflection;
using System.Text.Json;
using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class CommitHookRecoveryTests
{
    [Fact]
    public async Task Commit_RestoresKnownHookSideEffectsAndCommitsOriginalIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maddox-worker-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var runner = new ProcessRunner(new NoopContainment(), new NullLog());
            await Git(runner, directory, "init", "-b", "main");
            await Git(runner, directory, "config", "user.name", "Maddox Worker Test");
            await Git(runner, directory, "config", "user.email", "worker@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(directory, "task.txt"), "baseline\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "generated.stasis"), "baseline\n");
            await Git(runner, directory, "add", "-A");
            await Git(runner, directory, "commit", "--no-verify", "-m", "baseline");

            var hook = await Git(runner, directory, "rev-parse", "--path-format=absolute", "--git-path", "hooks/pre-commit");
            await File.WriteAllTextAsync(hook.Output.Trim(), """
                #!/bin/sh
                printf 'hook rewrite\n' > generated.stasis
                printf 'hook addition\n' > hook-created.stasis
                echo 'Stasis pre-commit: checking canonical source format'
                echo 'all Stasis files are formatted'
                echo 'Commit blocked: stage the formatted Stasis changes, then commit again.' >&2
                exit 1
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "task.txt"), "task change\n");
            await Git(runner, directory, "add", "-A");

            var configPath = Path.Combine(directory, "worker.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new WorkerConfig(
                1, TimeSpan.FromMinutes(15), 1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), "prompt.md", "model", "medium",
                3, TimeSpan.FromHours(2), TimeSpan.FromMinutes(30), [], [], "squash", "MaddoxTasks.exe", "codex", "gh", directory, Path.Combine(directory, "worktrees"))));
            var host = new WorkerHost(configPath, directory, processes: runner, log: new NullLog());
            var commit = typeof(WorkerHost).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)commit.Invoke(host, [new Workspace("Repo", directory, "codex/task-1-test", "https://example.invalid/repo.git"), "task commit", CancellationToken.None])!;

            Assert.Equal("task change\n", await File.ReadAllTextAsync(Path.Combine(directory, "task.txt")));
            Assert.Equal("baseline\n", (await File.ReadAllTextAsync(Path.Combine(directory, "generated.stasis"))).Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(directory, "hook-created.stasis")));
            Assert.Equal(string.Empty, (await Git(runner, directory, "status", "--porcelain")).Output);
            Assert.Equal("task commit", (await Git(runner, directory, "log", "-1", "--format=%s")).Output.Trim());
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<ExecResult> Git(IProcessRunner runner, string directory, params string[] arguments)
    {
        var result = await runner.RunAsync("git", arguments, directory, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error);
        return result;
    }

    private sealed class NullLog : IRollingLog
    {
        public void Write(string level, string message, object? data = null) { }
    }
}
