using System.Diagnostics;
using System.Globalization;

namespace MaddoxTasks.Infrastructure;

public interface IGitHubPullRequestClient
{
    GitHubPullRequest Get(string url);
}

public sealed record GitHubPullRequest(string Url, string? State, DateTimeOffset? MergedAt);

public sealed class GitHubCliPullRequestClient : IGitHubPullRequestClient
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public GitHubCliPullRequestClient(string executable = "gh", TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? "gh" : executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "GitHub CLI timeout must be positive.");
        }
    }

    public GitHubPullRequest Get(string url) => GetAsync(url).GetAwaiter().GetResult();

    private async Task<GitHubPullRequest> GetAsync(string url)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("pr");
        startInfo.ArgumentList.Add("view");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("state,mergedAt,url");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start GitHub CLI '{_executable}'.");
        using var cancellation = new CancellationTokenSource(_timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while cancellation was being handled.
            }

            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            }
            catch (Exception) when (outputTask.IsCanceled || errorTask.IsCanceled)
            {
                // Preserve the concise timeout error below.
            }

            throw new InvalidOperationException($"gh pr view timed out after {_timeout.TotalSeconds:0.#} seconds for '{url}'.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? $"exit code {process.ExitCode}" : error.Trim();
            throw new InvalidOperationException($"gh pr view failed for '{url}': {detail}");
        }

        using var document = System.Text.Json.JsonDocument.Parse(output);
        var root = document.RootElement;
        var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
        DateTimeOffset? mergedAt = null;
        if (root.TryGetProperty("mergedAt", out var mergedElement) && mergedElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var text = mergedElement.GetString();
            if (!string.IsNullOrWhiteSpace(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                mergedAt = parsed;
            }
        }

        return new GitHubPullRequest(url, state, mergedAt);
    }
}
