# Commands Reference

Use only agent JSON commands in agent workflows.

## Read Tasks

Windows:

```powershell
.\MaddoxTasks.exe agent issues
```

`agent next` remains a read-only compatibility command for selecting the next existing Active/Next task. Use `agent claim` for concurrent workers that need an atomic repository reservation.

Linux/macOS:

```bash
./MaddoxTasks agent issues
```

Optional filters on `agent issues`:

- `--status <Backlog|Active|Blocked|Next|ReadyForReview|Ready for Review|Done|Rejected>`
- `--not-status <Backlog|Active|Blocked|Next|ReadyForReview|Ready for Review|Done|Rejected>`
- `--max-priority <1..5>`
- `--labels <comma,separated,labels>`
- `--due-before <yyyy-MM-dd or date-time>`
- `--include-done <true|false>` (default is `true`; legacy option name that includes terminal `Done` and `Rejected` tasks)

## Execute Commands

## Atomic Repository Claim

Read-only preview:

```powershell
.\MaddoxTasks.exe agent claim --dry-run
```

Real claim:

```powershell
.\MaddoxTasks.exe agent claim
```

`agent claim` first resets stale Codex reservations and then atomically selects the first eligible `Next` issue using hierarchy order. A reset applies only to an `Active` issue with `UpdatedAt` at least 24 hours before the single normalized claim time (24 hours with no activity) and a current-period comment beginning exactly `Reservation owner: codexThreadId=` with a nonempty value, including `unavailable`. The current period begins at the latest `StatusChanged` to `Active`; older reservation comments do not qualify. Reset events and audit comments are ordered by issue sequence and persist even when no claim is available. Task families are ordered by top-level root priority and sequence. Each family is traversed child-first, with siblings ordered by priority and sequence; parents are considered after all descendants. For `agent next`, `Active` is only a tiebreaker among otherwise equal-priority siblings or roots; it never overrides descendant-before-ancestor traversal. A child uses only its own reservation keys, so a reserved parent does not block a disjoint child. A reserved child is skipped so a later claimable sibling can be selected. A family is exhausted before selection moves to the next family. The selected issue must not overlap an `Active` or `ReadyForReview` issue. A repository-less issue is eligible and uses the synthetic `missing` reservation key while its returned `repositories` array stays empty. It changes exactly that issue to `Active` and returns its issue JSON. It returns `null` when no claim is available. A scheduled runner should stop cleanly on `null`. `agent next` is read-only and selects the first `Active` or `Next` issue using the same hierarchy order. Missing and cyclic parent links are handled deterministically. `--dry-run` simulates stale cleanup and claim selection without appending events; the preview issue reports `Next`.

Repository labels are canonicalized as lowercase `repo:<name>` identities and compared case-insensitively. With no repository labels, Active and `ReadyForReview` tasks reserve the synthetic `missing` identity; an explicit `repo:missing` collides with it. Status and label changes are rejected when their resulting reservation keys conflict. The scheduled runner starts a repository-less claim from normalized `RepoRoot`, passes no `--add-dir`, and warns that no repository was specified and the impact scope is unknown.

The scheduled runner checks `ReadyForReview` tasks after work. Only canonical `https://github.com/<owner>/<repo>/pull/<number>` URLs in descriptions/comments are associated. It closes a task only when all associated PRs have non-null `mergedAt`; no-PR, open, closed-unmerged, and lookup-error tasks remain unchanged.

To run this deterministic step directly:

```powershell
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --gh-timeout-seconds 45
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --dry-run
```

The JSON response includes `dryRun` and an `outcomes` array. Each outcome includes the task id, title, canonical deduplicated PR URLs, and one of `closed`, `noPullRequests`, `unmerged`, `lookupError`, `concurrentStateChange`, `notFound`, or `dryRun`. A dry run does not invoke GitHub CLI or mutate tasks.

Run with inline JSON:

```powershell
.\MaddoxTasks.exe agent command --json "{""type"":""ChangeStatus"",""issueId"":""1"",""newStatus"":""Active""}"
```

Run with file JSON:

```powershell
.\MaddoxTasks.exe agent command --file cmd.json
```

Set default actor once (used when payload omits `actor` for `UpdateDescription` / `AddComment`):

```powershell
.\MaddoxTasks.exe agent command --actor gpt-5.2 --file cmd.json
```

PowerShell stdin pattern (avoids quote escaping):

```powershell
@'
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
'@ | .\MaddoxTasks.exe agent command
```

Supported `type` values (all available agent commands):

- `CreateIssue`
- `ChangeStatus`
- `ChangePriority`
- `AddLabel`
- `RemoveLabel`
- `SetRepositoryLabels`
- `SplitIssue`
- `UpdateDescription`
- `AddComment`
- `RequeueBlocked`

Every issue returned by `agent issues` includes `repositories`, derived from its `repo:` labels.

## Payload Schemas

`CreateIssue`:

```json
{
  "type": "CreateIssue",
  "title": "Fix reload bug",
  "description": "Investigate cache invalidation",
  "priority": 2,
  "status": "Next",
  "parentId": "551e912f-eee0-4042-ab87-3a89826fd88e",
  "dueDate": "2026-02-20"
}
```

`status` is optional and defaults to `Next`. The only supported initial statuses are `Next` and explicit `Backlog`; other statuses must be applied with `ChangeStatus` after creation. Successful command responses include the final stored `status`.

`ChangeStatus`:

```json
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
```

`RequeueBlocked`:

```json
{
  "type": "RequeueBlocked",
  "dryRun": true
}
```

`dryRun` is optional, must be a JSON boolean, and defaults to `false`. The dedicated response contains `success`, `message`, `dryRun`, `changedIssueIds`, and `skippedIssueIds`. Changed IDs are all issues that were `Blocked` in the atomic snapshot; skipped IDs are every non-Blocked issue in that snapshot. Both GUID arrays follow issue sequence. Preview reports would-change IDs without appending events. Apply writes only `StatusChanged` events from `Blocked` to `Next` and commits them as one transaction. Zero-change and repeat executions succeed without appending events. Priority, descriptions, comments, labels, repository identities, due dates, and parent/child relationships are preserved.

`ChangePriority`:

```json
{
  "type": "ChangePriority",
  "issueId": "1",
  "newPriority": 1
}
```

`AddLabel`:

```json
{
  "type": "AddLabel",
  "issueId": "1",
  "label": "infra"
}
```

`RemoveLabel`:

```json
{
  "type": "RemoveLabel",
  "issueId": "1",
  "label": "infra"
}
```

`SetRepositoryLabels`:

```json
{
  "type": "SetRepositoryLabels",
  "issueId": "1",
  "repositories": ["StasisLang", "MaddoxTasks"]
}
```

The nonempty repository list replaces only `repo:` labels. Values are normalized and deduplicated case-insensitively. Reservation conflicts are checked in the same event-store transaction, so failure leaves all labels unchanged. The success response includes the canonical `repositories` array.

`SplitIssue`:

```json
{
  "type": "SplitIssue",
  "issueId": "1",
  "children": [
    { "title": "Update Alpha", "description": "Apply the change to Alpha.", "repository": "Alpha" },
    { "title": "Update Beta", "description": "Apply the change to Beta.", "repository": "Beta" }
  ]
}
```

At least two children are required. Each child must have a nonempty title and description and exactly one repository; repositories must be unique case-insensitively. Children inherit the source priority, start in `Next`, and use the source as `parentId`. The source becomes `Done`. Existing `IssueCreated`, `RepositoryLabelsSet`, and `StatusChanged` events are appended as one atomic transaction, so any validation or reservation conflict leaves the source and all prospective children unchanged.

`UpdateDescription`:

```json
{
  "type": "UpdateDescription",
  "issueId": "1",
  "description": "Updated description text",
  "actor": "gpt-5.2"
}
```

`AddComment`:

```json
{
  "type": "AddComment",
  "issueId": "1",
  "comment": "Blocked on dependency upgrade",
  "actor": "claude-sonnet"
}
```

Notes:

- `issueId` accepts sequence (`"1"`), GUID prefix, or full GUID.
- For `UpdateDescription` and `AddComment`, set `actor` to the model identifier being used. If omitted, `agent command --actor` applies. If neither is set, environment (`MADDOX_TASKS_AGENT_ACTOR`, `MADDOX_TASKS_ACTOR`, `CODEX_MODEL`, `OPENAI_MODEL`, `ANTHROPIC_MODEL`, `CLAUDE_MODEL`, `MODEL`) is checked. If still unset and Claude settings exist, `.claude/settings.local.json`, `.claude/settings.json`, or `~/.claude/settings.json` (`model`) is used. If still unset and Codex config exists, `~/.codex/config.toml` (`model` + `model_reasoning_effort`) is used. Final fallback is `agent`.
