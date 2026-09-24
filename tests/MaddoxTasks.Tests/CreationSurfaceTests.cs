using MaddoxTasks.Cli;
using MaddoxTasks.Domain;
using MaddoxTasks.Application;
using MaddoxTasks.Infrastructure;
using MaddoxTasks.Ui;
using Microsoft.Data.Sqlite;

namespace MaddoxTasks.Tests;

public sealed class CreationSurfaceTests
{
    [Fact]
    public void CliCreateStatus_DefaultsToNext_AndAcceptsBacklog()
    {
        Assert.True(CliRunner.TryParseCreateStatus(null, out var defaultStatus, out var defaultError));
        Assert.Equal(Status.Next, defaultStatus);
        Assert.Empty(defaultError);

        Assert.True(CliRunner.TryParseCreateStatus("Backlog", out var backlogStatus, out var backlogError));
        Assert.Equal(Status.Backlog, backlogStatus);
        Assert.Empty(backlogError);
    }

    [Fact]
    public void CliCreateStatus_RejectsOtherInitialStatuses()
    {
        foreach (var value in new[] { "Active", "0", "Backlog, Next" })
        {
            Assert.False(CliRunner.TryParseCreateStatus(value, out _, out var error));
            Assert.Contains("Next", error, StringComparison.Ordinal);
            Assert.Contains("Backlog", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiCreateCommand_DefaultsToNext_AndAcceptsBacklog()
    {
        var defaultCommand = TuiApp.BuildCreateIssueCommand("Task", null, Priority.From(3), null);
        var backlogCommand = TuiApp.BuildCreateIssueCommand("Task", null, Priority.From(3), null, Status.Backlog);
        var dependentCommand = TuiApp.BuildCreateIssueCommand("Task", null, Priority.From(3), null, blockedBy: ["1"]);

        Assert.Equal(Status.Next, defaultCommand.Status);
        Assert.Equal(Status.Backlog, backlogCommand.Status);
        Assert.Equal(["1"], dependentCommand.BlockedBy);
    }

    [Fact]
    public async Task CliCreate_StoresDefaultNextAndExplicitBacklog()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"maddox-cli-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "tasks.db");

        try
        {
            Assert.Equal(0, await CliRunner.InvokeAsync(["--db", dbPath, "create", "Default"]));
            Assert.Equal(0, await CliRunner.InvokeAsync(["--db", dbPath, "create", "Backlog", "--status", "Backlog"]));

            var engine = new IssueEngine(new SqliteEventStore(dbPath), new SystemClock());
            Assert.Equal(
                [Status.Next, Status.Backlog],
                engine.QueryIssues(includeDone: true).Select(view => view.Issue.Status).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CliCreateAndBlockersCommand_SupportDependencyTokensAndClearing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"maddox-cli-dependencies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "tasks.db");

        try
        {
            Assert.Equal(0, await CliRunner.InvokeAsync(["--db", dbPath, "create", "Prerequisite", "--status", "Backlog"]));
            Assert.Equal(0, await CliRunner.InvokeAsync(["--db", dbPath, "create", "Dependent", "--blocked-by", "1"]));

            var engine = new IssueEngine(new SqliteEventStore(dbPath), new SystemClock());
            var dependent = engine.QueryIssues(includeDone: true).Single(view => view.Issue.Title == "Dependent");
            Assert.Equal("Prerequisite", Assert.Single(dependent.BlockedBy!).Title);
            Assert.False(Assert.Single(dependent.BlockedBy!).IsSatisfied);

            Assert.Equal(0, await CliRunner.InvokeAsync(["--db", dbPath, "blockers", dependent.ShortId[1..]]));
            Assert.Empty(engine.GetState().Issues[dependent.Issue.Id].BlockerIds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
