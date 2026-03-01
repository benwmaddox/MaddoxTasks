---
name: maddox-tasks
description: Operate and automate Maddox Tasks via agent JSON commands. Use when users ask to list/create/update tasks in machine-readable workflows, execute agent command payloads, diagnose database path behavior (MaddoxTasks.db defaults and MaddoxTasks.json overrides), or validate CI/release behavior for MaddoxTasks.
---

# Maddox Tasks Skill

Run Maddox Tasks with the published/released binary, never source internals, for agent operations.

## Execution Mode (Important)

1. Use **only agent JSON commands** when operating as an agent.
2. Treat `list tasks` as `agent issues` (machine-readable JSON).
3. Treat `next task` requests as `agent next` (machine-readable JSON for one selected task).
4. Do not run non-agent CLI commands (`list`, `create`, `status`, `priority`, `label`, `describe`, `comment`, `summary`) as part of this skill.
5. If the user uses skill-style prompts such as `$maddox-tasks ...`, map them to `agent` subcommands.

## Use Released Binary

1. Always use the published binary: `MaddoxTasks.exe` on Windows or `./MaddoxTasks` on Linux/macOS.
2. Do not invoke project source internals (for example direct library calls) for task operations.
3. For this skill's workflow, execute only `agent` subcommands.

Read `references/commands.md` for concrete command patterns.

## Database Rules

1. Assume default DB filename is `MaddoxTasks.db`.
2. On Windows, default to `%OneDrive%\MaddoxTasks\MaddoxTasks.db` with `%LOCALAPPDATA%\MaddoxTasks\MaddoxTasks.db` fallback.
3. On macOS, default to `~/Library/Application Support/MaddoxTasks/MaddoxTasks.db`.
4. On Linux, default to `$XDG_DATA_HOME/MaddoxTasks/MaddoxTasks.db` with `~/.local/share/MaddoxTasks/MaddoxTasks.db` fallback.
5. Check for `MaddoxTasks.json` overrides before assuming defaults.
6. Never read or write the database file directly (no raw SQL, sqlite shell, or file edits). Interact only through published-binary `agent` commands.

## Agent Command Workflow

1. Use `agent issues` to read current state as JSON.
For "list tasks", use `agent issues` by default.
For "next task", use `agent next` (selection policy: priority ascending across `Active`/`Next`, then `Active` before `Next`, then sequence).
2. Build structured command JSON (`CreateIssue`, `ChangeStatus`, `ChangePriority`, `AddLabel`, `RemoveLabel`, `UpdateDescription`, `AddComment`).
For `UpdateDescription` and `AddComment`, set `"actor"` to the exact model identifier (for example `"gpt-5.3-codex high"` or `"claude-sonnet"`), or pass `--actor <model-id>` on `agent command`. If actor is omitted, auto-detection uses env vars first, then Claude settings, then Codex config.
3. Execute with `agent command --file <json-file>` or stdin. Treat inline `--json` as last-resort on PowerShell.
4. Re-read with `agent issues` and verify deterministic output.

## Task Lifecycle Expectations

1. When beginning work on a task, if the task status is `Next`, change it to `Active` before doing implementation work.
2. While working, record decision points as comments using `AddComment`.
3. When implementation is complete, change the task status to `Ready for Review`.

## Git Workflow

1. For any code or configuration change, create a new branch before making edits.
2. Commit changes to that branch with clear commit messages.
3. Open a pull request for review; do not treat direct commits to the main branch as complete work.

## Release and CI Workflow

1. Use tag `v*` (for example `v0.2.0`) for stable releases.
2. Expect nightly prereleases only when code changed since last nightly tag.
3. Use `gh run list` and `gh release view <tag>` to verify CI/release state.
