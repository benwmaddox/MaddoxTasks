# Agent Guidelines

## Checkout policy

- Use the canonical `D:\code\Tasks\MaddoxTasks` checkout for normal development and keep at most one active MaddoxTasks task in it.
- Create and switch task branches in the canonical checkout. Do not create or move work into a worktree for routine isolation, parallelism, or to bypass a dirty or occupied checkout.
- Worktrees are an exceptional fallback. Use one only when the user explicitly requests it or a documented technical constraint makes the canonical checkout unusable. Keep it temporary and remove it after the work is safely integrated.
- Preserve existing worktrees unless ownership and cleanup state are proven. Their existence does not justify creating another.
- Delegated agents share the canonical checkout with explicit, non-overlapping ownership.

## Worker policy

- The shipped worker defaults to `workspaceMode: "checkout"` and relies on Maddox repository reservations to admit at most one task per repository.
- A canonical checkout must be clean and on its remote default branch before the worker claims it. Do not bypass an occupied checkout automatically.
- Before every new task, fetch and prune `origin` and base the task branch on the freshly fetched `origin/main` or `origin/master`, not a stale local branch.
- Set `workspaceMode` to `"worktree"` only as an explicit operational exception after recording why canonical checkout operation is not viable.
