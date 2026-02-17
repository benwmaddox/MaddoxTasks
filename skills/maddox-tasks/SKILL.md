---
name: maddox-tasks
description: Operate and automate Maddox Tasks from source or released binaries. Use when users ask to create/list/update tasks, run TUI/CLI commands, use JSON agent commands, diagnose database path behavior (MaddoxTasks.db defaults and MaddoxTasks.json overrides), or validate CI/release behavior for MaddoxTasks.
---

# Maddox Tasks Skill

Run Maddox Tasks with the released binary first when available.

## Use Released Binary

1. Use `MaddoxTasks.exe` on Windows or `./MaddoxTasks` on Linux/macOS.
2. Run without arguments to start TUI.
3. Run CLI commands directly for deterministic automation.

Read `references/commands.md` for concrete command patterns.

## Database Rules

1. Assume default DB filename is `MaddoxTasks.db`.
2. On Windows, default to `%OneDrive%\MaddoxTasks\MaddoxTasks.db` with `%LOCALAPPDATA%\MaddoxTasks\MaddoxTasks.db` fallback.
3. On macOS, default to `~/Library/Application Support/MaddoxTasks/MaddoxTasks.db`.
4. On Linux, default to `$XDG_DATA_HOME/MaddoxTasks/MaddoxTasks.db` with `~/.local/share/MaddoxTasks/MaddoxTasks.db` fallback.
5. Check for `MaddoxTasks.json` overrides before assuming defaults.

## Agent Command Workflow

1. Use `agent issues` to read current state as JSON.
2. Build structured command JSON (`CreateIssue`, `ChangeStatus`, `ChangePriority`, `AddLabel`, `RemoveLabel`, `UpdateDescription`).
3. Execute with `agent command --file <json-file>` or `--json <payload>`.
4. Re-read with `agent issues` and verify deterministic output.

## Release and CI Workflow

1. Use tag `v*` (for example `v0.2.0`) for stable releases.
2. Expect nightly prereleases only when code changed since last nightly tag.
3. Use `gh run list` and `gh release view <tag>` to verify CI/release state.
