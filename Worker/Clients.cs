using System.Text.Json;

namespace MaddoxTasks.Worker;

public sealed record CheckState(string Name, string State, string Bucket, string Link, string PullRequestUrl = "", string Details = "")
{
    public string Id => $"{Name}|{State}|{Link}";
    public bool IsFailure => Bucket.Equals("fail", StringComparison.OrdinalIgnoreCase) || State is "FAILURE" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED";
    public bool IsPending => Bucket.Equals("pending", StringComparison.OrdinalIgnoreCase) || State is "PENDING" or "QUEUED" or "IN_PROGRESS" or "EXPECTED" or "WAITING" or "REQUESTED";
}

public sealed record PullRequestSnapshot(bool Merged, IReadOnlyList<CheckState> Checks, IReadOnlyList<ReviewFeedback> Feedback)
{
    public bool IsGreen(IReadOnlyCollection<string> ignoredChecks)
    {
        var relevant = Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        return relevant.All(check => !check.IsFailure && !check.IsPending);
    }
    public IReadOnlyList<CheckState> Failures(IReadOnlyCollection<string> ignoredChecks) => Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase) && check.IsFailure).ToArray();
}

public interface IGitHubClient
{
    Task<PullRequestSnapshot> InspectAsync(string pullRequestUrl, bool includeFeedback, CancellationToken cancellationToken);
    Task ReplyAsync(string pullRequestUrl, ReviewFeedback feedback, string replyBody, CancellationToken cancellationToken);
    Task ResolveAsync(string pullRequestUrl, string threadId, CancellationToken cancellationToken);
    Task MergeAsync(string pullRequestUrl, CancellationToken cancellationToken);
}

public sealed class GitHubClient : IGitHubClient
{
    private readonly IProcessRunner processes;
    private readonly Func<WorkerConfig> config;
    private readonly IRollingLog log;
    public GitHubClient(IProcessRunner processes, Func<WorkerConfig> config, IRollingLog log) { this.processes = processes; this.config = config; this.log = log; }

    public async Task<PullRequestSnapshot> InspectAsync(string pullRequestUrl, bool includeFeedback, CancellationToken cancellationToken)
    {
        var id = Parse(pullRequestUrl);
        var settings = config();
        var view = await Require(settings.GhExe, ["pr", "view", pullRequestUrl, "--json", "mergedAt"], settings.RepoRoot, cancellationToken);
        using var viewJson = JsonDocument.Parse(view.Output);
        var merged = viewJson.RootElement.TryGetProperty("mergedAt", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String;
        if (merged) return new PullRequestSnapshot(true, [], []);

        var checkResult = await processes.RunAsync(settings.GhExe, ["pr", "checks", pullRequestUrl, "--json", "name,state,bucket,link"], settings.RepoRoot, cancellationToken);
        var checks = await AddFailureLogsAsync(ParseChecks(checkResult.Output), id, settings, cancellationToken);
        var feedback = includeFeedback ? await GetFeedback(id, settings, cancellationToken) : [];
        log.Write("info", "github.inspect", new { pullRequestUrl, checks = checks.Count, feedback = feedback.Count });
        return new PullRequestSnapshot(false, checks, feedback);
    }

    public async Task ReplyAsync(string pullRequestUrl, ReviewFeedback feedback, string replyBody, CancellationToken cancellationToken)
    {
        var id = Parse(pullRequestUrl);
        var settings = config();
        await Require(settings.GhExe, ["api", "--method", "POST", $"repos/{id.Owner}/{id.Repository}/pulls/{id.Number}/comments/{feedback.CommentDatabaseId}/replies", "-f", $"body={replyBody}"], settings.RepoRoot, cancellationToken);
        log.Write("info", "github.review.replied", new { pullRequestUrl, feedback.ThreadId });
    }

    public async Task ResolveAsync(string pullRequestUrl, string threadId, CancellationToken cancellationToken)
    {
        _ = Parse(pullRequestUrl);
        var settings = config();
        const string mutation = "mutation($threadId:ID!){resolveReviewThread(input:{threadId:$threadId}){thread{id isResolved}}}";
        await Require(settings.GhExe, ["api", "graphql", "-f", $"query={mutation}", "-f", $"threadId={threadId}"], settings.RepoRoot, cancellationToken);
        log.Write("info", "github.review.resolved", new { pullRequestUrl, threadId });
    }

    public async Task MergeAsync(string pullRequestUrl, CancellationToken cancellationToken)
    {
        var settings = config();
        await Require(settings.GhExe, ["pr", "merge", pullRequestUrl, "--squash", "--delete-branch"], settings.RepoRoot, cancellationToken);
        log.Write("info", "github.merge", new { pullRequestUrl, method = "squash" });
    }

    private async Task<IReadOnlyList<ReviewFeedback>> GetFeedback(PullRequestId id, WorkerConfig settings, CancellationToken cancellationToken)
    {
        const string query = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100){nodes{id isResolved comments(last:1){nodes{id databaseId body url}}}}}}}";
        var result = await Require(settings.GhExe, ["api", "graphql", "-f", $"query={query}", "-f", $"owner={id.Owner}", "-f", $"name={id.Repository}", "-F", $"number={id.Number}"], settings.RepoRoot, cancellationToken);
        using var document = JsonDocument.Parse(result.Output);
        var nodes = document.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetProperty("reviewThreads").GetProperty("nodes");
        var feedback = new List<ReviewFeedback>();
        foreach (var thread in nodes.EnumerateArray())
        {
            if (thread.GetProperty("isResolved").GetBoolean()) continue;
            var comment = thread.GetProperty("comments").GetProperty("nodes").EnumerateArray().LastOrDefault();
            if (comment.ValueKind == JsonValueKind.Undefined) continue;
            var threadNodeId = thread.GetProperty("id").GetString()!;
            var commentNodeId = comment.GetProperty("id").GetString()!;
            var databaseId = comment.GetProperty("databaseId").GetInt64();
            feedback.Add(new ReviewFeedback(threadNodeId, commentNodeId, databaseId, comment.GetProperty("body").GetString() ?? string.Empty, comment.GetProperty("url").GetString() ?? string.Empty));
        }
        return feedback;
    }

    private static List<CheckState> ParseChecks(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        return document.RootElement.EnumerateArray().Select(item => new CheckState(
            item.GetProperty("name").GetString() ?? string.Empty,
            item.TryGetProperty("state", out var state) ? state.GetString() ?? string.Empty : string.Empty,
            item.TryGetProperty("bucket", out var bucket) ? bucket.GetString() ?? string.Empty : string.Empty,
            item.TryGetProperty("link", out var link) ? link.GetString() ?? string.Empty : string.Empty)).ToList();
    }

    private async Task<List<CheckState>> AddFailureLogsAsync(List<CheckState> checks, PullRequestId pullRequest, WorkerConfig settings, CancellationToken cancellationToken)
    {
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index];
            if (!check.IsFailure || !TryGetActionsRunId(check.Link, out var runId)) continue;
            var logs = await processes.RunAsync(settings.GhExe, ["run", "view", runId, "--log-failed", "--repo", $"{pullRequest.Owner}/{pullRequest.Repository}"], settings.RepoRoot, cancellationToken);
            var text = logs.ExitCode == 0 ? logs.Output : logs.Error;
            if (text.Length > 16_000) text = text[^16_000..];
            checks[index] = check with { Details = text };
        }
        return checks;
    }

    private static bool TryGetActionsRunId(string link, out string runId)
    {
        runId = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var marker = Array.FindIndex(parts, part => part.Equals("runs", StringComparison.OrdinalIgnoreCase));
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || marker < 0 || marker + 1 >= parts.Length) return false;
        runId = parts[marker + 1];
        return runId.All(char.IsDigit);
    }

    private async Task<ExecResult> Require(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(executable, arguments, workingDirectory, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {result.Error.Trim()}");
        return result;
    }

    private static PullRequestId Parse(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || segments.Length != 4 || segments[2] != "pull" || !int.TryParse(segments[3], out var number)) throw new InvalidDataException($"Not a canonical GitHub pull request URL: {url}");
        return new PullRequestId(segments[0], segments[1], number);
    }
    private sealed record PullRequestId(string Owner, string Repository, int Number);
}
