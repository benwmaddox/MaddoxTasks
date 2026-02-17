# Commands Reference

## Start TUI

Windows:

```powershell
.\MaddoxTasks.exe
```

Linux/macOS:

```bash
./MaddoxTasks
```

## Core CLI Operations

Windows:

```powershell
.\MaddoxTasks.exe create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
.\MaddoxTasks.exe list --status Active
.\MaddoxTasks.exe status 1 Done
.\MaddoxTasks.exe priority 1 1
.\MaddoxTasks.exe label 1 architecture
.\MaddoxTasks.exe describe 1 "Updated description"
.\MaddoxTasks.exe comment 1 "Waiting on CI"
.\MaddoxTasks.exe summary week
```

Linux/macOS:

```bash
./MaddoxTasks create "Fix reload bug" --priority 2 --description "Investigate cache invalidation"
./MaddoxTasks list --status Active
./MaddoxTasks status 1 Done
./MaddoxTasks priority 1 1
./MaddoxTasks label 1 architecture
./MaddoxTasks describe 1 "Updated description"
./MaddoxTasks comment 1 "Waiting on CI"
./MaddoxTasks summary week
```

## Agent JSON Interface

```bash
./MaddoxTasks agent issues
./MaddoxTasks agent command --file cmd.json
```

`cmd.json`:

```json
{
  "type": "ChangeStatus",
  "issueId": "1",
  "newStatus": "Active"
}
```

Add a comment:

```json
{
  "type": "AddComment",
  "issueId": "1",
  "comment": "Blocked on dependency upgrade",
  "actor": "agent"
}
```
