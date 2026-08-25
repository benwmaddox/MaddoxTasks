# Maddox Tasks

## Quick Start (User First)

1. Go to GitHub Releases: `https://github.com/benwmaddox/MaddoxTasks/releases/latest`
2. Download your platform zip.
3. Extract it.
4. Run the app (no parameters needed):

Windows:

```powershell
.\MaddoxTasks.exe
```

Linux/macOS:

```bash
./MaddoxTasks
```

The app reuses the same DB on every run automatically.

## One-Time Windows Install (User-Level Skills + PATH)

From this repo:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

If you already extracted a release to another folder, still run the repo script and point it at that binary directory:

```powershell
powershell -ExecutionPolicy Bypass -File F:\Tasks\MaddoxTasks\scripts\install.ps1 -BinaryDir F:\MaddoxTasks -SkillSource F:\Tasks\MaddoxTasks\skills\maddox-tasks
```

What it does:

- Links `maddox-tasks` skill into `~/.agents/skills`, `~/.codex/skills`, and `~/.claude/skills`.
- Adds the directory containing `MaddoxTasks.exe` to your user `PATH`.

After install, open a new terminal and run:

```powershell
MaddoxTasks.exe
```

## What It Looks Like (TUI)

Main board:

![Maddox Tasks main board](docs/screenshots/tui-main-clean.png)

Help overlay (`?`):

![Maddox Tasks help overlay](docs/screenshots/tui-help-clean.png)

## Everyday CLI Commands (Released Binary)

Windows:

```powershell
.\MaddoxTasks.exe create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
.\MaddoxTasks.exe list --status Active
.\MaddoxTasks.exe status 1 Done
.\MaddoxTasks.exe label 1 architecture
.\MaddoxTasks.exe comment 1 "Waiting on security review"
.\MaddoxTasks.exe summary week
```

Linux/macOS:

```bash
./MaddoxTasks create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
./MaddoxTasks list --status Active
./MaddoxTasks status 1 Done
./MaddoxTasks label 1 architecture
./MaddoxTasks comment 1 "Waiting on security review"
./MaddoxTasks summary week
```

Issue tokens support:

- Sequence (`1`, `2`, ...)
- Full GUID
- GUID prefix

Tip: in TUI, press `Enter` on an issue to open detail view. Inside detail view, use `c` comment, `s` status, `d` description, `h` description history, and `q`/`Esc` to go back.
Comments and description history show `By` (`user`, `agent`, or a model id such as `gpt-5.2`).

Statuses are `Backlog`, `Next`, `Active`, `Blocked`, `ReadyForReview`, `Done`, and `Rejected`. `Done` and `Rejected` are terminal statuses; open views hide both by default, while `--include-done` includes all terminal tasks (the option name is retained for compatibility).

Active and `ReadyForReview` work reserve repositories using labels with the canonical `repo:<name>` prefix, for example `repo:StasisLang` (repository names are compared case-insensitively). An issue must have at least one repository before it can enter either reserving status; reserving tasks cannot share a repository. Moving to another status releases the reservation. Add a new `repo:` label before removing an old one when changing the repositories of active or review work.

## Agent JSON Commands

Windows:

```powershell
.\MaddoxTasks.exe agent issues
.\MaddoxTasks.exe agent next
.\MaddoxTasks.exe agent claim
.\MaddoxTasks.exe agent claim --dry-run
.\MaddoxTasks.exe agent command --file cmd.json
.\MaddoxTasks.exe agent command --actor gpt-5.2 --file cmd.json
```

Linux/macOS:

```bash
./MaddoxTasks agent issues
./MaddoxTasks agent next
./MaddoxTasks agent claim
./MaddoxTasks agent claim --dry-run
./MaddoxTasks agent command --file cmd.json
./MaddoxTasks agent command --actor gpt-5.2 --file cmd.json
```

`cmd.json` example:

```json
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
```

Comment example:

```json
{
  "type": "AddComment",
  "issueId": "1",
  "comment": "Blocked by API contract review",
  "actor": "gpt-5.2"
}
```

PowerShell tip: prefer `--file` or stdin here-string over inline `--json` to avoid escaping problems.
If actor is omitted for `UpdateDescription`/`AddComment`, default resolution order is `--actor`, then env vars (`MADDOX_TASKS_AGENT_ACTOR`, `MADDOX_TASKS_ACTOR`, `CODEX_MODEL`, `OPENAI_MODEL`, `ANTHROPIC_MODEL`, `CLAUDE_MODEL`, `MODEL`), then Claude settings (`.claude/settings.local.json`, `.claude/settings.json`, `~/.claude/settings.json` model), then Codex config (`~/.codex/config.toml` model + reasoning effort), then `agent`.

`agent issues` includes a deterministic `repositories` array derived from `repo:` labels. `agent claim` atomically selects the highest-priority available `Next` issue (sequence breaks ties), changes it to `Active`, and prints the selected issue JSON. It prints `null` when no repository-backed issue is available; scheduled runners should stop for that hour. `--dry-run` performs the same selection without changing the database.

After its claim/work loop, the hourly runner checks `ReadyForReview` tasks using only PR URLs found in their descriptions and comments. A review task with no PR URL stays open. A task closes automatically only when every associated PR reports a non-null `mergedAt` from `gh pr view`; open, closed-unmerged, lookup-error, and ambiguous cases remain `ReadyForReview` with a warning. Preview mode reports intended checks without calling `gh` or mutating tasks.

The deterministic reconciliation can also be run directly as machine-readable JSON:

```powershell
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --gh-timeout-seconds 45
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --dry-run
```

It extracts canonical GitHub pull-request URLs from each `ReadyForReview` description and comment, deduplicates them, checks `mergedAt`, and conditionally changes only still-`ReadyForReview` tasks to `Done`. No-PR, unmerged, lookup-error, and concurrent-state outcomes remain unchanged; processing continues across tasks.

For hourly Windows automation, preview or install the versioned scripts (installation does not run the scheduled task immediately):

```powershell
.\scripts\run-reserved-task.ps1 -MaddoxExe F:\MaddoxTasks\MaddoxTasks.exe -RepoRoot D:\code -Preview
.\scripts\install-reserved-task.ps1 -MaddoxExe F:\MaddoxTasks\MaddoxTasks.exe -RepoRoot D:\code
```

The runner validates every repository in a claim under `RepoRoot`, opens Codex in the first repository, and grants each additional repository with repeatable `--add-dir` arguments. An isolated regression check uses fake Maddox/Codex/GitHub commands and does not access the live database:

```powershell
.\scripts\tests\run-reserved-task-multi-repo.tests.ps1
```

## Where Data Is Stored

Default DB file is `MaddoxTasks.db`.

- Windows: `%OneDrive%\MaddoxTasks\MaddoxTasks.db` (fallback `%LOCALAPPDATA%\MaddoxTasks\MaddoxTasks.db`)
- macOS: `~/Library/Application Support/MaddoxTasks/MaddoxTasks.db`
- Linux: `$XDG_DATA_HOME/MaddoxTasks/MaddoxTasks.db` (fallback `~/.local/share/MaddoxTasks/MaddoxTasks.db`)

Override with `MaddoxTasks.json` in current directory, app directory, or OS config directory:

```json
{
  "databasePath": "D:\\Data\\MaddoxTasks.db"
}
```

Template file: `MaddoxTasks.json.example`

## How It Works (Technical, Secondary)

- Event-sourced core
- SQLite append-only event log
- Shared command pipeline for TUI, CLI, and agent JSON commands
- Spectre.Console TUI

## Source/Dev Commands

Run from source:

```bash
dotnet run
```

Build:

```bash
dotnet build
```

Test:

```bash
dotnet test .\tests\MaddoxTasks.Tests\MaddoxTasks.Tests.csproj
```

Publish single-file Native AOT (Windows default output `F:\MaddoxTasks`):

```powershell
.\scripts\publish.ps1 -Runtime win-x64
```

Publish without AOT:

```powershell
.\scripts\publish.ps1 -Runtime win-x64 -NoAot
```

## CI and Releases

- `CI` workflow runs build + tests on pushes to `main` and PRs.
- `CI` also publishes Native AOT binaries for `win-x64`, `linux-x64`, and `osx-arm64`, then runs `scripts/validate-published-agent.ps1` against each binary. The smoke gate creates and reads a task through only the published `agent` JSON surface using an isolated temporary SQLite database, catching missing native SQLite dependencies and command-surface drift.
- `Release` runs the same published-agent smoke gate for every runtime before packaging its zipped binary.
- Stable releases: tags matching `v*` (example: `v0.2.0`).
- Nightly prereleases: created only if code changed since the previous nightly tag.

## Agent Skill Directory

- `skills/maddox-tasks/SKILL.md`
- `skills/maddox-tasks/agents/openai.yaml`
- `skills/maddox-tasks/references/commands.md`
