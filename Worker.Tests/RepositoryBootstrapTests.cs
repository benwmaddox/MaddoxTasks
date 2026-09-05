using MaddoxTasks.Worker;

namespace MaddoxTasks.Worker.Tests;

public sealed class RepositoryBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "maddox-bootstrap-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "project");
    public RepositoryBootstrapTests() => Directory.CreateDirectory(Project);

    [Fact]
    public async Task ExistingOriginIsFetchedWithoutStagingOrChangingCheckout()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        var runner = new Runner(Project);
        await new RepositoryBootstrap(runner, "gh", "owner").EnsureAsync(root, "project", default);
        Assert.Contains("git fetch origin", runner.Calls);
        Assert.DoesNotContain(runner.Calls, x => x.StartsWith("gh ") || x.StartsWith("git add") || x.StartsWith("git pull") || x.StartsWith("git push"));
    }

    [Fact]
    public async Task MissingLocalRepositoryIsInitializedEvenInsideAncestorRepository()
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var runner = new Runner(Project) { MissingOrigin = true };
        await new RepositoryBootstrap(runner, "gh", "owner").EnsureAsync(root, "project", default);
        Assert.Equal("git init -b main", runner.Calls[0]);
        Assert.Contains(runner.Calls, x => x.Contains("POST user/repos -f name=project -F private=true"));
        Assert.Contains("git remote add origin https://github.com/owner/project.git", runner.Calls);
    }

    [Fact]
    public async Task FailedPrivateCreationDoesNotPushOrAdoptCollision()
    {
        var runner = new Runner(Project) { MissingOrigin = true, CreationFails = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryBootstrap(runner, "gh", "owner").EnsureAsync(root, "project", default));
        Assert.DoesNotContain(runner.Calls, x => x.StartsWith("git push") || x.StartsWith("git remote add") || x.StartsWith("git add"));
    }

    [Fact]
    public async Task UnbornRepositoryImportsBaselineWithManifestOutputExcluded()
    {
        var runner = new Runner(Project) { EmptyRemote = true, Unborn = true };
        File.WriteAllText(Path.Combine(Project, "stasis.json"), "{\"output\":\"build\"}");
        await new RepositoryBootstrap(runner, "gh", "owner").EnsureAsync(root, "project", default);
        Assert.Contains("git add --all -- . :(exclude,literal)build :(exclude,literal).stasis_cache", runner.Calls);
        Assert.Contains("git commit --allow-empty -m Initialize project baseline", runner.Calls);
        Assert.Contains("git push --set-upstream origin HEAD", runner.Calls);
    }

    [Fact]
    public async Task MissingOwnerAuthorizationDoesNotCreateRemote()
    {
        var runner = new Runner(Project) { MissingOrigin = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryBootstrap(runner, "gh").EnsureAsync(root, "project", default));
        Assert.DoesNotContain(runner.Calls, x => x.Contains("POST user/repos"));
    }

    [Fact]
    public async Task DifferentAuthenticatedOwnerDoesNotCreateRemote()
    {
        var runner = new Runner(Project) { MissingOrigin = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryBootstrap(runner, "gh", "different-owner").EnsureAsync(root, "project", default));
        Assert.DoesNotContain(runner.Calls, x => x.Contains("POST user/repos"));
    }

    [Fact]
    public async Task ExistingCommitIsPushedToEmptyRemoteWithoutStagingDirtyFiles()
    {
        var runner = new Runner(Project) { EmptyRemote = true };
        await new RepositoryBootstrap(runner, "gh").EnsureAsync(root, "project", default);
        Assert.Contains("git push --set-upstream origin HEAD", runner.Calls);
        Assert.DoesNotContain(runner.Calls, x => x.StartsWith("git add") || x.StartsWith("git commit"));
    }

    [Fact]
    public async Task MismatchedGitRootIsRejected()
    {
        var runner = new Runner(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryBootstrap(runner, "gh").EnsureAsync(root, "project", default));
        Assert.DoesNotContain(runner.Calls, x => x.StartsWith("gh "));
    }

    [Fact]
    public async Task OutsideRootIsRejectedBeforeCommands()
    {
        var runner = new Runner(Project);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryBootstrap(runner, "gh").EnsureAsync(root, "..", default));
        Assert.Empty(runner.Calls);
    }

    public void Dispose() => Directory.Delete(root, true);

    private sealed class Runner(string project) : IProcessRunner
    {
        public bool MissingOrigin { get; init; }
        public bool CreationFails { get; init; }
        public bool EmptyRemote { get; init; }
        public bool Unborn { get; init; }
        public List<string> Calls { get; } = [];
        public Task<ExecResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken, Action<string>? outputLine = null, TerminalOutputDirective? terminalOutput = null, string? standardInput = null)
        {
            var command = executable + " " + string.Join(' ', arguments);
            Calls.Add(command);
            var output = command switch
            {
                "git rev-parse --show-toplevel" => project,
                "git remote" => MissingOrigin ? "" : "origin",
                "gh api --hostname github.com user --jq .login" => "owner",
                "gh api --hostname github.com --method POST user/repos -f name=project -F private=true --jq .clone_url" => "https://github.com/owner/project.git",
                "git ls-remote --heads origin" => EmptyRemote ? "" : "abc refs/heads/main",
                "git symbolic-ref --short HEAD" => "main",
                _ => ""
            };
            var failure = (CreationFails && command.Contains("POST user/repos")) || (Unborn && command == "git rev-parse --verify HEAD");
            var code = failure ? 128 : command == "git show-ref --verify --quiet refs/heads/main" ? 1 : 0;
            return Task.FromResult(new ExecResult(code, output, failure ? "failed" : ""));
        }
    }
}
