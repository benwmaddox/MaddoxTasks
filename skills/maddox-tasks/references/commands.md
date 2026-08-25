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

`agent claim` atomically selects the first eligible `Next` issue (priority ascending, then sequence ascending), requiring at least one `repo:<name>` label and no overlap with an `Active` or `ReadyForReview` issue. It changes exactly that issue to `Active` and returns its issue JSON, including the deterministic `repositories` array. It returns `null` when no claim is available. A scheduled runner should stop cleanly on `null`.

Repository labels are canonicalized as lowercase `repo:<name>` identities and compared case-insensitively. Active and `ReadyForReview` tasks cannot lose their last repository or acquire a repository reserved by another reserving task. To change a reservation, add the replacement label first, then remove the old label.

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
- `UpdateDescription`
- `AddComment`

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
