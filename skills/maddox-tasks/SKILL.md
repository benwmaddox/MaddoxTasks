---
name: maddox-tasks
description: Operate and automate Maddox Tasks via agent JSON commands. Use when users ask to list/create/update tasks in machine-readable workflows, execute agent command payloads, diagnose database path behavior (MaddoxTasks.db defaults and MaddoxTasks.json overrides), or validate CI/release behavior for MaddoxTasks.
---

# Maddox Tasks Skill

Run Maddox Tasks with the released binary first when available.

## Execution Mode (Important)

1. Use **only agent JSON commands** when operating as an agent.
2. Treat `list tasks` as `agent issues` (machine-readable JSON).
3. Do not run non-agent CLI commands (`list`, `create`, `status`, `priority`, `label`, `describe`, `comment`, `summary`) as part of this skill.
4. If the user uses skill-style prompts such as `$maddox-tasks ...`, map them to `agent` subcommands.

## Use Released Binary

1. Use `MaddoxTasks.exe` on Windows or `./MaddoxTasks` on Linux/macOS.
2. For this skill's workflow, execute only `agent` subcommands.

Read `references/commands.md` for concrete command patterns.

## Database Rules

1. Assume default DB filename is `MaddoxTasks.db`.
2. On Windows, default to `%OneDrive%\MaddoxTasks\MaddoxTasks.db` with `%LOCALAPPDATA%\MaddoxTasks\MaddoxTasks.db` fallback.
3. On macOS, default to `~/Library/Application Support/MaddoxTasks/MaddoxTasks.db`.
4. On Linux, default to `$XDG_DATA_HOME/MaddoxTasks/MaddoxTasks.db` with `~/.local/share/MaddoxTasks/MaddoxTasks.db` fallback.
5. Check for `MaddoxTasks.json` overrides before assuming defaults.

## Agent Command Workflow

1. Use `agent issues` to read current state as JSON.
For "list tasks", use `agent issues` by default.
2. Build structured command JSON (`CreateIssue`, `ChangeStatus`, `ChangePriority`, `AddLabel`, `RemoveLabel`, `UpdateDescription`, `AddComment`).
For `UpdateDescription` and `AddComment`, use `"actor": "agent"` when acting as an automation.
3. Execute with `agent command --file <json-file>` or `--json <payload>`.
4. Re-read with `agent issues` and verify deterministic output.

## Release and CI Workflow

1. Use tag `v*` (for example `v0.2.0`) for stable releases.
2. Expect nightly prereleases only when code changed since last nightly tag.
3. Use `gh run list` and `gh release view <tag>` to verify CI/release state.
