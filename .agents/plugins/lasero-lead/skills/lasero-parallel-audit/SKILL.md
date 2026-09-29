---
name: lasero-parallel-audit
description: >-
  Use when lasero-lead needs to run a full parallel domain audit of the Lasero
  desktop application before making changes. Activates read-only investigation
  across all 6 specialist domains simultaneously, then synthesizes a
  P0/P1/P2/P3 repair plan.
---

# Lasero Parallel Audit Workflow

Run this skill at the start of any multi-domain task or when the codebase state is uncertain.

## Step 1 — Baseline verification (before spawning any subagents)

```powershell
dotnet build E:\lasero-desktop\LaseroDesktop.sln --configuration Debug
dotnet test E:\lasero-desktop\LaseroDesktop.sln
```

Record: errors, warnings, test count, pass/fail.

## Step 2 — Spawn 6 parallel read-only audit subagents (inherited workspace)

Launch all 6 simultaneously. They are read-only so shared workspace is safe.

| Subagent role | Focus |
|---------------|-------|
| Shell auditor | MainWindow, Home, nav, theme, SettingsWindow |
| Designer auditor | Inspector, tool rail, SelectionPropertiesBar, SceneCanvas UX |
| Core auditor | Domain models, persistence, toolpath, tests |
| Machine auditor | GRBL connection, preflight gates, job lifecycle, alarm surfacing |
| Materials auditor | Catalog, presets, bitmap import, raster pipeline |
| KAMIL auditor | Overlay states, chat API, context accuracy, apply-recommendation |

Each subagent must return:
- Current status (working / broken / missing)
- Regressions or safety gaps
- Broken functionality
- Risks
- Recommended fixes
- Files involved

## Step 3 — Synthesize findings

Collect all 6 reports. Resolve contradictions (if two agents report conflicting facts about the same file, the one that read the actual source wins).

Produce one unified priority list:

| Priority | Criteria |
|----------|----------|
| **P0** | Build failure, test failure, app crash, safety gate bypassed |
| **P1** | Core workflow broken, safety UI missing |
| **P2** | Feature broken but workaround exists, layout broken |
| **P3** | Polish, visual regression, future feature |

## Step 4 — Output the plan

Create an artifact `implementation_plan.md` with:
- P0/P1/P2/P3 issue list
- Proposed owner agent per issue
- Files involved per issue
- Sequencing (what must go first)
- Shared-file change coordination plan

Do NOT begin implementation until the plan is reviewed (or if operating autonomously, document the plan first then proceed).

## Step 5 — Delegate repairs

For each P0/P1 issue:
- Assign to the correct domain agent
- Use isolated worktree if the fix touches owned files + risk of conflict
- Use shared workspace only if truly read-only or if the fix is tiny and non-conflicting

Run independent repairs in parallel where file ownership allows it.

## Step 6 — Integration review

Before merging any repair:
1. Does it build with 0 warnings?
2. Does it pass all 352+ tests?
3. Does it introduce any duplicate state system?
4. Does it fake any machine/connection/job state?
5. Does it bypass any preflight gate?

Only accept if all 5 checks pass.

## Step 7 — Invoke lasero-qa

After integration, run lasero-qa for full regression verification.
