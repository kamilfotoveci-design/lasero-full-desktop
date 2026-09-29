---
name: lasero-worktree-repair
description: >-
  Use when lasero-lead needs to delegate an isolated implementation task to a
  specialist agent using a git worktree branch to prevent file conflicts. Covers
  how to create the worktree, assign the task, collect the result, and
  integrate it.
---

# Lasero Worktree Repair Workflow

Use this workflow for any implementation task that modifies source files, to prevent
conflicts with parallel agents working on other domains.

## When to use isolated worktrees

Use a worktree for:
- Any change to `MainWindow.xaml` / `MainWindow.xaml.cs`
- Any change to `ViewModels/MainViewModel.cs`
- Any change to `LaseroTheme.xaml` / `SharedUiStyles.xaml`
- Any change that touches files owned by more than one domain
- Parallel repairs being worked by 2+ agents simultaneously

Use shared workspace for:
- Read-only audits
- Test runs
- Single-agent repairs to fully isolated files

## Creating a worktree branch

The lead agent creates a named branch for the subagent:

```powershell
git -C E:\lasero-desktop worktree add E:\lasero-desktop-worktrees\<agent>-<task> -b <agent>/<task>
```

Example:
```powershell
git -C E:\lasero-desktop worktree add E:\lasero-desktop-worktrees\shell-alarm-banner -b shell/alarm-banner
```

## Assigning the task

When invoking a subagent with a worktree:
- Pass the worktree path as the project root
- Pass the branch name
- Give the agent its full ownership context from `AGENTS.md`
- State exactly what to change and what NOT to change

## Collecting the result

The subagent must return:
- Files changed (list of paths)
- Behavior changed (what it does differently)
- Tests run + result
- Known risks or open questions

## Integration review checklist

Before merging a worktree branch:

```powershell
# Build the worktree
dotnet build <worktree-path>\LaseroDesktop.sln

# Test the worktree
dotnet test <worktree-path>\LaseroDesktop.sln

# Review diff
git -C <worktree-path> diff main...HEAD
```

Answer these before merging:
1. ✅ 0 build errors, 0 warnings?
2. ✅ All tests pass?
3. ✅ No duplicate state introduced?
4. ✅ No fake machine/connection/job state?
5. ✅ No preflight gate weakened?
6. ✅ Diff is minimal and focused?

## Merging

```powershell
git -C E:\lasero-desktop merge <agent>/<task> --no-ff -m "Merge: <description>"
```

## Cleanup

```powershell
git -C E:\lasero-desktop worktree remove E:\lasero-desktop-worktrees\<agent>-<task>
git -C E:\lasero-desktop branch -d <agent>/<task>
```
