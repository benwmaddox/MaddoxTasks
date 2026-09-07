using System.Text.Json;

namespace MaddoxTasks.Worker;

public enum CheckClassification
{
    Passing,
    CodeRepair,
    TransientRerun,
    HumanGate,
    Pending,
    Unknown
}

public sealed record CheckState(string Name, string State, string Bucket, string Link, string PullRequestUrl = "", string Details = "", string BaseRefName = "")
{
    public string Id => $"{Name}|{State}|{Link}";

    public CheckClassification Classification
    {
        get
        {
            var state = State.Trim().ToUpperInvariant();
            var bucket = Bucket.Trim().ToLowerInvariant();
            if (state == "ACTION_REQUIRED") return CheckClassification.HumanGate;
            if (state == "FAILURE" || bucket == "fail") return CheckClassification.CodeRepair;
            if (state is "STARTUP_FAILURE" or "CANCELLED" or "TIMED_OUT") return CheckClassification.TransientRerun;
            if (bucket == "pending" || state is "PENDING" or "QUEUED" or "IN_PROGRESS" or "EXPECTED" or "WAITING" or "REQUESTED")
                return CheckClassification.Pending;
            if (state is "SUCCESS" or "NEUTRAL" or "SKIPPED" || bucket is "pass" or "skipping")
                return CheckClassification.Passing;
            return CheckClassification.Unknown;
        }
    }

    public bool IsCodeRepair => Classification == CheckClassification.CodeRepair;
    public bool IsTransient => Classification == CheckClassification.TransientRerun;
    public bool NeedsHumanApproval => Classification == CheckClassification.HumanGate;
    public bool IsFailure => Classification is CheckClassification.CodeRepair or CheckClassification.TransientRerun or CheckClassification.HumanGate;
    public bool IsPending => Classification is CheckClassification.Pending or CheckClassification.Unknown;
}

public sealed record PullRequestSnapshot(
    bool Merged,
    IReadOnlyList<CheckState> Checks,
    IReadOnlyList<ReviewFeedback> Feedback,
    string Mergeable = "MERGEABLE",
    string MergeStateStatus = "CLEAN",
    string HeadOid = "",
    string BaseRefName = "",
    string State = "OPEN",
    bool InspectionComplete = true,
    string InspectionError = "")
{
    public bool IsOpen => State.Equals("OPEN", StringComparison.OrdinalIgnoreCase);
    public bool Open => IsOpen;
    public bool MergeabilityPending => !Merged && Mergeable.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);
    public bool HasMergeConflict => !Merged && (Mergeable.Equals("CONFLICTING", StringComparison.OrdinalIgnoreCase)
        || MergeStateStatus.Equals("DIRTY", StringComparison.OrdinalIgnoreCase));

    public bool IsGreen(IReadOnlyCollection<string> ignoredChecks)
    {
        if (Merged || !IsOpen || !InspectionComplete) return false;
        var relevant = Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        return relevant.All(check => check.Classification == CheckClassification.Passing);
    }

    public IReadOnlyList<CheckState> Failures(IReadOnlyCollection<string> ignoredChecks) =>
        Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase) && check.IsFailure).ToArray();

    public IReadOnlyList<CheckState> CodeRepairs(IReadOnlyCollection<string> ignoredChecks) =>
        Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase) && check.IsCodeRepair).ToArray();

    public IReadOnlyList<CheckState> TransientFailures(IReadOnlyCollection<string> ignoredChecks) =>
        Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase) && check.IsTransient).ToArray();

    public IReadOnlyList<CheckState> HumanGates(IReadOnlyCollection<string> ignoredChecks) =>
        Checks.Where(check => !ignoredChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase) && check.NeedsHumanApproval).ToArray();
}

public interface IGitHubClient
{
    Task<PullRequestSnapshot> InspectAsync(string pullRequestUrl, bool includeFeedback, CancellationToken cancellationToken);
    Task ReplyAsync(string pullRequestUrl, ReviewFeedback feedback, string replyBody, CancellationToken cancellationToken);
    Task ResolveAsync(string pullRequestUrl, string threadId, CancellationToken cancellationToken);
    Task MergeAsync(string pullRequestUrl, CancellationToken cancellationToken);

    // A default keeps older test and embedding clients source-compatible. The
    // production client overrides it, and the worker supplies its own bounded
    // cancellation token; this method never retries internally.
    Task RerunAsync(string actionsRunUrl, CancellationToken cancellationToken) => Task.CompletedTask;

    Task RerunWorkflowAsync(string actionsRunUrl, CancellationToken cancellationToken) => RerunAsync(actionsRunUrl, cancellationToken);
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
        var view = await processes.RunAsync(settings.GhExe, ["pr", "view", pullRequestUrl, "--json", "state,mergedAt,mergeable,mergeStateStatus,headRefOid,baseRefName"], settings.RepoRoot, cancellationToken);
        if (view.ExitCode != 0)
            return IncompleteSnapshot(view, "pull-request inspection failed");

        if (!TryParsePullRequestView(view.Output, out var merged, out var state, out var mergeable, out var mergeStateStatus, out var headOid, out var baseRefName, out var viewError))
            return IncompleteSnapshot(viewError ?? "pull-request inspection returned malformed JSON");

        // A merged PR is terminal and does not need a checks query. Retain the
        // state from GitHub so a closed-but-unmerged PR cannot look reviewable.
        if (merged)
        {
            var mergedSnapshot = new PullRequestSnapshot(true, [], [], mergeable, mergeStateStatus, headOid, baseRefName, state, true);
            log.Write("info", "github.inspect", new { pullRequestUrl, checks = 0, feedback = 0, state, inspectionComplete = true });
            return mergedSnapshot;
        }

        var checkResult = await processes.RunAsync(settings.GhExe, ["pr", "checks", pullRequestUrl, "--json", "name,state,bucket,link"], settings.RepoRoot, cancellationToken);
        var checkParse = ParseChecks(checkResult);
        var checks = await AddFailureLogsAsync(checkParse.Checks, id, settings, cancellationToken);
        var complete = checkParse.Complete;
        var errors = checkParse.Error;
        IReadOnlyList<ReviewFeedback> feedback = [];
        if (includeFeedback)
        {
            try
            {
                feedback = await GetFeedback(id, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                complete = false;
                errors = AppendError(errors, "review feedback inspection timed out");
            }
            catch (Exception exception)
            {
                complete = false;
                errors = AppendError(errors, "review feedback inspection failed: " + Limit(exception.Message, 500));
            }
        }

        log.Write("info", "github.inspect", new { pullRequestUrl, checks = checks.Count, feedback = feedback.Count, state, inspectionComplete = complete, error = errors });
        return new PullRequestSnapshot(false, checks, feedback, mergeable, mergeStateStatus, headOid, baseRefName, state, complete, errors ?? string.Empty);
    }

    public async Task RerunAsync(string actionsRunUrl, CancellationToken cancellationToken)
    {
        if (!TryParseActionsRun(actionsRunUrl, out var owner, out var repository, out var runId))
            throw new InvalidDataException($"Not a canonical GitHub Actions run URL: {actionsRunUrl}");
        var settings = config();
        await Require(settings.GhExe, ["run", "rerun", runId, "--repo", $"{owner}/{repository}"], settings.RepoRoot, cancellationToken);
        log.Write("info", "github.check.rerun", new { actionsRunUrl, runId, repository = $"{owner}/{repository}" });
    }

    public Task RerunWorkflowAsync(string actionsRunUrl, CancellationToken cancellationToken) => RerunAsync(actionsRunUrl, cancellationToken);

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

    private static CheckParseResult ParseChecks(ExecResult result)
    {
        var output = result.Output.Trim();
        var error = result.Error.Trim();
        if (TryParseCheckArray(output, out var checks, out var parseError))
        {
            // gh uses exit code 8 for pending checks. A valid JSON array is
            // still a complete inspection in that case; the individual
            // pending states keep the snapshot non-green.
            return (result.ExitCode is 0 or 8) && !ContainsFailureSignal(error)
                ? new CheckParseResult(checks, true, null)
                : new CheckParseResult([], false, "pull-request checks inspection failed: " + Limit(ExecResultDiagnostics.Failure(result), 1_000));
        }

        if (IsNoChecksResponse(output, error)) return new CheckParseResult([], true, null);

        var detail = !string.IsNullOrWhiteSpace(parseError) ? parseError : ExecResultDiagnostics.Failure(result);
        return new CheckParseResult([], false, "pull-request checks inspection incomplete: " + Limit(detail, 1_000));
    }

    private static bool TryParseCheckArray(string output, out List<CheckState> checks, out string? error)
    {
        checks = [];
        error = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            error = "gh pr checks returned no JSON output";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "gh pr checks JSON root was not an array";
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGetRequiredString(item, "name", out var name)
                    || !TryGetRequiredString(item, "state", out var state))
                {
                    checks = [];
                    error = "gh pr checks JSON contained a malformed check entry";
                    return false;
                }

                checks.Add(new CheckState(
                    name,
                    state,
                    TryGetOptionalString(item, "bucket"),
                    TryGetOptionalString(item, "link")));
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = "gh pr checks returned malformed JSON: " + exception.Message;
            return false;
        }
    }

    private static bool IsNoChecksResponse(string output, string error)
    {
        // `gh pr checks --json ...` normally emits [] for this case. Some gh
        // versions instead print a human-readable no-checks notice and may
        // use a non-zero status. Accept only that explicit notice; blank,
        // auth, and network failures remain incomplete.
        if (output == "[]" && !ContainsFailureSignal(error)) return true;
        if (ContainsFailureSignal(error) || ContainsFailureSignal(output)) return false;
        var text = string.IsNullOrWhiteSpace(output) ? error : output;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("no checks", StringComparison.Ordinal)
            && (normalized.Contains("reported", StringComparison.Ordinal)
                || normalized.Contains("found", StringComparison.Ordinal)
                || normalized.Contains("available", StringComparison.Ordinal)
                || normalized.Contains("configured", StringComparison.Ordinal));
    }

    private static bool ContainsFailureSignal(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("authentication", StringComparison.Ordinal)
            || normalized.Contains("bad credentials", StringComparison.Ordinal)
            || normalized.Contains("http 401", StringComparison.Ordinal)
            || normalized.Contains("http 403", StringComparison.Ordinal)
            || normalized.Contains("not logged in", StringComparison.Ordinal)
            || normalized.Contains("login required", StringComparison.Ordinal)
            || normalized.Contains("unauthorized", StringComparison.Ordinal)
            || normalized.Contains("forbidden", StringComparison.Ordinal)
            || normalized.Contains("not found", StringComparison.Ordinal)
            || normalized.Contains("network", StringComparison.Ordinal)
            || normalized.Contains("timed out", StringComparison.Ordinal)
            || normalized.Contains("timeout", StringComparison.Ordinal)
            || normalized.Contains("connection", StringComparison.Ordinal)
            || normalized.Contains("unable to access", StringComparison.Ordinal)
            || normalized.Contains("failed to connect", StringComparison.Ordinal)
            || normalized.Contains("could not resolve", StringComparison.Ordinal)
            || normalized.Contains("api request failed", StringComparison.Ordinal)
            || normalized.Contains("error", StringComparison.Ordinal);
    }

    private static bool TryParsePullRequestView(string output, out bool merged, out string state, out string mergeable, out string mergeStateStatus, out string headOid, out string baseRefName, out string? error)
    {
        merged = false;
        state = "UNKNOWN";
        mergeable = "UNKNOWN";
        mergeStateStatus = "UNKNOWN";
        headOid = string.Empty;
        baseRefName = string.Empty;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "gh pr view JSON root was not an object";
                return false;
            }

            var root = document.RootElement;
            merged = root.TryGetProperty("mergedAt", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mergedAt.GetString());
            state = TryGetOptionalString(root, "state");
            if (string.IsNullOrWhiteSpace(state)) state = merged ? "CLOSED" : "OPEN";
            mergeable = TryGetOptionalString(root, "mergeable");
            mergeStateStatus = TryGetOptionalString(root, "mergeStateStatus");
            headOid = TryGetOptionalString(root, "headRefOid");
            baseRefName = TryGetOptionalString(root, "baseRefName");
            if (state == "UNKNOWN" || string.IsNullOrWhiteSpace(mergeable) || string.IsNullOrWhiteSpace(mergeStateStatus))
            {
                error = "gh pr view JSON omitted required pull-request state or mergeability fields";
                return false;
            }
            return true;
        }
        catch (JsonException exception)
        {
            error = "gh pr view returned malformed JSON: " + exception.Message;
            return false;
        }
    }

    private static bool TryGetRequiredString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string TryGetOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    }

    private static PullRequestSnapshot IncompleteSnapshot(ExecResult result, string prefix)
    {
        var detail = ExecResultDiagnostics.Failure(result);
        return IncompleteSnapshot(prefix + ": " + Limit(detail, 1_000));
    }

    private static PullRequestSnapshot IncompleteSnapshot(string error) =>
        new(false, [], [], "UNKNOWN", "UNKNOWN", "", "", "UNKNOWN", false, error);

    private static string AppendError(string? current, string addition) => string.IsNullOrWhiteSpace(current) ? addition : current + " | " + addition;
    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private async Task<List<CheckState>> AddFailureLogsAsync(List<CheckState> checks, PullRequestId pullRequest, WorkerConfig settings, CancellationToken cancellationToken)
    {
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index];
            if (!check.IsCodeRepair || !TryGetActionsRunId(check.Link, out var runId)) continue;
            var logs = await processes.RunAsync(settings.GhExe, ["run", "view", runId, "--log-failed", "--repo", $"{pullRequest.Owner}/{pullRequest.Repository}"], settings.RepoRoot, cancellationToken);
            var text = logs.ExitCode == 0 ? logs.Output : logs.Error;
            if (text.Length > 16_000) text = text[^16_000..];
            checks[index] = check with { Details = text };
        }
        return checks;
    }

    private static bool TryGetActionsRunId(string link, out string runId)
    {
        return TryParseActionsRun(link, out _, out _, out runId);
    }

    private static bool TryParseActionsRun(string link, out string owner, out string repository, out string runId)
    {
        owner = string.Empty;
        repository = string.Empty;
        runId = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5
            || !parts[2].Equals("actions", StringComparison.OrdinalIgnoreCase)
            || !parts[3].Equals("runs", StringComparison.OrdinalIgnoreCase)
            || !parts[0].All(IsValidPathCharacter)
            || !parts[1].All(IsValidPathCharacter)) return false;
        runId = parts[4];
        if (string.IsNullOrWhiteSpace(runId) || !runId.All(char.IsDigit)) return false;
        owner = parts[0];
        repository = parts[1];
        return true;
    }

    private static bool IsValidPathCharacter(char value) => char.IsLetterOrDigit(value) || value is '-' or '_' or '.';

    private sealed record CheckParseResult(List<CheckState> Checks, bool Complete, string? Error);

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
