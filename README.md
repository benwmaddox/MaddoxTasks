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
.\MaddoxTasks.exe create "Fix reload bug" --priority 2 --description "Investigate cache invalidation" --status Next
.\MaddoxTasks.exe create "Keep in backlog" --status Backlog
.\MaddoxTasks.exe list --status Active
.\MaddoxTasks.exe status 1 Done
.\MaddoxTasks.exe label 1 architecture
.\MaddoxTasks.exe comment 1 "Waiting on security review"
.\MaddoxTasks.exe summary week
```

Linux/macOS:

```bash
./MaddoxTasks create "Fix reload bug" --priority 2 --description "Investigate cache invalidation" --status Next
./MaddoxTasks create "Keep in backlog" --status Backlog
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

## LAN Web UI (Desktop and Mobile)

Run the server alongside the CLI or TUI when a browser is more convenient:

Windows:

```powershell
.\MaddoxTasks.exe serve
.\MaddoxTasks.exe serve --host 0.0.0.0 --port 5000 --db D:\Data\MaddoxTasks.db
```

Linux/macOS:

```bash
./MaddoxTasks serve
./MaddoxTasks serve --host 0.0.0.0 --port 5000 --db /data/MaddoxTasks.db
```

The default bind address is `0.0.0.0` (all interfaces) and the default port is
`5000`. On startup the server prints a localhost URL and the LAN IPv4 URLs it
finds. Open one of those URLs on the desktop or a phone/tablet on the same
network. If the device cannot connect, allow the selected TCP port through the
computer's firewall and check that the network is not isolating wireless
clients. Use `--host 127.0.0.1` when the UI should remain local to the computer.

The server intentionally has no authentication in this first iteration. Bind
to a trusted, private network only, do not forward the port to the internet,
and stop the server when it is not needed. The browser UI reads and writes the
same event-sourced SQLite database as the CLI/TUI; the existing atomic store
keeps concurrent processes safe. Changes made on another device appear after
the automatic refresh (or press the refresh button / `r`).

The board groups issues by status and includes search, status/priority filters,
issue creation (including parent and due date), status and priority editing,
description, labels, comments, and history. Issues within each status column are
ordered by priority (P1 first) and then issue sequence. Repository labels are
shown as distinct `Repository: <name>` tags, and the Repository locks button lists
the Active and Ready for Review task blocking each reserved repository. Every
mutation has a visible button and touch targets are sized for mobile use. Keyboard
shortcuts are:

- `Up`/`Down` or `j`/`k`: navigate issues; `Enter`: open the selected issue
- `n`: new issue; `s`: status; `p`: priority; `t`: labels
- `d`: mark the selected issue done (or focus description in detail); `c`: comment
- `/`: search; `r`: refresh; `?`: help; `Esc`: close the current panel

Shortcuts are ignored while typing in a form. The JSON API is available under
`/api/issues` for clients that need the same browser operations. The 10-second
automatic and manual refresh preserve unsaved description, label, and comment
drafts along with focus, selection, and scroll position. A successful mutation
reloads server state while clearing only the submitted field. Current reservation
blockers are also available from `/api/repository-locks`.

Statuses are `Backlog`, `Next`, `Active`, `Blocked`, `ReadyForReview`, `Done`, and `Rejected`. `Done` and `Rejected` are terminal statuses; open views hide both by default, while `--include-done` includes all terminal tasks (the option name is retained for compatibility).

New issues default to `Next` across the CLI, TUI, and agent command surfaces. Use the explicit `--status Backlog` CLI/TUI choice or `"status": "Backlog"` agent field when a new issue should remain in Backlog. Existing issues are not migrated or reordered.

Active and `ReadyForReview` work reserve repository scopes using labels with the canonical `repo:<name>` prefix, for example `repo:StasisLang` (repository names are compared case-insensitively). An issue with no repository labels reserves the synthetic identity `missing`, so only one repository-less issue can hold a reserving status at a time. This does not add a `repo:missing` label: its public `repositories` array remains empty. An explicit `repo:missing` label collides with the synthetic identity. Moving to another status releases the reservation. Repository labels may be added or removed while work is reserving only when the resulting scope is free.

## Agent JSON Commands

Windows:

```powershell
.\MaddoxTasks.exe agent issues
.\MaddoxTasks.exe agent next
.\MaddoxTasks.exe agent claim
.\MaddoxTasks.exe agent claim --dry-run
.\MaddoxTasks.exe agent research-claim
.\MaddoxTasks.exe agent research-claim --dry-run
.\MaddoxTasks.exe agent command --file cmd.json
.\MaddoxTasks.exe agent command --actor gpt-5.2 --file cmd.json
```

Linux/macOS:

```bash
./MaddoxTasks agent issues
./MaddoxTasks agent next
./MaddoxTasks agent claim
./MaddoxTasks agent claim --dry-run
./MaddoxTasks agent research-claim
./MaddoxTasks agent research-claim --dry-run
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

Atomically replace only an issue's repository labels while preserving every other label:

```json
{
  "type": "SetRepositoryLabels",
  "issueId": "1",
  "repositories": ["StasisLang", "MaddoxTasks"]
}
```

The repository list must be nonempty. Names are normalized and deduplicated case-insensitively, and the entire command is rejected without changes if any requested repository is reserved by another `Active` or `ReadyForReview` issue.

Atomically replace a coordination issue with one independently executable child per repository:

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

A split requires at least two children. Every child inherits the parent priority, starts in `Next`, references the source as its parent, and owns exactly one repository that no sibling may repeat case-insensitively. On success the source becomes `Done`. Validation, child creation, repository-label assignment, and source completion are one transaction; any failure writes nothing.

Requeue every currently `Blocked` issue to `Next` in one atomic command:

```json
{
  "type": "RequeueBlocked",
  "dryRun": true
}
```

`dryRun` is an optional boolean that defaults to `false`. Preview and apply both return `changedIssueIds` (the Blocked issues that would change or changed) and `skippedIssueIds` (every other issue in the same snapshot), with both arrays ordered by issue sequence. Preview writes nothing. Apply appends only `StatusChanged` events and commits all changes together; an empty or repeated run succeeds without writing events. All task content, labels, repositories, comments, priority, and hierarchy remain unchanged.

`agent research-claim` selects one eligible `Blocked` task and records its durable research-attempt comment. Use
`--dry-run` to preview without recording the marker, or `--cooldown 14.00:00:00` to override the positive cooldown.
The selection uses hierarchy priority/sequence order and skips a task whose latest marker is newer than the cooldown.
The worker's internal `CompleteResearch` command is conditional: it requires the research marker and changes the
source from `Blocked` to `Next` only if the source is still `Blocked`, so a human status change cannot be overwritten.

PowerShell tip: prefer `--file` or stdin here-string over inline `--json` to avoid escaping problems.
If actor is omitted for `UpdateDescription`/`AddComment`, default resolution order is `--actor`, then env vars (`MADDOX_TASKS_AGENT_ACTOR`, `MADDOX_TASKS_ACTOR`, `CODEX_MODEL`, `OPENAI_MODEL`, `ANTHROPIC_MODEL`, `CLAUDE_MODEL`, `MODEL`), then Claude settings (`.claude/settings.local.json`, `.claude/settings.json`, `~/.claude/settings.json` model), then Codex config (`~/.codex/config.toml` model + reasoning effort), then `agent`.

`agent issues` includes a deterministic `repositories` array derived from `repo:` labels. `agent claim` and `agent next` order work hierarchically: task families are ordered by their top-level root priority and sequence, then each family is traversed child-first with siblings ordered by priority and sequence. For `agent next`, `Active` is only a tiebreaker among otherwise equal-priority siblings or roots; it never overrides descendant-before-ancestor traversal. A child is considered using only its own reservation keys, so a reserved parent does not block a disjoint child, and a reserved child does not block a later claimable sibling. Before selecting, `agent claim` automatically resets any `Active` task whose latest mutation is at least 24 hours old (24 hours with no activity) and which has a current-period comment beginning `Reservation owner: codexThreadId=` with a nonempty value (including `unavailable`). The reset writes a `Next` status event and an audit comment, in issue-sequence order, then atomically selects the first eligible `Next` issue whose reservation keys are not held by `Active` or `ReadyForReview` work. A repository-less task is eligible and uses the synthetic `missing` reservation while still returning `repositories: []`. Historical reservation comments from earlier Active periods, non-Codex reservations, young tasks, and `ReadyForReview` tasks are not reset. Cleanup is persisted even when no claim is available. `agent next` reports the first `Active` or `Next` issue in the same hierarchy order. Both commands handle missing or cyclic parent links deterministically. Claim prints `null` when no issue is available; scheduled runners should stop for that hour. `--dry-run` simulates stale cleanup and selection, reports the preview issue as `Next`, and writes no events. For a repository-less claim, `scripts/run-reserved-task.ps1` starts Codex in the normalized `RepoRoot`, passes no `--add-dir`, and warns that the impact scope is unknown.

The worker also reserves at most one shared Codex slot for blocked-task research. `agent research-claim` atomically
selects one eligible `Blocked` task in the same hierarchy order and records a durable
`maddox-research-worker` comment marker. A task with a marker newer than the configured
`researchCooldown` (14 days by default) is skipped, so concurrent or recurring runs do not
research the same task too often. The research Codex process runs read-only and may perform
read-only web research, but it cannot mutate files, Git/GitHub, or external services. Its
validated result may only change Maddox task entries (including creating tasks). Findings are
recorded as a task comment; only after all task-entry mutations succeed does the worker use an
atomic `CompleteResearch` transition that changes the original task from `Blocked` to `Next`
if it is still `Blocked`. If it is still blocked, the findings remain on the task and its
status remains unchanged.

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

The Windows release also includes `MaddoxTasks.Worker.exe`, `worker.json`, and `worker-prompt.md`. Register the interactive at-logon worker (or validate the registration without changing Task Scheduler) with:

```powershell
.\scripts\install-worker-task.ps1 -BinaryDir F:\MaddoxTasks -DryRun
.\scripts\install-worker-task.ps1 -BinaryDir F:\MaddoxTasks
```

On startup, the worker claims at most one fresh task immediately, then attempts
one additional fresh claim per `capacityFillInterval` (one minute by default)
while capacity remains. Reaching the concurrency cap or receiving an empty or
failed claim ends this startup ramp; subsequent capacity openings do not restart
it. Afterward, fresh claims return to the normal `claimInterval` cadence, still
at most one per tick. Follow-up work remains immediate and takes priority. Set
`maxConcurrentCodexProcesses` to `0` to pause new Codex work while keeping PR
monitoring and reconciliation active; running Codex processes drain naturally.
Raise the value to resume. Run `MaddoxTasks.Worker.exe --stop` for an orderly
local shutdown. The visible dashboard keeps recently blocked work for
`blockedDisplayDuration` (10 minutes by default), then rolls it off while the
durable journal and JSONL logs retain the full record.
Blocked jobs retain their owned worktrees and branches, including tracked changes
and non-ignored untracked files, for diagnosis or later recovery. Destructive
worktree and branch cleanup is eligible only after the job reaches `Done`;
best-effort ignored generated-output cleanup may still run with `git clean -fdX`.

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
