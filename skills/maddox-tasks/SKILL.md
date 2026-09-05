---
name: maddox-tasks
description: Operate and automate Maddox Tasks via agent JSON commands. Use when users ask to list, create, update, bulk-requeue, claim, or research tasks; diagnose database path behavior; or validate MaddoxTasks CI and releases.
---

# Maddox Tasks Skill

Run Maddox Tasks with the published/released binary, never source internals, for agent operations.

## Execution Mode (Important)

1. Use **only agent JSON commands** when operating as an agent.
2. Treat `list tasks` as `agent issues` (machine-readable JSON).
3. Do not run non-agent CLI commands (`list`, `create`, `status`, `priority`, `label`, `describe`, `comment`, `summary`) as part of this skill.
4. If the user uses skill-style prompts such as `$maddox-tasks ...`, map them to `agent` subcommands.

## Text Guidelines (ASCII Preference)

1. Prefer ASCII characters in all agent-generated text (comments, titles, descriptions) where possible.
2. Avoid curly quotes, em dashes, and other non-ASCII punctuation that can break terminals/tools.
3. Use ASCII equivalents: `"` and `'`, `--`, and `...`.

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
2. Build structured command JSON (`CreateIssue`, `ChangeStatus`, `ChangePriority`, `AddLabel`, `RemoveLabel`, `SetRepositoryLabels`, `SplitIssue`, `UpdateDescription`, `AddComment`, `RequeueBlocked`). Repository reservations are labels in the canonical form `repo:<name>`; repository identity is case-insensitive. Use `SetRepositoryLabels` to atomically replace repository scope while preserving unrelated labels.
   - For `CreateIssue`, omit `status` to store `Next` (the default), or set `status` explicitly to `Backlog`; other initial statuses are rejected. Successful command responses include the final stored `status`.
   - For `RequeueBlocked`, set optional boolean `dryRun` to `true` to preview the atomic move of every currently `Blocked` issue to `Next`. Prefer this single transactional command over issuing one `ChangeStatus` command per task. The response reports sequence-ordered changed and skipped GUIDs; preview writes nothing.
   - For blocked-task research, use `agent research-claim` (or `agent research-claim --dry-run`) to atomically select one eligible `Blocked` task. Its exact `maddox-research-worker` comment marker is the durable attempt record; the configured `researchCooldown` defaults to 14 days and prevents another claim for that task during the cooldown. The worker gives the research Codex a read-only snapshot of only the selected task, permits read-only web research, and validates a closed set of Maddox task-entry mutations before applying them. The internal `CompleteResearch` command can move the source to `Next` only when its research marker exists and the source is still `Blocked`; it cannot overwrite a human status change.
   - For `UpdateDescription` and `AddComment`, set `"actor"` to the exact runtime model identifier, or pass `--actor <model-id>` on `agent command`. If actor is omitted, auto-detection uses environment variables first, then Claude settings, then Codex config.
3. Execute with `agent command --file <json-file>` or stdin. Treat inline `--json` as last-resort on PowerShell.
4. Re-read with `agent issues` and verify deterministic output.

## Repository Reservations and Claims

`agent issues` returns a deterministic `repositories` array for every issue. To safely run concurrent scheduled workers, use `agent claim` instead of selecting a `Next` issue from a read-only listing:

```powershell
.\MaddoxTasks.exe agent claim
.\MaddoxTasks.exe agent claim --dry-run
```

`agent claim` and `agent next` use deterministic hierarchy ordering. Task families are ordered by top-level root priority and sequence. Within a family, descendants are visited child-first and siblings are ordered by priority and sequence; a parent is considered only after its descendants. For `agent next`, `Active` is only a tiebreaker among otherwise equal-priority siblings or roots; it never overrides descendant-before-ancestor traversal. Missing or cyclic parent links are handled without unbounded recursion. Before selection, `agent claim` atomically resets stale Codex reservations: an `Active` issue with `UpdatedAt` at least 24 hours before the single normalized claim time (24 hours with no activity) and a current-period comment beginning exactly `Reservation owner: codexThreadId=` with a nonempty value (including `unavailable`) is changed to `Next` and receives an agent audit comment. Reset order is issue sequence; the latest `StatusChanged` to `Active` defines the current period, so older reservation comments do not qualify. No other Active task, young task, or ReadyForReview task is reset. Cleanup is committed even when no claim candidate remains. It then atomically selects one eligible `Next` issue whose reservation keys are not held by `Active` or `ReadyForReview` issues. Real repositories use normalized names; an issue with no `repo:` label uses the synthetic reservation key `missing` and remains claimable while returning `repositories: []`. An explicit `repo:missing` label conflicts with that synthetic key. Eligibility and conflicts are evaluated per selected issue: a reserved parent does not block a disjoint child, and a reserved child does not block a later claimable sibling. It changes the selected issue to `Active` and returns its issue JSON. `agent next` reports the first `Active` or `Next` issue in the same hierarchy order. Claim returns JSON `null` when no issue is available; stop the worker for that invocation. Dry-run simulates stale cleanup and selection but appends no events; its preview issue remains `Next`. Manual status and label changes enforce the same reservation-key rule. Moving to another status releases the reservation. The runner executes repository-less claims from `RepoRoot`, passes no `--add-dir`, and identifies the impact scope as unknown.

The hourly runner reconciles `ReadyForReview` tasks after work. It associates only canonical GitHub PR URLs in the task description/comments, and closes a task only when every associated PR has a non-null `mergedAt` from `gh pr view`. No-PR tasks and any open/closed-unmerged/error cases remain open. Preview mode is read-only and skips live `gh` calls.

Use the deterministic binary command for this reconciliation:

```powershell
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --gh-timeout-seconds 45
.\MaddoxTasks.exe agent reconcile-reviews --gh-exe gh --dry-run
```

The command emits one JSON result containing per-task outcomes (`closed`, `noPullRequests`, `unmerged`, `lookupError`, `concurrentStateChange`, or `dryRun`) and continues after individual lookup failures. Only MaddoxTasks performs the conditional status transition; PowerShell should not duplicate URL parsing, GitHub lookup, or state mutation logic.

## Task Lifecycle Expectations

1. A scheduled worker beginning its next task uses `agent claim`; it does not select or activate a task separately. When a user names a task, operate on that task and do not claim an unrelated one.
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
