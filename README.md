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
.\MaddoxTasks.exe summary week
```

Linux/macOS:

```bash
./MaddoxTasks create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
./MaddoxTasks list --status Active
./MaddoxTasks status 1 Done
./MaddoxTasks label 1 architecture
./MaddoxTasks summary week
```

Issue tokens support:

- Sequence (`1`, `2`, ...)
- Full GUID
- GUID prefix

Tip: in TUI, press `Enter` on an issue to open details and edit description inline. The description input uses your terminal's native line editor.

## Agent JSON Commands

Windows:

```powershell
.\MaddoxTasks.exe agent issues
.\MaddoxTasks.exe agent command --file cmd.json
```

Linux/macOS:

```bash
./MaddoxTasks agent issues
./MaddoxTasks agent command --file cmd.json
```

`cmd.json` example:

```json
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
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

Publish single-file (Windows default output `F:\MaddoxTasks`):

```powershell
.\scripts\publish.ps1 -Runtime win-x64
```

## CI and Releases

- `CI` workflow runs build + tests on pushes to `main` and PRs.
- `Release` workflow publishes zipped binaries.
- Stable releases: tags matching `v*` (example: `v0.2.0`).
- Nightly prereleases: created only if code changed since the previous nightly tag.

## Agent Skill Directory

- `skills/maddox-tasks/SKILL.md`
- `skills/maddox-tasks/agents/openai.yaml`
- `skills/maddox-tasks/references/commands.md`
