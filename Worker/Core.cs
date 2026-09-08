using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MaddoxTasks.Worker;

public interface IClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record WorkerConfig(
    int SchemaVersion,
    TimeSpan ClaimInterval,
    int MaxConcurrentCodexProcesses,
    TimeSpan PrPollInterval,
    TimeSpan ClarificationTimeout,
    string PromptFile,
    string Model,
    string ReasoningEffort,
    int RepairMaxAttempts,
    TimeSpan RepairMaxElapsed,
    TimeSpan ReviewQuietPeriod,
    string[] IgnoredChecks,
    string[] AutoMergeRepositories,
    string AutoMergeMethod,
    string MaddoxExe,
    string CodexExe,
    string GhExe,
    string RepoRoot,
    string WorktreeRoot,
    TimeSpan? BlockedDisplayDuration = null,
    TimeSpan? ResearchCooldown = null,
    string? PrivateRepositoryOwner = null,
    int? WorkerRetryMaxAttempts = null,
    TimeSpan? WorkerRetryMaxElapsed = null,
    TimeSpan? WorkerRetryBaseDelay = null,
    TimeSpan? ResearchFailureCooldown = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public TimeSpan EffectiveBlockedDisplayDuration => BlockedDisplayDuration ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveResearchCooldown => ResearchCooldown ?? TimeSpan.FromDays(1);
    // Worker retries have their own bounds.  They intentionally do not reuse
    // the pull-request repair budget: a transient worker failure is a
    // different failure domain from a repeatedly failing review check.
    public int EffectiveWorkerRetryMaxAttempts => WorkerRetryMaxAttempts ?? 3;
    public TimeSpan EffectiveWorkerRetryMaxElapsed => WorkerRetryMaxElapsed ?? TimeSpan.FromHours(2);
    public TimeSpan EffectiveWorkerRetryBaseDelay => WorkerRetryBaseDelay ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveResearchFailureCooldown => ResearchFailureCooldown ?? TimeSpan.FromHours(1);

    public static WorkerConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Worker configuration is empty.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Unsupported schemaVersion {SchemaVersion}.");
        if (ClaimInterval <= TimeSpan.Zero) throw new InvalidDataException("claimInterval must be positive.");
        if (PrPollInterval <= TimeSpan.Zero) throw new InvalidDataException("prPollInterval must be positive.");
        if (ClarificationTimeout <= TimeSpan.Zero) throw new InvalidDataException("clarificationTimeout must be positive.");
        if (MaxConcurrentCodexProcesses < 0) throw new InvalidDataException("maxConcurrentCodexProcesses cannot be negative.");
        if (RepairMaxAttempts < 1 || RepairMaxElapsed <= TimeSpan.Zero) throw new InvalidDataException("Repair bounds must be positive.");
        if (ReviewQuietPeriod <= TimeSpan.Zero) throw new InvalidDataException("reviewQuietPeriod must be positive.");
        if (BlockedDisplayDuration is { } blockedDisplayDuration && blockedDisplayDuration <= TimeSpan.Zero) throw new InvalidDataException("blockedDisplayDuration must be positive.");
        if (ResearchCooldown is { } researchCooldown && researchCooldown <= TimeSpan.Zero) throw new InvalidDataException("researchCooldown must be positive.");
        if (WorkerRetryMaxAttempts is { } workerRetryMaxAttempts && workerRetryMaxAttempts < 1) throw new InvalidDataException("workerRetryMaxAttempts must be positive.");
        if (WorkerRetryMaxElapsed is { } workerRetryMaxElapsed && workerRetryMaxElapsed <= TimeSpan.Zero) throw new InvalidDataException("workerRetryMaxElapsed must be positive.");
        if (WorkerRetryBaseDelay is { } workerRetryBaseDelay && workerRetryBaseDelay <= TimeSpan.Zero) throw new InvalidDataException("workerRetryBaseDelay must be positive.");
        if (ResearchFailureCooldown is { } researchFailureCooldown && researchFailureCooldown <= TimeSpan.Zero) throw new InvalidDataException("researchFailureCooldown must be positive.");
        if (!string.Equals(AutoMergeMethod, "squash", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only squash auto-merge is supported.");
        foreach (var value in new[] { PromptFile, Model, ReasoningEffort, MaddoxExe, CodexExe, GhExe, RepoRoot, WorktreeRoot })
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Required configuration values cannot be blank.");
        if (!Directory.Exists(RepoRoot)) throw new InvalidDataException($"repoRoot does not exist: {RepoRoot}");
    }
}

public sealed class ConfigState
{
    private readonly object gate = new();
    private WorkerConfig current;
    public ConfigState(WorkerConfig initial) => current = initial;
    public WorkerConfig Current { get { lock (gate) return current; } }
    public bool TryReload(string path, out string? error)
    {
        try { var replacement = WorkerConfig.Load(path); lock (gate) current = replacement; error = null; return true; }
        catch (Exception exception) { error = exception.Message; return false; }
    }
}

public sealed record TaskCommentDto(DateTime Timestamp, string Comment, string Actor);
public sealed record TaskDto(int Sequence, string IssueId, string Title, string Description, string[] Repositories)
{
    public TaskCommentDto[] Comments { get; init; } = [];
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// A deliberately closed set of task-ledger mutations that the blocked-task
/// researcher may return. It has no command, process, filesystem, Git, or
/// GitHub escape hatch.
/// </summary>
public sealed record ResearchMutation(
    string Type,
    string? IssueId = null,
    string? Title = null,
    string? Description = null,
    int? NewPriority = null,
    string? Label = null,
    string? NewStatus = null,
    string[]? Repositories = null,
    string? ParentId = null,
    int? Priority = null,
    string? Status = null,
    string? Comment = null);

public sealed record ResearchPlan(
    string Outcome,
    string Summary,
    string[] Findings,
    ResearchMutation[] Mutations);

public static class ResearchPlanPolicy
{
    public const string Actor = "maddox-research-worker";
    public const string Completed = "completed";
    public const string Unblocked = "unblocked";
    public const string StillBlocked = "stillBlocked";

    private static readonly HashSet<string> ExistingMutationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddComment",
        "UpdateDescription",
        "ChangePriority",
        "AddLabel",
        "RemoveLabel",
        "SetRepositoryLabels",
        "ChangeStatus"
    };

    public static ResearchPlan Parse(string json, TaskDto sourceTask)
    {
        if (sourceTask is null) throw new ArgumentNullException(nameof(sourceTask));
        return Parse(json, sourceTask.IssueId, sourceTask.Sequence);
    }

    public static ResearchPlan Parse(string json, string sourceIssueId, int? sourceSequence = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Research result must be a JSON object.");

        var outcome = RequiredString(root, "outcome");
        if (outcome.Equals(Completed, StringComparison.OrdinalIgnoreCase)) outcome = Completed;
        else if (outcome.Equals(Unblocked, StringComparison.OrdinalIgnoreCase)) outcome = Unblocked;
        else if (outcome.Equals(StillBlocked, StringComparison.OrdinalIgnoreCase)) outcome = StillBlocked;
        else throw new InvalidDataException("Research outcome must be 'completed', 'unblocked', or 'stillBlocked'.");

        var summary = RequiredString(root, "summary");
        var findings = RequiredStringArray(root, "findings", allowEmpty: true);
        var mutationsElement = RequiredProperty(root, "mutations");
        if (mutationsElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Research mutations must be an array.");
        if (mutationsElement.GetArrayLength() > 100) throw new InvalidDataException("Research result contains too many mutations.");

        var mutations = mutationsElement.EnumerateArray()
            .Select(element => ParseMutation(element, sourceIssueId, sourceSequence))
            .ToArray();

        return new ResearchPlan(outcome, summary, findings, mutations);
    }

    public static bool IsSourceStatusMutation(ResearchMutation mutation, string sourceIssueId, int? sourceSequence = null)
        => mutation.Type.Equals("ChangeStatus", StringComparison.OrdinalIgnoreCase)
            && IsSourceIssueToken(mutation.IssueId, sourceIssueId, sourceSequence);

    public static string FindingsComment(ResearchPlan plan)
    {
        var lines = new List<string> { "Research findings: " + plan.Summary };
        lines.AddRange(plan.Findings.Select(finding => "- " + finding));
        return string.Join(Environment.NewLine, lines);
    }

    private static ResearchMutation ParseMutation(JsonElement element, string sourceIssueId, int? sourceSequence)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each research mutation must be an object.");
        var type = RequiredString(element, "type");
        var canonicalType = ExistingMutationTypes.FirstOrDefault(value => value.Equals(type, StringComparison.OrdinalIgnoreCase)) ??
            (type.Equals("CreateIssue", StringComparison.OrdinalIgnoreCase) ? "CreateIssue" : null);
        if (canonicalType is null) throw new InvalidDataException($"Research mutation type '{type}' is not allowed.");

        if (canonicalType == "CreateIssue")
        {
            var title = RequiredString(element, "title");
            var description = RequiredString(element, "description", allowEmpty: true);
            var priority = OptionalInt(element, "priority") ?? 3;
            if (priority is < 1 or > 5) throw new InvalidDataException("CreateIssue priority must be between 1 and 5.");
            var status = OptionalString(element, "status") ?? "Next";
            if (!status.Equals("Next", StringComparison.OrdinalIgnoreCase) && !status.Equals("Backlog", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CreateIssue status must be 'Next' or 'Backlog'.");
            var parentId = OptionalString(element, "parentId");
            if (parentId is not null && !Guid.TryParse(parentId, out _)) throw new InvalidDataException("CreateIssue parentId must be a valid issue id GUID.");
            var repositories = OptionalStringArray(element, "repositories", allowEmpty: true);
            ValidateRepositories(repositories);
            return new ResearchMutation(canonicalType, Title: title, Description: description, Priority: priority, Status: status, ParentId: parentId, Repositories: repositories);
        }

        var issueId = RequiredString(element, "issueId");
        if (canonicalType == "AddComment")
            return new ResearchMutation(canonicalType, IssueId: issueId, Comment: RequiredString(element, "comment"));
        if (canonicalType == "UpdateDescription")
            return new ResearchMutation(canonicalType, IssueId: issueId, Description: RequiredString(element, "description", allowEmpty: true));
        if (canonicalType == "ChangePriority")
        {
            var priority = RequiredInt(element, "newPriority");
            if (priority is < 1 or > 5) throw new InvalidDataException("newPriority must be between 1 and 5.");
            return new ResearchMutation(canonicalType, IssueId: issueId, NewPriority: priority);
        }
        if (canonicalType is "AddLabel" or "RemoveLabel")
            return new ResearchMutation(canonicalType, IssueId: issueId, Label: RequiredString(element, "label"));
        if (canonicalType == "SetRepositoryLabels")
        {
            var repositories = RequiredStringArray(element, "repositories", allowEmpty: false);
            ValidateRepositories(repositories);
            return new ResearchMutation(canonicalType, IssueId: issueId, Repositories: repositories);
        }

        var newStatus = RequiredString(element, "newStatus");
        if (!Enum.TryParse<ResearchStatus>(newStatus, true, out var parsedStatus))
            throw new InvalidDataException($"Invalid status '{newStatus}' in research mutation.");
        if (IsSourceStatusMutation(new ResearchMutation(canonicalType, IssueId: issueId), sourceIssueId, sourceSequence))
            throw new InvalidDataException("Research mutations may not directly change the source task status.");
        return new ResearchMutation(canonicalType, IssueId: issueId, NewStatus: parsedStatus.ToString());
    }

    private static void ValidateRepositories(string[]? repositories)
    {
        if (repositories is null) return;
        if (repositories.Any(repository => string.IsNullOrWhiteSpace(repository) || repository.Trim().StartsWith("repo:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Repositories must be non-empty names without the 'repo:' prefix.");
        if (repositories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != repositories.Length)
            throw new InvalidDataException("Repositories must not contain duplicates.");
    }

    private static string RequiredString(JsonElement root, string name, bool allowEmpty = false)
    {
        var value = OptionalString(root, name);
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException($"Research result requires a non-empty '{name}' string.");
        return value.Trim();
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        var property = RequiredProperty(root, name, required: false);
        if (property.ValueKind == JsonValueKind.Undefined || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Research field '{name}' must be a string.");
        return property.GetString();
    }

    private static JsonElement RequiredProperty(JsonElement root, string name, bool required = true)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        if (required) throw new InvalidDataException($"Research result is missing '{name}'.");
        return default;
    }

    private static int RequiredInt(JsonElement root, string name)
    {
        var property = RequiredProperty(root, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value)) throw new InvalidDataException($"Research field '{name}' must be an integer.");
        return value;
    }

    private static int? OptionalInt(JsonElement root, string name)
    {
        var property = RequiredProperty(root, name, required: false);
        if (property.ValueKind == JsonValueKind.Undefined || property.ValueKind == JsonValueKind.Null) return null;
        return RequiredInt(root, name);
    }

    private static string[] RequiredStringArray(JsonElement root, string name, bool allowEmpty)
    {
        var property = RequiredProperty(root, name);
        return ParseStringArray(property, name, allowEmpty);
    }

    private static string[]? OptionalStringArray(JsonElement root, string name, bool allowEmpty)
    {
        var property = RequiredProperty(root, name, required: false);
        return property.ValueKind == JsonValueKind.Undefined || property.ValueKind == JsonValueKind.Null
            ? null
            : ParseStringArray(property, name, allowEmpty);
    }

    private static string[] ParseStringArray(JsonElement property, string name, bool allowEmpty)
    {
        if (property.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Research field '{name}' must be an array of strings.");
        var values = property.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new InvalidDataException($"Research field '{name}' must contain non-empty strings.");
            return item.GetString()!.Trim();
        }).ToArray();
        if (!allowEmpty && values.Length == 0) throw new InvalidDataException($"Research field '{name}' must not be empty.");
        return values;
    }

    private static bool IsSourceIssueToken(string? token, string sourceIssueId, int? sourceSequence)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var normalized = token.Trim();
        if (string.Equals(normalized, sourceIssueId, StringComparison.OrdinalIgnoreCase)) return true;

        // Agent issue tokens also accept an unambiguous GUID prefix. A
        // researcher must not use that shorthand to bypass the source-task
        // status guard, in either hyphenated (D) or compact (N) form.
        if (Guid.TryParse(sourceIssueId, out var sourceGuid) &&
            (sourceGuid.ToString("D").StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
             sourceGuid.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalized.StartsWith("#", StringComparison.Ordinal)) normalized = normalized[1..];
        return sourceSequence.HasValue && int.TryParse(normalized, out var sequence) && sequence == sourceSequence.Value;
    }

    private enum ResearchStatus
    {
        Backlog,
        Next,
        Active,
        Blocked,
        ReadyForReview,
        Done,
        Rejected
    }
}

public sealed record PendingTaskUpdateBatch(string? Description, TaskCommentDto[] Comments);
public sealed record ClarificationChild(string Title, string Description, string Repository, string Rationale);
public sealed record ClarificationDecision(string Action, string[] Repositories, ClarificationChild[] Children, string Rationale, double Confidence, bool Ambiguous);

public static class ClarificationPolicy
{
    public static ClarificationDecision Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var action = root.GetProperty("action").GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        var repositories = root.GetProperty("repositories").EnumerateArray().Select(item => item.GetString()?.Trim() ?? string.Empty).ToArray();
        var children = root.GetProperty("children").EnumerateArray().Select(item => new ClarificationChild(
            item.GetProperty("title").GetString()?.Trim() ?? string.Empty,
            item.GetProperty("description").GetString()?.Trim() ?? string.Empty,
            item.GetProperty("repository").GetString()?.Trim() ?? string.Empty,
            item.GetProperty("rationale").GetString()?.Trim() ?? string.Empty)).ToArray();
        var decision = new ClarificationDecision(action, repositories, children,
            root.GetProperty("rationale").GetString()?.Trim() ?? string.Empty,
            root.GetProperty("confidence").GetDouble(), root.GetProperty("ambiguous").GetBoolean());
        Validate(decision);
        return decision;
    }

    private static void Validate(ClarificationDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Rationale)) throw new InvalidDataException("Clarification requires a rationale.");
        if (double.IsNaN(decision.Confidence) || decision.Confidence is < 0 or > 1) throw new InvalidDataException("Clarification confidence must be between 0 and 1.");
        if (decision.Ambiguous) throw new InvalidDataException("Codex reported material repository ambiguity: " + decision.Rationale);
        if (decision.Action == "assign")
        {
            if (decision.Repositories.Length == 0 || decision.Repositories.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Repository assignment requires at least one repository.");
            if (decision.Repositories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != decision.Repositories.Length)
                throw new InvalidDataException("Repository assignment must not contain duplicate repositories.");
            if (decision.Children.Length != 0) throw new InvalidDataException("Repository assignment must not contain split children.");
            return;
        }
        if (decision.Action != "split") throw new InvalidDataException("Clarification action must be 'assign' or 'split'.");
        if (decision.Repositories.Length != 0) throw new InvalidDataException("A split must describe repositories through its child tasks only.");
        if (decision.Children.Length < 2) throw new InvalidDataException("A split requires at least two child tasks.");
        if (decision.Children.Any(child => string.IsNullOrWhiteSpace(child.Title) || string.IsNullOrWhiteSpace(child.Description) || string.IsNullOrWhiteSpace(child.Repository) || string.IsNullOrWhiteSpace(child.Rationale)))
            throw new InvalidDataException("Every split child requires a title, description, rationale, and exactly one repository.");
        var unique = decision.Children.Select(child => child.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (unique != decision.Children.Length) throw new InvalidDataException("Each split child must own a unique repository.");
    }
}

/// <summary>
/// The only blocker values a worker result may use.  Keeping this list closed
/// is important: a free-form blocker must never silently become a retry or a
/// ReadyForReview transition.
/// </summary>
public static class WorkerBlockerKinds
{
    public const string None = "none";
    public const string TransientWorker = "transientWorker";
    public const string WorkerRepairable = "workerRepairable";
    public const string UpstreamDependency = "upstreamDependency";
    public const string MissingCredential = "missingCredential";
    public const string MissingHardware = "missingHardware";
    public const string MissingInput = "missingInput";
    public const string UserDecision = "userDecision";
    public const string PolicyRestriction = "policyRestriction";
    public const string HumanReview = "humanReview";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        None,
        TransientWorker,
        WorkerRepairable,
        UpstreamDependency,
        MissingCredential,
        MissingHardware,
        MissingInput,
        UserDecision,
        PolicyRestriction,
        HumanReview
    };

    public static bool IsRetryable(string kind) => kind is TransientWorker or WorkerRepairable;
}

/// <summary>
/// Extracts an absolute future retry/reset time from a worker diagnostic.
/// Codex and service diagnostics are not consistent about formatting, so the
/// parser accepts the common human form (for example, "try again at Sep 10th,
/// 2026 3:14 PM") as well as ISO-8601 timestamps.  Timestamp-less diagnostics
/// remain valid and use the normal bounded backoff.
/// </summary>
public static class RetryTimestampPolicy
{
    private static readonly Regex Marker = new(
        @"(?ix)\b(?:try\s+again|retry|reset(?:s)?|available)\b(?:\s+(?:again|after|at|on|around|from|until|is|will|be)){0,5}\s+(?<value>[A-Za-z0-9][^.;\r\n]{0,96})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ordinal = new(@"(?<=\d)(?:st|nd|rd|th)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Formats =
    [
        "MMM d, yyyy h:mm tt",
        "MMMM d, yyyy h:mm tt",
        "MMM d yyyy h:mm tt",
        "MMMM d yyyy h:mm tt",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK"
    ];

    public static DateTime? ParseFuture(string? detail, DateTime nowUtc)
        => TryParseFuture(detail, nowUtc, out var parsed) ? parsed : null;

    public static DateTime? ParseFutureTimestamp(string? detail, DateTime nowUtc)
        => ParseFuture(detail, nowUtc);

    public static bool TryParseFuture(string? detail, DateTime nowUtc, out DateTime retryAtUtc)
    {
        retryAtUtc = default;
        if (string.IsNullOrWhiteSpace(detail)) return false;

        var now = NormalizeUtc(nowUtc);
        foreach (var candidate in Candidates(detail))
        {
            if (!TryParseCandidate(candidate, out var parsed) || parsed <= now) continue;
            retryAtUtc = parsed;
            return true;
        }
        return false;
    }

    private static IEnumerable<string> Candidates(string detail)
    {
        yield return detail.Trim();
        foreach (Match match in Marker.Matches(detail))
        {
            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;
            yield return value;

            // A sentence often appends an explanation after the timestamp.
            // Try progressively shorter prefixes so that punctuation-free
            // human diagnostics still parse deterministically.
            var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var length = words.Length - 1; length >= 2; length--)
                yield return string.Join(' ', words.Take(length));
        }
    }

    private static bool TryParseCandidate(string candidate, out DateTime utc)
    {
        candidate = Ordinal.Replace(candidate.Trim().TrimEnd('.', ',', ';'), string.Empty);
        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var offset))
        {
            utc = offset.UtcDateTime;
            return true;
        }

        if (DateTime.TryParseExact(candidate, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            utc = NormalizeUtc(exact);
            return true;
        }

        if (DateTime.TryParse(candidate, CultureInfo.GetCultureInfo("en-US"),
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var general))
        {
            utc = NormalizeUtc(general);
            return true;
        }

        utc = default;
        return false;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed record WorkerBlocker(string Kind, string Summary, string[] Evidence, DateTime? RetryAtUtc = null)
{
    public bool IsRetryable => WorkerBlockerKinds.IsRetryable(Kind);
    public bool IsHumanReview => Kind == WorkerBlockerKinds.HumanReview;
    public bool RequiresFreshSession => WorkerRetryPolicy.RequiresFreshSession(Kind, Summary);
}

public sealed record WorkerResult(
    string Status,
    string Summary,
    bool WorkComplete,
    WorkerBlocker Blocker,
    bool IsLegacy = false);

/// <summary>
/// Parses the lifecycle part of a Codex result independently from the
/// repository publication manifest.  New Codex invocations use strict mode;
/// persisted journals may opt into the narrow legacy defaults below so an old
/// worker journal can still be recovered safely.
/// </summary>
public static class WorkerResultPolicy
{
    private static readonly string[] Statuses = ["completed", "noChanges", "blocked"];

    public static WorkerResult Parse(string json, bool allowLegacy = false)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement, allowLegacy);
    }

    public static WorkerResult Parse(JsonElement root, bool allowLegacy = false)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Worker result must be a JSON object.");

        var statusValue = OptionalString(root, "status");
        if (string.IsNullOrWhiteSpace(statusValue)) throw new InvalidDataException("Worker result requires a non-empty 'status'.");
        var status = Statuses.FirstOrDefault(value => value.Equals(statusValue.Trim(), StringComparison.OrdinalIgnoreCase));
        if (status is null) throw new InvalidDataException("Worker result status must be 'completed', 'noChanges', or 'blocked'.");

        var summary = OptionalString(root, "summary");
        var hasSummary = !string.IsNullOrWhiteSpace(summary);
        if (!hasSummary && !allowLegacy) throw new InvalidDataException("Worker result requires a non-empty 'summary'.");
        summary = hasSummary ? summary!.Trim() : "Legacy worker result";

        var workCompleteProperty = FindProperty(root, "workComplete");
        if (workCompleteProperty is { } workValue && workValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) workCompleteProperty = null;
        var blockerProperty = FindProperty(root, "blocker");
        if (blockerProperty is { } blockerValue && blockerValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) blockerProperty = null;
        var isLegacy = workCompleteProperty is null || blockerProperty is null || !hasSummary;
        if (isLegacy && !allowLegacy)
            throw new InvalidDataException("New worker results require workComplete and blocker.");

        var workComplete = workCompleteProperty is { } workElement
            ? ReadBoolean(workElement, "workComplete")
            : status != "blocked";
        var blocker = blockerProperty is { } blockerElement
            ? ParseBlocker(blockerElement, summary!, allowLegacy)
            : LegacyBlocker(status, summary!);

        ValidateCombination(status, workComplete, blocker);
        return new WorkerResult(status, summary!, workComplete, blocker, isLegacy);
    }

    public static bool TryParse(string json, out WorkerResult? result, out string? error, bool allowLegacy = false)
    {
        try
        {
            result = Parse(json, allowLegacy);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            result = null;
            error = exception.Message;
            return false;
        }
    }

    public static void ValidateCombination(string status, bool workComplete, WorkerBlocker blocker)
    {
        if (!WorkerBlockerKinds.All.Contains(blocker.Kind))
            throw new InvalidDataException($"Worker blocker kind '{blocker.Kind}' is not allowed.");
        if (string.IsNullOrWhiteSpace(blocker.Summary)) throw new InvalidDataException("Worker blocker requires a non-empty summary.");
        if (blocker.Evidence is null || blocker.Evidence.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Worker blocker evidence must contain only non-empty strings.");
        if (blocker.Kind != WorkerBlockerKinds.None && blocker.Evidence.Length == 0)
            throw new InvalidDataException("Non-none worker blockers require at least one evidence item.");

        if (blocker.Kind == WorkerBlockerKinds.HumanReview && !workComplete)
            throw new InvalidDataException("humanReview requires workComplete=true.");

        if (status is "completed" or "noChanges")
        {
            if (!workComplete) throw new InvalidDataException($"{status} requires workComplete=true.");
            if (blocker.Kind is not (WorkerBlockerKinds.None or WorkerBlockerKinds.HumanReview))
                throw new InvalidDataException($"{status} may use only blocker none or humanReview.");
            return;
        }

        if (blocker.Kind == WorkerBlockerKinds.None)
            throw new InvalidDataException("blocked requires a non-none blocker.");
        if (blocker.Kind == WorkerBlockerKinds.HumanReview)
            throw new InvalidDataException("humanReview is valid only for completed or noChanges results.");
    }

    private static WorkerBlocker ParseBlocker(JsonElement property, string resultSummary, bool allowLegacy)
    {
        if (property.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Worker result blocker must be an object.");
        var kindValue = OptionalString(property, "kind");
        var summary = OptionalString(property, "summary");
        var evidenceProperty = FindProperty(property, "evidence");
        var complete = !string.IsNullOrWhiteSpace(kindValue) && !string.IsNullOrWhiteSpace(summary)
            && evidenceProperty is { } evidenceElement && evidenceElement.ValueKind == JsonValueKind.Array;
        if (!complete && !allowLegacy)
            throw new InvalidDataException("Worker result blocker requires kind, summary, and evidence.");
        if (!complete)
        {
            // A malformed legacy blocked result is deliberately treated as an
            // external blocker.  It cannot accidentally receive a retry.
            return LegacyBlocker("blocked", resultSummary);
        }

        var kind = WorkerBlockerKinds.All.FirstOrDefault(value => value.Equals(kindValue!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (kind is null) throw new InvalidDataException($"Worker blocker kind '{kindValue}' is not allowed.");
        if (evidenceProperty!.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Worker blocker evidence must be an array of strings.");
        var evidence = evidenceProperty.Value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException("Worker blocker evidence must contain only non-empty strings.");
            return item.GetString()!.Trim();
        }).ToArray();
        var retryAtUtc = ParseRetryAt(property, summary!);
        return new WorkerBlocker(kind, summary!.Trim(), evidence, retryAtUtc);
    }

    private static WorkerBlocker LegacyBlocker(string status, string summary) => status == "blocked"
        ? new WorkerBlocker(WorkerBlockerKinds.UpstreamDependency, summary, [summary])
        : new WorkerBlocker(WorkerBlockerKinds.None, summary, []);

    private static DateTime? ParseRetryAt(JsonElement blocker, string summary)
    {
        foreach (var name in new[] { "retryAtUtc", "retryAfterUtc", "retryAt", "resetAtUtc", "resetAt" })
        {
            var value = FindProperty(blocker, name);
            if (value is null) continue;
            if (value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Worker blocker field '{name}' must be a timestamp string or null.");
            var explicitValue = value.Value.GetString();
            if (RetryTimestampPolicy.TryParseFuture(explicitValue, DateTime.UtcNow, out var explicitRetryAt)) return explicitRetryAt;
        }

        return RetryTimestampPolicy.ParseFuture(summary, DateTime.UtcNow);
    }

    private static bool ReadBoolean(JsonElement property, string name)
    {
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False) return property.GetBoolean();
        throw new InvalidDataException($"Worker field '{name}' must be a boolean.");
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        var property = FindProperty(root, name);
        if (property is null || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Worker field '{name}' must be a string.");
        return property.Value.GetString();
    }

    private static JsonElement? FindProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
}

public sealed record WorkerFailureClassification(string Kind, string Summary, string Fingerprint, DateTime? RetryAtUtc = null)
{
    public bool Retryable => WorkerBlockerKinds.IsRetryable(Kind);
    public bool RequiresFreshSession => WorkerRetryPolicy.RequiresFreshSession(Kind, Summary);
}

/// <summary>
/// Classifies worker-owned failures by stable categories.  Explicit credential,
/// hardware, and human-decision signals are intentionally checked before broad
/// transient patterns so they cannot be retried forever from free-form text.
/// </summary>
public static class WorkerFailurePolicy
{
    public static WorkerFailureClassification Classify(Exception exception) => Classify(exception, DateTime.UtcNow);

    public static WorkerFailureClassification Classify(Exception exception, DateTime nowUtc)
    {
        var text = Flatten(exception);
        var normalized = text.ToLowerInvariant();
        var kind = ClassifyKind(exception, normalized);
        var summary = Limit(text, 1_500);
        var fingerprint = WorkerRetryPolicy.Fingerprint(kind, text);
        var retryAtUtc = RetryTimestampPolicy.ParseFuture(text, nowUtc);
        return new WorkerFailureClassification(kind, summary, fingerprint, retryAtUtc);
    }

    private static string ClassifyKind(Exception exception, string text)
    {
        if (ContainsAny(text, "credential", "authentication failed", "authentication token", "not logged in", "login required", "github token", "gh auth", "unauthorized", "http 401", "permission denied (publickey)"))
            return WorkerBlockerKinds.MissingCredential;
        if (ContainsAny(text, "gpu", "cuda", "device not found", "no hardware", "hardware unavailable", "runner.os", "virtualization"))
            return WorkerBlockerKinds.MissingHardware;
        if (ContainsAny(text, "user decision", "waiting for user", "manual approval", "human approval", "needs review", "clarification required"))
            return WorkerBlockerKinds.UserDecision;
        if (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
            return WorkerBlockerKinds.TransientWorker;
        if (ContainsAny(text, "rate limit", "rate-limit", "too many requests", "429", "quota", "usage limit", "overloaded", "temporarily unavailable", "service unavailable", "try again later", "timed out", "timeout", "connection reset", "connection refused", "socket", "network", "dns", "could not resolve", "github api", "api request failed", "git fetch", "git push", "process failed to start", "failed to start", "failed to launch", "could not start", "cannot start", "executable not found", "no such file or directory", "system cannot find", "process start", "tool failed", "502", "503", "504"))
            return WorkerBlockerKinds.TransientWorker;
        if (ContainsAny(text, "merge conflict", "conflicting", "worktree is dirty", "workspace", "structured result", "codex result", "repository manifest", "result change flag"))
            return WorkerBlockerKinds.WorkerRepairable;
        if (exception is IOException or InvalidDataException)
            return WorkerBlockerKinds.WorkerRepairable;
        // Unknown process failures are worker-owned until bounded retries prove
        // otherwise. External gates must be reported explicitly by the
        // structured result instead of being inferred from an opaque exception.
        return WorkerBlockerKinds.WorkerRepairable;
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);

    private static string Flatten(Exception exception)
    {
        var values = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            if (!string.IsNullOrWhiteSpace(current.Message)) values.Add(current.Message.Trim());
        return string.Join(" | ", values);
    }

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength] + "...";
}

public static class CodexClientFailurePolicy
{
    public static bool IsWorkerWide(string diagnostic)
    {
        var normalized = diagnostic.ToLowerInvariant();
        return normalized.Contains("requires a newer version of codex", StringComparison.Ordinal)
            || normalized.Contains("failed to load models cache", StringComparison.Ordinal)
            || normalized.Contains("failed to install system skills", StringComparison.Ordinal);
    }
}

public static class WorkerRetryPolicy
{
    public static TimeSpan MaxRetryDelay { get; } = TimeSpan.FromHours(1);

    public static string Fingerprint(string kind, string detail)
    {
        var normalized = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + normalized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Internal-thread/session failures cannot be repaired by resuming the
    /// same Codex conversation.  The next attempt should retain the worktree
    /// and repair context but start a fresh session.
    /// </summary>
    public static bool RequiresFreshSession(string kind, string? detail = null)
    {
        if (kind.Equals(WorkerBlockerKinds.WorkerRepairable, StringComparison.OrdinalIgnoreCase)) return true;
        if (!kind.Equals(WorkerBlockerKinds.TransientWorker, StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = detail?.ToLowerInvariant() ?? string.Empty;
        return normalized.Contains("internal thread", StringComparison.Ordinal)
            || normalized.Contains("thread not found", StringComparison.Ordinal)
            || normalized.Contains("thread unavailable", StringComparison.Ordinal)
            || normalized.Contains("unknown thread", StringComparison.Ordinal)
            || normalized.Contains("invalid thread", StringComparison.Ordinal)
            || normalized.Contains("thread id", StringComparison.Ordinal)
            || normalized.Contains("session not found", StringComparison.Ordinal)
            || normalized.Contains("invalid session", StringComparison.Ordinal)
            || normalized.Contains("resume failed", StringComparison.Ordinal);
    }

    public static bool ShouldStartFreshSession(Job job) => job.WorkerRetryFreshSession;

    public static bool HasSafeMetadata(Job job) => job.Phase == JobPhases.RetryWaiting
        && job.WorkerRetryAttempts > 0
        && !string.IsNullOrWhiteSpace(job.WorkerRetryFingerprint)
        && IsUsableTimestamp(job.WorkerRetryStartedUtc)
        && IsUsableTimestamp(job.WorkerRetryNextUtc);

    public static bool IsDue(Job job, DateTime nowUtc) => HasSafeMetadata(job) && nowUtc >= job.WorkerRetryNextUtc!.Value;

    public static bool TrySchedule(Job job, string kind, string summary, DateTime nowUtc, WorkerConfig config, out TimeSpan delay)
        => TrySchedule(job, kind, summary, nowUtc, config, retryAtUtc: null, out delay);

    public static bool TrySchedule(Job job, string kind, string summary, DateTime nowUtc, WorkerConfig config, DateTime? retryAtUtc, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!WorkerBlockerKinds.IsRetryable(kind)) return false;

        var fingerprint = Fingerprint(kind, summary);
        var same = string.Equals(job.WorkerRetryFingerprint, fingerprint, StringComparison.Ordinal);
        var continuingRepairEpisode = kind.Equals(WorkerBlockerKinds.WorkerRepairable, StringComparison.OrdinalIgnoreCase)
            && job.WorkerRetryLastKind?.Equals(WorkerBlockerKinds.WorkerRepairable, StringComparison.OrdinalIgnoreCase) == true
            && job.WorkerRetryAttempts > 0
            && IsUsableTimestamp(job.WorkerRetryStartedUtc);
        if (!same && !continuingRepairEpisode)
        {
            job.WorkerRetryAttempts = 0;
            job.WorkerRetryStartedUtc = nowUtc;
        }
        else job.WorkerRetryStartedUtc ??= nowUtc;

        // Service/transient failures are temporal gates.  They must continue
        // to be retried after a long outage or usage-limit reset; the delay is
        // capped to keep malformed or unbounded diagnostics from creating a
        // single effectively permanent sleep.  A workerRepairable episode is
        // bounded across changing summaries because model paraphrases must not
        // reset its configured attempt and elapsed-time budget.  Clear() marks
        // genuine progress and starts a fresh episode for a later failure.
        if (kind.Equals(WorkerBlockerKinds.WorkerRepairable, StringComparison.OrdinalIgnoreCase))
        {
            if (nowUtc - job.WorkerRetryStartedUtc.Value >= config.EffectiveWorkerRetryMaxElapsed) return false;
            if (job.WorkerRetryAttempts >= config.EffectiveWorkerRetryMaxAttempts) return false;
        }

        job.WorkerRetryFingerprint = fingerprint;
        job.WorkerRetryLastKind = kind;
        job.WorkerRetryLastSummary = summary;
        job.WorkerRetryAttempts = job.WorkerRetryAttempts == int.MaxValue ? int.MaxValue : job.WorkerRetryAttempts + 1;
        job.WorkerRetryFreshSession = RequiresFreshSession(kind, summary);
        var exponent = Math.Min(job.WorkerRetryAttempts - 1, 10);
        var multiplier = Math.Pow(2, exponent);
        var seconds = Math.Min(config.EffectiveWorkerRetryBaseDelay.TotalSeconds * multiplier, MaxRetryDelay.TotalSeconds);
        delay = TimeSpan.FromSeconds(Math.Max(1, seconds));
        var parsedRetryAt = retryAtUtc is { } explicitRetryAt
            ? NormalizeUtc(explicitRetryAt)
            : RetryTimestampPolicy.ParseFuture(summary, NormalizeUtc(nowUtc));
        if (parsedRetryAt is { } retryAt && retryAt > NormalizeUtc(nowUtc))
        {
            // A service-provided reset is authoritative.  Persisting the
            // absolute UTC instant makes restart recovery deterministic.
            job.WorkerRetryNextUtc = retryAt;
            job.WorkerRetryResetUtc = retryAt;
        }
        else
        {
            job.WorkerRetryNextUtc = NormalizeUtc(nowUtc) + delay;
            job.WorkerRetryResetUtc = null;
        }
        return true;
    }

    public static bool TrySchedule(Job job, WorkerBlocker blocker, DateTime nowUtc, WorkerConfig config, out TimeSpan delay)
        => TrySchedule(job, blocker.Kind, blocker.Summary, nowUtc, config, blocker.RetryAtUtc, out delay);

    public static bool TryParseRetryAt(string? detail, DateTime nowUtc, out DateTime retryAtUtc)
        => RetryTimestampPolicy.TryParseFuture(detail, nowUtc, out retryAtUtc);

    /// <summary>
    /// Rebuilds the minimum retry ledger when an older worker crashed between
    /// journal writes.  A RetryWaiting phase is already an explicit durable
    /// decision to retry; dropping it into a terminal Blocked state would lose
    /// recoverable task work.  The reconstruction is intentionally
    /// conservative: it preserves any valid fields, counts the reconstructed
    /// retry as one attempt, and uses the normal bounded delay before launch.
    /// </summary>
    public static bool TryReconstruct(Job job, DateTime nowUtc, WorkerConfig config, out RetryMetadataReconstruction reconstruction)
    {
        reconstruction = default!;
        if (job.Phase != JobPhases.RetryWaiting || HasSafeMetadata(job)) return false;

        var now = NormalizeUtc(nowUtc);
        var kind = WorkerBlockerKinds.All.Contains(job.LastBlockerKind ?? string.Empty)
            && WorkerBlockerKinds.IsRetryable(job.LastBlockerKind!)
            ? job.LastBlockerKind!
            : WorkerBlockerKinds.WorkerRepairable;
        var summary = string.IsNullOrWhiteSpace(job.LastBlockerSummary)
            ? "Retry metadata was incomplete after a worker interruption; reconstructing a safe retry."
            : job.LastBlockerSummary!.Trim();

        var started = UsableTimestamp(job.WorkerRetryStartedUtc)
            ?? UsableTimestamp(job.PhaseChangedUtc)
            ?? UsableTimestamp(job.StartedUtc)
            ?? now;
        if (started > now) started = now;

        var attempts = Math.Max(1, job.WorkerRetryAttempts);
        job.WorkerRetryAttempts = attempts;
        job.WorkerRetryStartedUtc = started;
        job.WorkerRetryLastKind = kind;
        job.WorkerRetryLastSummary = summary;
        job.WorkerRetryFingerprint ??= Fingerprint(kind, summary);
        if (string.IsNullOrWhiteSpace(job.WorkerRetryFingerprint)) job.WorkerRetryFingerprint = Fingerprint(kind, summary);
        job.WorkerRetryFreshSession |= RequiresFreshSession(kind, summary);

        var mode = ResolveRetryMode(job);
        job.WorkerRetryMode = mode.ToString();
        var parsedReset = job.WorkerRetryResetUtc is { } reset && reset > now
            ? NormalizeUtc(reset)
            : RetryTimestampPolicy.ParseFuture(summary, now);
        job.WorkerRetryResetUtc = parsedReset;
        var next = UsableTimestamp(job.WorkerRetryNextUtc);
        if (next is null || next <= now)
            next = parsedReset is { } future ? future : now + config.EffectiveWorkerRetryBaseDelay;
        job.WorkerRetryNextUtc = next;
        reconstruction = new RetryMetadataReconstruction(
            mode,
            $"Reconstructed retry metadata for {kind}; next attempt scheduled at {next:O}.");
        return true;
    }

    public static bool TryReconstruct(Job job, DateTime nowUtc, WorkerConfig config, out RecoveryMode mode, out string reason)
    {
        if (TryReconstruct(job, nowUtc, config, out var reconstruction))
        {
            mode = reconstruction.Mode;
            reason = reconstruction.Reason;
            return true;
        }

        mode = RecoveryPlanner.ModeFor(job);
        reason = string.Empty;
        return false;
    }

    private static RecoveryMode ResolveRetryMode(Job job)
    {
        if (string.Equals(job.WorkerRetryMode, nameof(RecoveryMode.ResumeRepair), StringComparison.OrdinalIgnoreCase)) return RecoveryMode.ResumeRepair;
        if (string.Equals(job.WorkerRetryMode, nameof(RecoveryMode.ResumeInitial), StringComparison.OrdinalIgnoreCase)) return RecoveryMode.ResumeInitial;
        if (string.Equals(job.WorkerRetryMode, nameof(RecoveryMode.Initial), StringComparison.OrdinalIgnoreCase)) return RecoveryMode.Initial;
        if (job.PendingResultIsRepair || job.PendingCheckFailures.Count > 0 || job.PendingFeedback.Count > 0) return RecoveryMode.ResumeRepair;
        return string.IsNullOrWhiteSpace(job.ThreadId) ? RecoveryMode.Initial : RecoveryMode.ResumeInitial;
    }

    private static bool IsUsableTimestamp(DateTime? value) => value is { } timestamp && timestamp != DateTime.MinValue && timestamp != DateTime.MaxValue;
    private static DateTime? UsableTimestamp(DateTime? value) => IsUsableTimestamp(value) ? NormalizeUtc(value!.Value) : null;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static void Clear(Job job)
    {
        job.WorkerRetryAttempts = 0;
        job.WorkerRetryStartedUtc = null;
        job.WorkerRetryNextUtc = null;
        job.WorkerRetryFingerprint = null;
        job.WorkerRetryLastKind = null;
        job.WorkerRetryLastSummary = null;
        job.WorkerRetryMode = null;
        job.WorkerRetryResetUtc = null;
        job.WorkerRetryFreshSession = false;
    }
}

public sealed record RetryMetadataReconstruction(RecoveryMode Mode, string Reason);

public sealed record Workspace(string Repository, string Directory, string Branch, string Remote, string BaseRef = "");

public sealed record WorkspaceBranchCandidate(string Branch, string Directory);

public static class WorkspaceBranchPolicy
{
    public static WorkspaceBranchCandidate SelectAvailable(
        string baseBranch,
        string baseDirectory,
        IReadOnlySet<string> localBranches,
        IReadOnlySet<string> remoteBranches,
        IReadOnlySet<string> directories)
    {
        for (var attempt = 1; attempt <= 10_000; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $"-retry-{attempt}";
            var candidate = new WorkspaceBranchCandidate(baseBranch + suffix, baseDirectory + suffix);
            if (!localBranches.Contains(candidate.Branch)
                && !remoteBranches.Contains(candidate.Branch)
                && !directories.Contains(candidate.Directory)) return candidate;
        }

        throw new InvalidOperationException("No collision-free task branch is available.");
    }

    public static string SelectStartingRef(string pullRequestsJson, string defaultHead, string priorRemoteRef)
    {
        using var document = JsonDocument.Parse(pullRequestsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Pull request lookup did not return an array.");
        var prior = document.RootElement.EnumerateArray().FirstOrDefault();
        if (prior.ValueKind != JsonValueKind.Object) return priorRemoteRef;
        if (!prior.TryGetProperty("mergedAt", out var mergedAt)
            || mergedAt.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return priorRemoteRef;
        if (!prior.TryGetProperty("baseRefName", out var baseRefName)
            || string.IsNullOrWhiteSpace(baseRefName.GetString())) return defaultHead;
        return "origin/" + baseRefName.GetString();
    }
}
public sealed record PullRequestState(string Url, string Repository, string HeadOid = "");

public static class PublicationMetadata
{
    public static string CommitMessage(JsonElement result, int sequence) =>
        NonBlank(result, "commitMessage", $"Complete task {sequence}");

    public static string PullRequestTitle(JsonElement result, string taskTitle) =>
        NonBlank(result, "prTitle", taskTitle);

    public static string PullRequestBody(JsonElement result) =>
        NonBlank(result, "prBody", NonBlank(result, "summary", "Automated task"));

    private static string NonBlank(JsonElement result, string property, string fallback)
    {
        if (!result.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}

public static class RepositoryPathPolicy
{
    public static string Normalize(string repoRoot, string repository)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.IsPathRooted(repository) ? repository : Path.Combine(root, repository));
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidDataException("Repository must resolve to a directory beneath the configured repository root: " + repository);
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}

public static class WorkspaceProcessEnvironment
{
    public static IReadOnlyDictionary<string, string> IsolatedBuild() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CARGO_TARGET_DIR"] = "target",
        ["CARGO_INCREMENTAL"] = "0"
    };
}
public sealed record ReviewFeedback(string ThreadId, string CommentNodeId, long CommentDatabaseId, string Body, string Url);
public sealed record ReviewDisposition(string ThreadId, bool Addressed, string ReplyBody);
public sealed record CheckDisposition(string CheckId, bool Addressed, string Summary);

public sealed class ReviewWindow
{
    public DateTime? GreenSinceUtc { get; set; }
    public bool Closed { get; set; }

    public bool Update(bool green, bool newActionableFeedback, DateTime now, TimeSpan quietPeriod)
    {
        if (!green)
        {
            GreenSinceUtc = null;
            Closed = false;
            return false;
        }

        if (GreenSinceUtc is null || newActionableFeedback)
        {
            GreenSinceUtc = now;
            Closed = false;
        }

        if (Closed) return true;
        if (now - GreenSinceUtc.Value >= quietPeriod) Closed = true;
        return Closed;
    }
}

public sealed class Job
{
    public required TaskDto Task { get; set; }
    public string Phase { get; set; } = JobPhases.Claimed;
    public DateTime PhaseChangedUtc { get; set; }
    public string? BlockReason { get; set; }
    public DateTime StartedUtc { get; set; }
    public string[] Latest { get; set; } = [];
    public DateTime? LatestChangedUtc { get; set; }
    public string? ThreadId { get; set; }
    public bool ReservationOwnerRecorded { get; set; }
    public bool ExactReservationOwnerRecorded { get; set; }
    public List<Workspace> Workspaces { get; set; } = [];
    public List<PullRequestState> PullRequests { get; set; } = [];
    public required string Prompt { get; set; }
    public required string Model { get; set; }
    public required string Effort { get; set; }
    public int RepairAttempts { get; set; }
    public DateTime? RepairStartedUtc { get; set; }
    public Dictionary<string, int> RepairAttemptsByPullRequest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> RepairStartedUtcByPullRequest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ProcessedFeedbackIds { get; set; } = new(StringComparer.Ordinal);
    public List<ReviewFeedback> PendingFeedback { get; set; } = [];
    public HashSet<string> ProcessedCheckIds { get; set; } = new(StringComparer.Ordinal);
    public List<CheckState> PendingCheckFailures { get; set; } = [];
    public ReviewWindow ReviewWindow { get; set; } = new();
    public bool ReadyForReviewRecorded { get; set; }
    public string? PendingResultJson { get; set; }
    public bool PendingResultIsRepair { get; set; }
    public Dictionary<string, PublicationProgress> Publication { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExecutionStartHeads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PullRequestCommentRecorded { get; set; }
    public bool CodexResultCommentRecorded { get; set; }
    public bool CleanupPending { get; set; }
    public DateTime? ObservedTaskUpdatedAt { get; set; }
    public string? ObservedDescription { get; set; }
    public HashSet<string> ProcessedHumanCommentKeys { get; set; } = new(StringComparer.Ordinal);
    public string? PendingDescription { get; set; }
    public List<TaskCommentDto> PendingHumanComments { get; set; } = [];
    public bool TaskUpdateWindowClosed { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TaskUpdateInFlight { get; set; }
    public bool BlockedReassessmentAttempted { get; set; }
    public bool AdoptedBlockedWorkspace { get; set; }
    public bool AdoptedResultReassessmentAttempted { get; set; }
    public int WorkerRetryAttempts { get; set; }
    public DateTime? WorkerRetryStartedUtc { get; set; }
    public DateTime? WorkerRetryNextUtc { get; set; }
    public DateTime? WorkerRetryResetUtc { get; set; }
    public string? WorkerRetryFingerprint { get; set; }
    public string? WorkerRetryLastKind { get; set; }
    public string? WorkerRetryLastSummary { get; set; }
    public string? WorkerRetryMode { get; set; }
    public bool WorkerRetryFreshSession { get; set; }
    public Dictionary<string, string> RepairGenerationByPullRequest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> TransientCheckRerunUtc { get; set; } = new(StringComparer.Ordinal);
    public string? LastBlockerKind { get; set; }
    public string? LastBlockerSummary { get; set; }
    public string[] LastBlockerEvidence { get; set; } = [];
    public bool HumanReviewRequested { get; set; }
    public bool BlockCommentRecorded { get; set; }
    public string? BlockActor { get; set; }
}

public static class AdoptedWorkspaceResultPolicy
{
    public static bool ShouldReassess(bool adoptedBlockedWorkspace, bool reassessmentAttempted, bool reportedChanged, bool repositoryChanged)
        => adoptedBlockedWorkspace && !reassessmentAttempted && !reportedChanged && repositoryChanged;
}

public static class TaskUpdatePolicy
{
    public static bool AcceptsUpdates(Job job) => !job.TaskUpdateWindowClosed && job.Phase is JobPhases.Claimed or JobPhases.Clarifying or JobPhases.Implementing or JobPhases.Repairing;

    public static void Seed(Job job, TaskDto task)
    {
        job.ObservedTaskUpdatedAt = task.UpdatedAt;
        job.ObservedDescription = task.Description;
        foreach (var comment in task.Comments) job.ProcessedHumanCommentKeys.Add(CommentKey(comment));
    }

    public static bool Ingest(Job job, TaskDto task)
    {
        var changed = false;
        var legacy = job.ObservedDescription is null && job.ObservedTaskUpdatedAt is null;
        if (legacy) { job.ObservedDescription = job.Task.Description; changed = true; }
        if (!string.Equals(job.ObservedDescription, task.Description, StringComparison.Ordinal))
        {
            job.ObservedDescription = task.Description;
            job.PendingDescription = task.Description;
            changed = true;
        }
        foreach (var comment in task.Comments.OrderBy(comment => comment.Timestamp))
        {
            var key = CommentKey(comment);
            if (!job.ProcessedHumanCommentKeys.Add(key)) continue;
            changed = true;
            if (!comment.Actor.Equals("user", StringComparison.OrdinalIgnoreCase)) continue;
            if (legacy && comment.Timestamp <= job.StartedUtc) continue;
            job.PendingHumanComments.Add(comment);
        }
        if (job.ObservedTaskUpdatedAt != task.UpdatedAt) changed = true;
        job.ObservedTaskUpdatedAt = task.UpdatedAt;
        return changed;
    }

    public static bool HasPending(Job job) => job.PendingDescription is not null || job.PendingHumanComments.Count > 0;
    public static PendingTaskUpdateBatch Capture(Job job) => new(job.PendingDescription, [.. job.PendingHumanComments]);
    public static void BeginApplying(Job job) => job.TaskUpdateInFlight = true;
    public static void EndApplying(Job job) => job.TaskUpdateInFlight = false;
    public static void MarkDelivered(Job job, PendingTaskUpdateBatch batch)
    {
        if (batch.Description is not null && string.Equals(job.PendingDescription, batch.Description, StringComparison.Ordinal)) job.PendingDescription = null;
        var delivered = batch.Comments.Select(CommentKey).ToHashSet(StringComparer.Ordinal);
        job.PendingHumanComments.RemoveAll(comment => delivered.Contains(CommentKey(comment)));
    }
    public static string CommentKey(TaskCommentDto comment) => $"{comment.Timestamp.ToUniversalTime():O}\n{comment.Actor}\n{comment.Comment}";
    public static string DashboardPhase(Job job, string phase) => job.TaskUpdateInFlight
        ? "Applying task update"
        : HasPending(job) ? "Task update queued" : phase;
}

public static class BlockedReassessmentPolicy
{
    public static bool ShouldReassess(Job job, string? status) => status == "blocked" && !job.BlockedReassessmentAttempted && !string.IsNullOrWhiteSpace(job.ThreadId);
}

public static class WorkspaceCleanupPolicy
{
    public static bool IsProvenOwned(Job job, string worktreeRoot)
    {
        if (job.Workspaces.Count == 0) return false;
        var root = Path.GetFullPath(worktreeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in job.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(workspace.Repository) || string.IsNullOrWhiteSpace(workspace.Directory) || string.IsNullOrWhiteSpace(workspace.Branch)) return false;
            string path; try { path = Path.GetFullPath(workspace.Directory); } catch { return false; }
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(path) || !repositories.Add(workspace.Repository)) return false;
            if (!workspace.Branch.StartsWith($"codex/task-{job.Task.Sequence}-", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public static bool CanDelete(Job job) => job.Phase == JobPhases.Done && job.CleanupPending;
    public static IReadOnlyList<Job> Pending(IEnumerable<Job> jobs) => jobs.Where(CanDelete).ToArray();
}

public sealed class PublicationProgress
{
    public bool CommitCreated { get; set; }
    public bool Pushed { get; set; }
    public string? PullRequestUrl { get; set; }
}

public static class JobPhases
{
    public const string Claimed = "Claimed";
    public const string Clarifying = "Clarifying repository";
    public const string Implementing = "Implementing";
    public const string Repairing = "Repairing";
    public const string Publishing = "Publishing";
    public const string Monitoring = "Monitoring";
    public const string RetryWaiting = "Retry waiting";
    public const string StatusSyncPending = "Status sync pending";
    public const string Blocked = "Blocked";
    public const string Done = "Done";
}

public sealed class Journal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public List<Job> Jobs { get; set; } = [];
    public DateTime? CodexUnavailableUntilUtc { get; set; }
    public static Journal Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json) ?? new Journal()
        : new Journal();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, true);
    }
}

public static partial class CodexUsageLimitPolicy
{
    private const string UsageLimitMarker = "You've hit your usage limit.";

    public static bool TryGetRetryUtc(string diagnostic, DateTime nowUtc, TimeZoneInfo localZone, out DateTime retryUtc)
    {
        retryUtc = default;
        if (!diagnostic.Contains(UsageLimitMarker, StringComparison.OrdinalIgnoreCase)) return false;

        var match = RetryTimeRegex().Match(diagnostic);
        if (match.Success)
        {
            var normalized = OrdinalSuffixRegex().Replace(match.Groups["retry"].Value, "$1");
            if (DateTime.TryParseExact(normalized, "MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var localTime))
            {
                retryUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), localZone);
                return true;
            }
        }

        retryUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddHours(1);
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"try again at (?<retry>[A-Za-z]{3} \d{1,2}(?:st|nd|rd|th)?, \d{4} \d{1,2}:\d{2} [AP]M)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RetryTimeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,2})(?:st|nd|rd|th)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OrdinalSuffixRegex();
}

public static class RecoveryPlanner
{
    public static IReadOnlyList<Job> JobsToRequeue(Journal journal, DateTime? nowUtc = null) => journal.Jobs
        .Where(job => job.Phase is not (JobPhases.Done or JobPhases.Blocked or JobPhases.Monitoring))
        .Where(job => job.Phase != JobPhases.RetryWaiting
            || (WorkerRetryPolicy.HasSafeMetadata(job) && (nowUtc is null || WorkerRetryPolicy.IsDue(job, nowUtc.Value))))
        .OrderBy(job => job.StartedUtc)
        .ToArray();

    public static RecoveryMode ModeFor(Job job) => job.Phase switch
    {
        JobPhases.StatusSyncPending => RecoveryMode.SyncBlocked,
        JobPhases.Publishing when !string.IsNullOrWhiteSpace(job.PendingResultJson) => RecoveryMode.Publish,
        JobPhases.Repairing => RecoveryMode.ResumeRepair,
        JobPhases.Implementing when !string.IsNullOrWhiteSpace(job.ThreadId) => RecoveryMode.ResumeInitial,
        JobPhases.RetryWaiting when string.Equals(job.WorkerRetryMode, nameof(RecoveryMode.ResumeRepair), StringComparison.OrdinalIgnoreCase) => RecoveryMode.ResumeRepair,
        JobPhases.RetryWaiting when job.PendingResultIsRepair => RecoveryMode.ResumeRepair,
        JobPhases.RetryWaiting when !string.IsNullOrWhiteSpace(job.ThreadId) => RecoveryMode.ResumeInitial,
        JobPhases.Publishing => RecoveryMode.UnrecoverablePublication,
        _ => RecoveryMode.Initial
    };

    public static RecoveryMode ModeForAdopted(Job job)
    {
        if (job.PullRequests.Count == 0) return RecoveryMode.Initial;
        return job.PendingCheckFailures.Count > 0 || job.PendingFeedback.Count > 0
            ? RecoveryMode.ResumeRepair
            : RecoveryMode.Monitoring;
    }
}

public enum RecoveryMode { Initial, ResumeInitial, ResumeRepair, Publish, Monitoring, SyncBlocked, UnrecoverablePublication }

public static class MergeBasePolicy
{
    public static bool IsValid(string baseRefName)
    {
        if (string.IsNullOrWhiteSpace(baseRefName) || baseRefName == "@" || baseRefName.StartsWith('-') || baseRefName.Length > 255) return false;
        if (baseRefName.StartsWith('/') || baseRefName.EndsWith('/') || baseRefName.EndsWith('.')
            || baseRefName.Contains("..", StringComparison.Ordinal) || baseRefName.Contains("@{", StringComparison.Ordinal)
            || baseRefName.Contains("//", StringComparison.Ordinal)) return false;
        if (baseRefName.Split('/').Any(component => component.StartsWith('.') || component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))) return false;
        return !baseRefName.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
            || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\');
    }
}

public static class RepairRetryPolicy
{
    /// <summary>
    /// Creates a stable identity for one repair generation.  A new PR head or
    /// a new pending check/review identity represents substantive new work and
    /// therefore gets a fresh repair budget.  Status/details are deliberately
    /// excluded so polling the same failure does not reset the budget.
    /// </summary>
    public static string GenerationFingerprint(
        string pullRequestUrl,
        string? headOid,
        IEnumerable<CheckState> pendingChecks,
        IEnumerable<ReviewFeedback> pendingFeedback)
    {
        var checkIdentity = pendingChecks
            .Select(check => string.Join('\u001f', check.Name, check.PullRequestUrl, check.Link))
            .OrderBy(identity => identity, StringComparer.Ordinal);
        var feedbackIdentity = pendingFeedback
            .Select(feedback => string.Join('\u001f', feedback.ThreadId, feedback.CommentNodeId, feedback.Url))
            .OrderBy(identity => identity, StringComparer.Ordinal);
        var payload = string.Join("\n", new[]
        {
            pullRequestUrl.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(headOid) ? "<unknown-head>" : headOid.Trim().ToLowerInvariant(),
            "checks:" + string.Join("\u001e", checkIdentity),
            "feedback:" + string.Join("\u001e", feedbackIdentity)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string GenerationFingerprint(
        PullRequestState pullRequest,
        IEnumerable<CheckState> pendingChecks,
        IEnumerable<ReviewFeedback> pendingFeedback)
        => GenerationFingerprint(pullRequest.Url, pullRequest.HeadOid, pendingChecks, pendingFeedback);

    public static bool BeginGeneration(
        Job job,
        string pullRequestUrl,
        string? headOid,
        IEnumerable<CheckState> pendingChecks,
        IEnumerable<ReviewFeedback> pendingFeedback,
        DateTime nowUtc)
    {
        var fingerprint = GenerationFingerprint(pullRequestUrl, headOid, pendingChecks, pendingFeedback);
        if (job.RepairGenerationByPullRequest.TryGetValue(pullRequestUrl, out var previous)
            && previous.Equals(fingerprint, StringComparison.Ordinal)) return false;

        job.RepairGenerationByPullRequest[pullRequestUrl] = fingerprint;
        job.RepairAttemptsByPullRequest[pullRequestUrl] = 0;
        job.RepairStartedUtcByPullRequest[pullRequestUrl] = nowUtc;
        job.RepairAttempts = 0;
        job.RepairStartedUtc = nowUtc;
        return true;
    }

    public static bool EnsureGeneration(
        Job job,
        string pullRequestUrl,
        string? headOid,
        IEnumerable<CheckState> pendingChecks,
        IEnumerable<ReviewFeedback> pendingFeedback,
        DateTime nowUtc)
        => BeginGeneration(job, pullRequestUrl, headOid, pendingChecks, pendingFeedback, nowUtc);

    public static bool BeginGeneration(
        Job job,
        PullRequestState pullRequest,
        IEnumerable<CheckState> pendingChecks,
        IEnumerable<ReviewFeedback> pendingFeedback,
        DateTime nowUtc)
        => BeginGeneration(job, pullRequest.Url, pullRequest.HeadOid, pendingChecks, pendingFeedback, nowUtc);

    public static void RearmChecks(Job job, IEnumerable<CheckState> checks)
    {
        var ids = checks.Select(check => check.Id).ToHashSet(StringComparer.Ordinal);
        job.PendingCheckFailures.RemoveAll(check => ids.Contains(check.Id));
        job.ProcessedCheckIds.RemoveWhere(ids.Contains);
    }
}

public sealed record ClaimSnapshot(WorkerConfig Config, string Prompt);
public static class ClaimAdmission
{
    public static bool TrySnapshot(WorkerConfig config, string configPath, out ClaimSnapshot? snapshot, out string? error)
    {
        try
        {
            var promptPath = Path.IsPathRooted(config.PromptFile) ? config.PromptFile : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, config.PromptFile);
            var prompt = File.ReadAllText(promptPath);
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("Prompt file is empty: " + promptPath);
            snapshot = new ClaimSnapshot(config, prompt);
            error = null;
            return true;
        }
        catch (Exception exception) { snapshot = null; error = exception.Message; return false; }
    }
}

public static class ReservationAttribution
{
    public const string Pending = "Reservation owner: codexThreadId=pending";
    public static string Exact(string threadId) => "Reservation owner: codexThreadId=" + threadId;
    public static bool NeedsPending(Job job) => !job.ReservationOwnerRecorded;
    public static bool NeedsExact(Job job) => !job.ExactReservationOwnerRecorded && !string.IsNullOrWhiteSpace(job.ThreadId);
}

public static class PublicationPolicy
{
    public static bool HasTaskCommit(PublicationProgress progress, bool hasUncommittedChanges, bool headChanged) => progress.CommitCreated || hasUncommittedChanges || headChanged;
    public static bool NeedsPush(PublicationProgress progress, string localHead, string? remoteHead) => !string.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase);
    public static bool NeedsPullRequest(PublicationProgress progress, string? recoveredUrl) => string.IsNullOrWhiteSpace(progress.PullRequestUrl) && string.IsNullOrWhiteSpace(recoveredUrl);
}

public static class CommitHookRecoveryPolicy
{
    private const string CheckStarted = "Stasis pre-commit: checking canonical source format";
    private const string CheckPassed = "all Stasis files are formatted";
    private const string RestageInstruction = "Commit blocked: stage the formatted Stasis changes, then commit again.";
    private const string FormatterRan = "Stasis pre-commit: formatting source before blocking this commit";
    private const string EnforcedFormatStarted = "Stasis pre-commit: enforcing canonical source format";
    private const string EnforcedPathFormatStarted = "Stasis pre-commit: enforcing canonical format on staged source paths";
    private const string EnforcedRestageInstruction = "Commit blocked: review and stage the enforced formatting changes, then commit again.";

    public static bool CanRestoreAndBypass(ExecResult commit, string indexBefore, string indexAfter, bool hasUnstagedChanges)
    {
        if (commit.ExitCode == 0 || !hasUnstagedChanges || !indexBefore.Equals(indexAfter, StringComparison.Ordinal)) return false;
        var diagnostic = commit.Output + "\n" + commit.Error;
        return diagnostic.Contains(CheckStarted, StringComparison.Ordinal)
            && diagnostic.Contains(CheckPassed, StringComparison.Ordinal)
            && diagnostic.Contains(RestageInstruction, StringComparison.Ordinal)
            && !diagnostic.Contains(FormatterRan, StringComparison.Ordinal);
    }

    public static bool CanRestageAndRetry(ExecResult commit, string indexBefore, string indexAfter, bool hasUnstagedChanges, bool onlyStasisChanges)
    {
        if (commit.ExitCode == 0 || !hasUnstagedChanges || !onlyStasisChanges
            || !indexBefore.Equals(indexAfter, StringComparison.Ordinal)) return false;
        var diagnostic = commit.Output + "\n" + commit.Error;
        return (diagnostic.Contains(EnforcedFormatStarted, StringComparison.Ordinal)
                || diagnostic.Contains(EnforcedPathFormatStarted, StringComparison.Ordinal))
            && diagnostic.Contains(EnforcedRestageInstruction, StringComparison.Ordinal);
    }
}

public sealed class ConcurrencyGate
{
    private readonly Func<int> capacity;
    private int active;
    public ConcurrencyGate(Func<int> capacity) => this.capacity = capacity;
    public int Active => Volatile.Read(ref active);
    public bool TryReserve()
    {
        while (true)
        {
            var observed = Active;
            if (observed >= capacity()) return false;
            if (Interlocked.CompareExchange(ref active, observed + 1, observed) == observed) return true;
        }
    }
    public void Release()
    {
        if (Interlocked.Decrement(ref active) < 0) throw new InvalidOperationException("Concurrency reservation underflow.");
    }
}

/// <summary>
/// Separate admission guard for the singleton research role. The total
/// process limit is still enforced by <see cref="ConcurrencyGate"/>; this
/// guard only prevents two scheduler paths from launching researchers.
/// </summary>
public sealed class ResearchAdmission
{
    private int active;

    public bool IsActive => Volatile.Read(ref active) != 0;

    public bool TryReserve() => Interlocked.CompareExchange(ref active, 1, 0) == 0;

    public void Release()
    {
        if (Interlocked.Exchange(ref active, 0) == 0)
            throw new InvalidOperationException("Research admission underflow.");
    }
}

public static class FeedbackPolicy
{
    public static IReadOnlyList<ReviewFeedback> AddNew(Job job, IEnumerable<ReviewFeedback> incoming)
    {
        var pending = job.PendingFeedback.Select(item => item.CommentNodeId).ToHashSet(StringComparer.Ordinal);
        var additions = incoming.Where(item => !job.ProcessedFeedbackIds.Contains(item.CommentNodeId) && pending.Add(item.CommentNodeId)).ToArray();
        job.PendingFeedback.AddRange(additions);
        return additions;
    }

    public static IReadOnlyList<ReviewDisposition> ActionsFor(Job job, IEnumerable<ReviewDisposition> dispositions)
    {
        var pendingThreads = job.PendingFeedback.Select(item => item.ThreadId).ToHashSet(StringComparer.Ordinal);
        return dispositions
            .Where(item => item.Addressed && !string.IsNullOrWhiteSpace(item.ReplyBody) && pendingThreads.Contains(item.ThreadId))
            .GroupBy(item => item.ThreadId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(item => !job.ProcessedFeedbackIds.Contains(ActionKey(item.ThreadId)))
            .ToArray();
    }

    public static string ActionKey(string threadId) => "action:" + threadId;
}

public static class ReviewActionLedger
{
    public static string ReplyKey(string threadId) => "reply:" + threadId;
    public static string ResolveKey(string threadId) => "resolve:" + threadId;
    public static bool NeedsReply(Job job, string threadId) => !job.ProcessedFeedbackIds.Contains(ReplyKey(threadId));
    public static bool NeedsResolve(Job job, string threadId) => !job.ProcessedFeedbackIds.Contains(ResolveKey(threadId));
}

public static class DashboardFormatter
{
    private static readonly Regex EscapeSequence = new("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", RegexOptions.Compiled);
    private static readonly Regex Controls = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.Compiled);

    public static string[] LatestLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var sanitized = Sanitize(text);
        if (TryHumanizeStructured(sanitized, out var humanized)) return humanized;
        return sanitized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3).ToArray();
    }

    public static string Truncate(string text, int width)
    {
        width = Math.Max(1, width);
        if (text.Length <= width) return text;
        if (width <= 3) return "..."[..width];
        return text[..(width - 3)] + "...";
    }

    public static string[] WrapLines(IEnumerable<string> sourceLines, int width, string indent = "  ", int maxLines = 3)
    {
        if (maxLines <= 0) return [];
        var available = Math.Max(1, width - indent.Length);
        var result = new List<string>(maxLines);
        foreach (var source in sourceLines)
        {
            var words = Sanitize(source).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = string.Empty;
            foreach (var rawWord in words)
            {
                var word = rawWord.Length <= available ? rawWord : Truncate(rawWord, available);
                if (current.Length == 0) current = word;
                else if (current.Length + 1 + word.Length <= available) current += " " + word;
                else
                {
                    result.Add(indent + current);
                    if (result.Count == maxLines) return result.ToArray();
                    current = word;
                }
            }
            if (current.Length > 0)
            {
                result.Add(indent + current);
                if (result.Count == maxLines) return result.ToArray();
            }
        }
        return result.ToArray();
    }

    private static string Sanitize(string text) => Controls.Replace(EscapeSequence.Replace(text.Replace("\r", string.Empty), string.Empty), string.Empty);

    private static bool TryHumanizeStructured(string text, out string[] lines)
    {
        lines = [];
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("status", out var status) && root.TryGetProperty("summary", out var summary))
            {
                _ = status;
                lines = ["Summary: " + Sanitize(summary.GetString() ?? string.Empty)];
                return true;
            }
            if (root.TryGetProperty("ambiguous", out var ambiguous) && root.TryGetProperty("rationale", out var rationale))
            {
                var result = new List<string> { ambiguous.GetBoolean() ? "Repository clarification: ambiguous" : "Repository clarification: identified" };
                result.Add("Rationale: " + Sanitize(rationale.GetString() ?? string.Empty));
                lines = result.Take(3).ToArray();
                return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    public static string[] NormalizePersistedLatest(IEnumerable<string> latest) => LatestLines(string.Join('\n', latest));

}

public static class DashboardSummary
{
    public static bool Update(Job job, string? text, DateTime changedUtc)
    {
        var normalized = DashboardFormatter.LatestLines(text);
        if (job.Latest.SequenceEqual(normalized, StringComparer.Ordinal)) return false;
        job.Latest = normalized;
        job.LatestChangedUtc = changedUtc;
        return true;
    }
}

public sealed class BufferedRefresh(IClock clock, TimeSpan minimumInterval, Func<Task> refresh)
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int pending;
    private DateTime? lastRefreshUtc;

    public void Request()
    {
        Interlocked.Exchange(ref pending, 1);
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await signal.WaitAsync(cancellationToken);
            if (Interlocked.Exchange(ref pending, 0) == 0) continue;

            if (lastRefreshUtc is { } previous)
            {
                var remaining = minimumInterval - (clock.UtcNow - previous);
                if (remaining > TimeSpan.Zero) await clock.Delay(remaining, cancellationToken);
            }

            // The upcoming refresh includes every request received while buffering.
            Interlocked.Exchange(ref pending, 0);
            await refresh();
            lastRefreshUtc = clock.UtcNow;
        }
    }
}

public sealed class FreshClaimAllowance
{
    private bool used;
    public bool TryReserve(ConcurrencyGate capacity)
    {
        if (used || !capacity.TryReserve()) return false;
        used = true;
        return true;
    }
}

public enum FreshClaimOutcome
{
    NotAttempted,
    ClaimedWithSpareCapacity,
    ClaimedAtCapacity,
    Unavailable
}

public sealed class ClaimCadence
{
    private DateTime anchorUtc;

    public ClaimCadence(DateTime startedUtc)
    {
        anchorUtc = startedUtc;
    }

    public bool ImmediateRefillPending { get; private set; } = true;

    public DateTime NextTickUtc(WorkerConfig config) => ImmediateRefillPending
        ? anchorUtc
        : anchorUtc + config.ClaimInterval;

    public bool IsDue(DateTime nowUtc, WorkerConfig config) => nowUtc >= NextTickUtc(config);

    public void CompleteTick(FreshClaimOutcome outcome, DateTime nowUtc)
    {
        anchorUtc = nowUtc;
        ImmediateRefillPending = outcome == FreshClaimOutcome.ClaimedWithSpareCapacity;
    }

    public void RequestImmediateRefill(DateTime nowUtc)
    {
        anchorUtc = nowUtc;
        ImmediateRefillPending = true;
    }
}

public static class DashboardPolicy
{
    public static IReadOnlyList<Job> VisibleJobs(IEnumerable<Job> jobs, DateTime now, TimeSpan blockedDisplayDuration) => jobs
        .GroupBy(job => job.Task.IssueId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(ChangedAt).ThenByDescending(job => job.StartedUtc).First())
        .Where(job => job.Phase != JobPhases.Done)
        .Where(job => job.Phase != JobPhases.Blocked || now - ChangedAt(job) < blockedDisplayDuration)
        .ToArray();

    private static DateTime ChangedAt(Job job) => job.PhaseChangedUtc > DateTime.MinValue ? job.PhaseChangedUtc : job.StartedUtc;
}

public static class MonitoringDisplay
{
    public static string Describe(Job job, DateTime nowUtc, TimeSpan quietPeriod, bool autoMergeAllowed)
    {
        if (job.ReadyForReviewRecorded)
        {
            if (!autoMergeAllowed) return "Waiting for your PR decision";
            if (job.ReviewWindow.GreenSinceUtc is not { } readyGreenSince)
                return "Ready for review · auto-merge waiting on CI/review window";

            var readyRemaining = quietPeriod - (nowUtc - readyGreenSince);
            if (readyRemaining <= TimeSpan.Zero || job.ReviewWindow.Closed)
                return "Ready to auto-merge";
            var readyMinutes = Math.Max(1, (int)Math.Ceiling(readyRemaining.TotalMinutes));
            return $"Ready for review · auto-merge in {readyMinutes}m";
        }
        if (job.ReviewWindow.GreenSinceUtc is not { } greenSince)
            return "Waiting on CI/review window";

        var remaining = quietPeriod - (nowUtc - greenSince);
        if (remaining <= TimeSpan.Zero)
            return "Waiting on CI/review window";
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Waiting on CI/review window · {minutes}m left";
    }
}

public sealed record ConsoleSegment(string Text, ConsoleColor Color);

public static class DashboardSegments
{
    public const ConsoleColor Structural = ConsoleColor.Gray;
    public const ConsoleColor Tag = ConsoleColor.Cyan;
    public const ConsoleColor Title = ConsoleColor.Magenta;
    public const ConsoleColor Detail = ConsoleColor.White;

    public static string FormatUpdateTimestamp(DateTimeOffset localTime) => localTime.ToString("h:mm tt", CultureInfo.InvariantCulture);

    public static string FormatElapsed(TimeSpan elapsed)
    {
        var wholeSeconds = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(elapsed.TotalSeconds)));
        return wholeSeconds.ToString("g", CultureInfo.InvariantCulture);
    }

    public static ConsoleSegment[] JobHeader(Job job, string phase, TimeSpan elapsed) =>
    [
        new($"#{job.Task.Sequence}", Tag),
        new(" ", Structural),
        new(job.Task.Title, Title),
        new(" [", Structural),
        new(phase, Tag),
        new($"] {FormatElapsed(elapsed)}", Structural)
    ];

    public static ConsoleSegment[] RepositoryLine(string repositories, string? pullRequests) =>
    [
        new("  ", Structural),
        new(repositories, Tag),
        new(string.IsNullOrWhiteSpace(pullRequests) ? string.Empty : " | " + pullRequests, Structural)
    ];

    public static ConsoleSegment[] UpdateLine(string wrappedText, DateTimeOffset localTime)
    {
        var text = wrappedText.StartsWith("  ", StringComparison.Ordinal) ? wrappedText[2..] : wrappedText;
        return
        [
            new("  " + FormatUpdateTimestamp(localTime) + " ", Tag),
            new(text, Detail)
        ];
    }

    public static ConsoleSegment[] Truncate(IEnumerable<ConsoleSegment> segments, int width)
    {
        var remaining = Math.Max(1, width);
        var result = new List<ConsoleSegment>();
        foreach (var segment in segments)
        {
            if (segment.Text.Length <= remaining) { result.Add(segment); remaining -= segment.Text.Length; if (remaining == 0) break; continue; }
            result.Add(segment with { Text = DashboardFormatter.Truncate(segment.Text, remaining) });
            break;
        }
        return result.Where(segment => segment.Text.Length > 0).ToArray();
    }
}

public static class ConsoleSegmentWriter
{
    public static void WriteLine(IEnumerable<ConsoleSegment> segments)
    {
        var previous = Console.ForegroundColor;
        try
        {
            foreach (var segment in segments) { Console.ForegroundColor = segment.Color; Console.Write(segment.Text); }
            Console.WriteLine();
        }
        finally { Console.ForegroundColor = previous; }
    }
}

public static class BlockedWorkspaceAdoption
{
    public static Job? TryAdopt(Journal journal, TaskDto claimedTask, string worktreeRoot, DateTime startedUtc)
    {
        var repositories = claimedTask.Repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(worktreeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = journal.Jobs
            .Where(job => job.Phase == JobPhases.Blocked && job.Task.IssueId.Equals(claimedTask.IssueId, StringComparison.OrdinalIgnoreCase))
            .Where(job => IsEligible(job, repositories, root))
            .OrderByDescending(job => job.StartedUtc)
            .FirstOrDefault();
        if (candidate is null) return null;

        candidate.Task = claimedTask;
        candidate.Phase = JobPhases.Claimed;
        candidate.StartedUtc = startedUtc;
        candidate.PhaseChangedUtc = startedUtc;
        candidate.BlockReason = null;
        candidate.Latest = [];
        candidate.LatestChangedUtc = null;
        candidate.ThreadId = null;
        candidate.ReservationOwnerRecorded = false;
        candidate.ExactReservationOwnerRecorded = false;
        candidate.PendingResultJson = null;
        candidate.PendingResultIsRepair = false;
        candidate.Publication.Clear();
        candidate.ExecutionStartHeads.Clear();
        candidate.RepairAttempts = 0;
        candidate.RepairStartedUtc = null;
        candidate.RepairAttemptsByPullRequest.Clear();
        candidate.RepairStartedUtcByPullRequest.Clear();
        candidate.RepairGenerationByPullRequest.Clear();
        candidate.TransientCheckRerunUtc.Clear();
        candidate.ObservedTaskUpdatedAt = null;
        candidate.ObservedDescription = null;
        candidate.ProcessedHumanCommentKeys.Clear();
        candidate.PendingDescription = null;
        candidate.PendingHumanComments.Clear();
        candidate.TaskUpdateWindowClosed = false;
        candidate.TaskUpdateInFlight = false;
        candidate.BlockedReassessmentAttempted = false;
        candidate.AdoptedBlockedWorkspace = true;
        candidate.AdoptedResultReassessmentAttempted = false;
        WorkerRetryPolicy.Clear(candidate);
        candidate.LastBlockerKind = null;
        candidate.LastBlockerSummary = null;
        candidate.LastBlockerEvidence = [];
        candidate.HumanReviewRequested = false;
        candidate.BlockCommentRecorded = false;
        candidate.BlockActor = null;
        return candidate;
    }

    private static bool IsEligible(Job job, HashSet<string> repositories, string worktreeRoot)
    {
        if (job.Workspaces.Count == 0 || string.IsNullOrWhiteSpace(job.Prompt) || string.IsNullOrWhiteSpace(job.Model) || string.IsNullOrWhiteSpace(job.Effort)) return false;
        if (!repositories.SetEquals(job.Task.Repositories) || !repositories.SetEquals(job.Workspaces.Select(workspace => workspace.Repository))) return false;
        if (job.Workspaces.Select(workspace => workspace.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Workspaces.Count) return false;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in job.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(workspace.Repository) || string.IsNullOrWhiteSpace(workspace.Directory) || string.IsNullOrWhiteSpace(workspace.Branch) || string.IsNullOrWhiteSpace(workspace.Remote)) return false;
            string directory;
            try { directory = Path.GetFullPath(workspace.Directory); } catch { return false; }
            if (!directory.StartsWith(worktreeRoot, StringComparison.OrdinalIgnoreCase) || !directories.Add(directory) || !branches.Add(workspace.Branch)) return false;
        }
        return true;
    }
}

public static class CodexEventParser
{
    public static (string? ThreadId, string? Text) Parse(string jsonLine)
    {
        using var document = JsonDocument.Parse(jsonLine);
        var root = document.RootElement;
        var threadId = root.TryGetProperty("thread_id", out var thread) ? thread.GetString() : null;
        string? text = root.TryGetProperty("text", out var directText) ? directText.GetString() : null;
        if (text is null && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var itemText)) text = itemText.GetString();
        if (text is null && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) text = message.GetString();
        return (threadId, text);
    }
}

public sealed class CodexTerminalEventTracker
{
    private bool structuredResultObserved;
    public bool Observe(string jsonLine)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            structuredResultObserved |= ContainsStructuredResult(root);
            return structuredResultObserved && IsTerminalEvent(root);
        }
        catch (JsonException) { return false; }
    }

    private static bool ContainsStructuredResult(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(element.GetString() ?? string.Empty);
                return ContainsStructuredResult(document.RootElement);
            }
            catch (JsonException) { return false; }
        }

        if (element.ValueKind != JsonValueKind.Object) return false;
        if (IsWorkerLifecycleResult(element) || IsResearchResult(element) || IsClarificationResult(element)) return true;

        // Codex has emitted all of these wrappers across its JSON event
        // variants.  Recurse only through named result/message payloads and
        // require a complete known contract at the leaf; an arbitrary object
        // under `result` or an incidental agent message is not enough.
        foreach (var name in new[] { "result", "payload", "output", "response", "data" })
            if (TryGetProperty(element, name, out var nested) && ContainsStructuredResult(nested)) return true;

        if (TryGetProperty(element, "item", out var item)
            && item.ValueKind == JsonValueKind.Object
            && TryGetString(item, "type", out var itemType)
            && itemType.Equals("agent_message", StringComparison.OrdinalIgnoreCase)
            && TryGetProperty(item, "text", out var text)
            && ContainsStructuredResult(text)) return true;
        if (TryGetProperty(element, "last_agent_message", out var lastMessage) && ContainsStructuredResult(lastMessage)) return true;
        if (TryGetProperty(element, "message", out var message) && ContainsStructuredResult(message)) return true;
        return false;
    }

    private static bool IsWorkerLifecycleResult(JsonElement element)
        => HasNonBlankString(element, "status")
            && HasNonBlankString(element, "summary")
            && HasBoolean(element, "workComplete")
            && HasObject(element, "blocker")
            && HasArray(element, "repositories");

    private static bool IsResearchResult(JsonElement element)
        => HasNonBlankString(element, "outcome")
            && HasNonBlankString(element, "summary")
            && HasArray(element, "findings")
            && HasArray(element, "mutations");

    private static bool IsClarificationResult(JsonElement element)
        => HasNonBlankString(element, "action")
            && HasArray(element, "repositories")
            && HasArray(element, "children")
            && HasNonBlankString(element, "rationale")
            && HasNumber(element, "confidence")
            && HasBoolean(element, "ambiguous");

    private static bool HasNonBlankString(JsonElement element, string name)
        => TryGetString(element, name, out var value) && !string.IsNullOrWhiteSpace(value);

    private static bool HasBoolean(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasNumber(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number;

    private static bool HasArray(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array;

    private static bool HasObject(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static bool IsTerminalEvent(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var eventType) ? eventType.GetString() : null;
        if (type is "turn.completed" or "task_complete") return true;
        return type == "event_msg" && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("type", out var payloadType) && payloadType.GetString() == "task_complete";
    }

}
