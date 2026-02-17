# Maddox Tasks

Deterministic personal task engine with:

- Event-sourced core
- SQLite append-only event log
- Shared command pipeline for TUI, CLI, and agent JSON commands
- Spectre.Console terminal UI

## Run

```bash
dotnet run
```

No command parameters are required. Without a command it starts the TUI and reuses the same default DB.

Default DB path:

- Windows: `%OneDrive%\MaddoxTasks\MaddoxTasks.db` (fallback: `%LOCALAPPDATA%\MaddoxTasks\MaddoxTasks.db`)
- macOS: `~/Library/Application Support/MaddoxTasks/MaddoxTasks.db`
- Linux: `$XDG_DATA_HOME/MaddoxTasks/MaddoxTasks.db` (fallback: `~/.local/share/MaddoxTasks/MaddoxTasks.db`)

Override with `MaddoxTasks.json` in current directory, app directory, or OS config directory:

```json
{
  "databasePath": "D:\\Data\\MaddoxTasks.db"
}
```

A ready template is included at `MaddoxTasks.json.example`.

## Run Compiled Binary

From GitHub Releases:

1. Download the zip for your platform from the latest release.
2. Extract it.
3. Run:

Windows:

```powershell
.\MaddoxTasks.exe
```

Linux/macOS:

```bash
./MaddoxTasks
```

No parameters are required. The app will keep using the same default `MaddoxTasks.db` path between runs.

## CLI examples

```bash
dotnet run -- create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
dotnet run -- list --status Active
dotnet run -- status 1 Done
dotnet run -- label 1 architecture
dotnet run -- summary week
```

Issue tokens accept:

- Sequence (`1`, `2`, ...)
- Full GUID
- GUID prefix

## Agent interface

Get issues as JSON:

```bash
dotnet run -- agent issues
```

Execute structured command JSON:

```bash
dotnet run -- agent command --file cmd.json
```

`cmd.json` example:

```json
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
```

## Build

```bash
dotnet build
```

## Test

```bash
dotnet test .\tests\MaddoxTasks.Tests\MaddoxTasks.Tests.csproj
```

## Publish (single-file)

```powershell
.\scripts\publish.ps1 -Runtime win-x64
```

Default output is `F:\MaddoxTasks`.

## CI and Releases

- `CI` workflow runs build + tests on pushes to `main` and pull requests.
- `Release` workflow builds zipped binaries and publishes GitHub Releases.
- Nightly releases run daily, and publish only if code changed since the last nightly tag.
- Stable releases are built from tags matching `v*` (for example `v0.2.0`).

