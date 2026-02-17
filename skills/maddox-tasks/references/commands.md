# Commands Reference

Use only agent JSON commands in agent workflows.

## Read Tasks

Windows:

```powershell
.\MaddoxTasks.exe agent issues
```

Linux/macOS:

```bash
./MaddoxTasks agent issues
```

Optional filters on `agent issues`:

- `--status <Backlog|Active|Blocked|Next|Done>`
- `--not-status <Backlog|Active|Blocked|Next|Done>`
- `--max-priority <1..5>`
- `--labels <comma,separated,labels>`
- `--due-before <yyyy-MM-dd or date-time>`
- `--include-done <true|false>` (default is `true`)

## Execute Commands

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

## Payload Schemas

`CreateIssue`:

```json
{
  "type": "CreateIssue",
  "title": "Fix reload bug",
  "description": "Investigate cache invalidation",
  "priority": 2,
  "parentId": "551e912f-eee0-4042-ab87-3a89826fd88e",
  "dueDate": "2026-02-20"
}
```

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
