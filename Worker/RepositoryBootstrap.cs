namespace MaddoxTasks.Worker;

public sealed class RepositoryBootstrap(IProcessRunner processes, string ghExe, string? privateRepositoryOwner = null)
{
    public async Task<string> EnsureAsync(string repoRoot, string repository, CancellationToken ct)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, repository));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
            throw new InvalidOperationException("Invalid repository path: " + repository);
        for (var directory = new DirectoryInfo(path); !directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase); directory = directory.Parent!)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Repository path traverses a link: " + repository);

        async Task<ExecResult> Run(string executable, params string[] args)
        {
            var result = await processes.RunAsync(executable, args, path, ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"{executable} {args[0]} failed (exit {result.ExitCode}).");
            return result;
        }

        // A parent checkout does not make this project a repository of its own.
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            await Run("git", "init", "-b", "main");
        var top = (await Run("git", "rev-parse", "--show-toplevel")).Output.Trim();
        if (!Path.GetFullPath(top).TrimEnd(Path.DirectorySeparatorChar).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Git repository root does not match project: " + repository);

        var remotes = (await Run("git", "remote")).Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!remotes.Contains("origin", StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(privateRepositoryOwner) || !System.Text.RegularExpressions.Regex.IsMatch(privateRepositoryOwner, "^[A-Za-z0-9][A-Za-z0-9-]*$"))
                throw new InvalidOperationException("Configure privateRepositoryOwner to authorize new private GitHub repositories.");
            var owner = (await Run(ghExe, "api", "--hostname", "github.com", "user", "--jq", ".login")).Output.Trim();
            if (!owner.Equals(privateRepositoryOwner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Authenticated GitHub account does not match privateRepositoryOwner.");
            // Creation is atomic: existing names and authentication errors fail, never attach an unrelated remote.
            var created = (await Run(ghExe, "api", "--hostname", "github.com", "--method", "POST", "user/repos", "-f", "name=" + Path.GetFileName(path), "-F", "private=true", "--jq", ".clone_url")).Output.Trim();
            var expected = "https://github.com/" + owner + "/" + Path.GetFileName(path) + ".git";
            if (!created.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Created repository URL does not match the authorized destination.");
            await Run("git", "remote", "add", "origin", expected);
        }
        await Run("git", "remote", "get-url", "origin");
        await Run("git", "fetch", "origin");
        var refs = (await Run("git", "ls-remote", "--heads", "origin")).Output;
        if (string.IsNullOrWhiteSpace(refs))
        {
            var head = await processes.RunAsync("git", ["rev-parse", "--verify", "HEAD"], path, ct);
            if (head.ExitCode != 0)
            {
                // Verify an unborn branch, rather than treating every rev-parse failure as an empty repository.
                var branch = (await Run("git", "symbolic-ref", "--short", "HEAD")).Output.Trim();
                var existing = await processes.RunAsync("git", ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], path, ct);
                if (existing.ExitCode != 1) throw new InvalidOperationException("Cannot verify unborn repository: " + repository);
                if (!string.IsNullOrWhiteSpace((await Run("git", "diff", "--cached", "--name-only")).Output))
                    throw new InvalidOperationException("Review and commit the existing staged initial files before bootstrap: " + repository);
                var add = new List<string> { "add", "--all", "--", "." };
                var manifestPath = Path.Combine(path, "stasis.json");
                if (File.Exists(manifestPath))
                {
                    using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (manifest.RootElement.TryGetProperty("output", out var output) && output.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var outputPath = Path.GetFullPath(Path.Combine(path, output.GetString()!));
                        if (!outputPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Stasis output must be beneath the project root.");
                        var relative = Path.GetRelativePath(path, outputPath).Replace('\\', '/');
                        add.Add(":(exclude,literal)" + relative);
                    }
                    add.Add(":(exclude,literal).stasis_cache");
                }
                await Run("git", add.ToArray());
                await Run("git", "commit", "--allow-empty", "-m", "Initialize project baseline");
            }
            await Run("git", "push", "--set-upstream", "origin", "HEAD");
            await Run("git", "fetch", "origin");
        }
        await Run("git", "remote", "set-head", "origin", "--auto");
        return path;
    }
}
