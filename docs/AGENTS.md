# Lasero Desktop — Agent Configuration

This document describes the 8 Antigravity agents configured for the Lasero project under `.agents/plugins/`.

---

## Overview

Lasero uses a **plugin-based agent system** powered by Antigravity's customization framework. Each agent is a plugin containing:

- `plugin.json` — plugin manifest (enabled/disabled state)
- `rules/AGENTS.md` — always-on behavioral identity (loaded automatically when the plugin is enabled)
- `skills/*/SKILL.md` — on-demand workflow procedures (loaded on demand by the agent)

---

## Enabling an Agent

In the Antigravity IDE or CLI, enable the desired plugin before starting a session:

```
# CLI
agy plugin enable lasero-lead

# UI
Settings → Plugins → lasero-lead → Enable
```

**Only one specialist agent should be enabled at a time** alongside `lasero-lead`. Enabling multiple domain agents in a single session may cause conflicting instructions.

---

## Agents

### 1. `lasero-lead` _(always on)_

**Role:** Technical lead, architect, orchestrator, integrator.

**When to use:** Any task that spans multiple domains, requires architecture decisions, involves shared files, or needs integration across agents.

**Owns:** Architecture, shared files coordination, git integration, final QA sign-off.

**Does NOT:** Personally implement every change — it delegates.

**Key skills:**
- `lasero-parallel-audit` — run 6 parallel domain audits, produce P0/P1/P2/P3 plan
- `lasero-worktree-repair` — manage isolated branch/worktree repairs

---

### 2. `lasero-shell` _(enable for Shell/Home work)_

**Role:** WPF application shell, Home screen, navigation, design system.

**When to use:** Changing MainWindow layout, navigation rail, Home screen, Settings window, shared styles, theme tokens, reusable UI components.

**Owns:** `MainWindow.xaml`, `MainWindow.xaml.cs`, `HomeView.xaml`, `HomeViewModel.cs`, `LaseroTheme.xaml`, `SharedUiStyles.xaml`, `SettingsWindow.xaml`, `Components/`.

**Reference:** `docs/stitch-homepage`

---

### 3. `lasero-designer` _(enable for Designer work)_

**Role:** Designer workspace, canvas interaction, inspector, tool rail, object editing UX.

**When to use:** Anything in the Návrh (Designer) screen: inspector sections, tool rail, SelectionPropertiesBar, SceneCanvas mouse interaction, zoom/pan, layer list, operation settings UI.

**Owns:** `DesignerInspectorView.xaml`, `DesignerToolRail.xaml`, `SelectionPropertiesBar.xaml`, `SceneViewModel.cs`, `Controls/SceneCanvas.xaml.cs`, `DesignerTool.cs`.

**Reference:** `docs/stitch-navrh`

---

### 4. `lasero-core` _(enable for Core/Persistence work)_

**Role:** Domain model, project persistence, toolpath architecture, backend services.

**When to use:** Changing project file schema, serialization, undo/redo commands, toolpath generation, SVG import, material domain models, `Lasero.Core` in general.

**Owns:** All of `Lasero.Core/`, `ProjectFile.cs`, `AppSettingsStore.cs`, `MaterialPresetStore.cs`, `RecentProjectsStore.cs`, `JobHistoryStore.cs`, `ChatStore.cs`, `ProjectRecoveryStore.cs`.

**Principle:** Every meaningful change must include a test.

---

### 5. `lasero-machine` ⚠️ _(enable only for GRBL/Safety work)_

**Role:** Serial communication, GRBL protocol, preflight, job streaming, machine safety.

**When to use:** Alarm handling, connection lifecycle, preflight gate changes, Frame/Start/Pause/Resume/Stop behavior, jog, work area validation.

**Owns:** `Lasero.Core/Grbl/`, `Lasero.Core/Jobs/`, `Lasero.Core/Machines/`, `ConnectionViewModel.cs`, `GCodeViewModel.cs` (job lifecycle section), `JogViewModel.cs`, `DeviceView.xaml`.

**⚠️ Safety-first:** This agent is conservative by design. Never weaken preflight gates. Never fake machine state.

---

### 6. `lasero-materials` _(enable for Materials/Bitmap work)_

**Role:** Material catalog, user presets, bitmap import, raster engraving, trace.

**When to use:** Material picker UI, recipe display, swatch grid, preset management, raster import dialog, dithering, bitmap trace window.

**Owns:** `Lasero.Core/Materials/`, `Lasero.Core/Raster/`, `Lasero.Core/Trace/`, `MaterialsWindow.xaml`, `MaterialsViewModel.cs`, `MaterialSwatchViewModels.cs`, `RasterImportWindow.xaml`, `BitmapTraceWindow.xaml`.

**Reference:** `docs/stitch-material`

---

### 7. `lasero-kamil` _(enable for KAMIL/AI work)_

**Role:** KAMIL AI assistant overlay, chat backend integration, conversation state.

**When to use:** KAMIL overlay UX, state machine (Minimized/QuickAsk/Expanded), composer, context chips, apply-recommendation cards, chat session management, Kamil animations.

**Owns:** `Views/Kamil/`, `ChatView.xaml`, `ChatViewModel.cs`, `KamilAssistantViewModel.cs`, `ChatStore.cs`, `Lasero.Core/LaseroApi/LaseroChatClient.cs`.

**Reference:** `docs/stitch-chat`

---

### 8. `lasero-qa` _(enable for QA/Regression work)_

**Role:** Independent QA verifier. Default mode is read-only.

**When to use:** After any integration, before release, when verifying another agent's work, investigating runtime binding errors, checking multi-resolution layout.

**Default: does NOT modify production code.** Only modifies tests when explicitly assigned a repair.

**Key skill:** `lasero-qa-regression` — full regression checklist (build → tests → navigation → workflow → machine safety → KAMIL → multi-resolution)

---

## Ownership Table

| Area | Owner |
|------|-------|
| Home screen | lasero-shell |
| Navigation rail | lasero-shell |
| Settings | lasero-shell |
| Shared WPF theme | lasero-shell |
| Reusable components | lasero-shell |
| Designer workspace | lasero-designer |
| Canvas interaction | lasero-designer |
| Inspector | lasero-designer |
| Tool rail | lasero-designer |
| Core domain models | lasero-core |
| Project persistence | lasero-core |
| Toolpath pipeline | lasero-core |
| Undo/Redo commands | lasero-core |
| Machine/GRBL | lasero-machine |
| Preflight/Safety | lasero-machine |
| Job lifecycle | lasero-machine |
| Materials catalog | lasero-materials |
| User presets | lasero-materials |
| Bitmap import | lasero-materials |
| Raster engraving | lasero-materials |
| KAMIL overlay | lasero-kamil |
| Chat backend | lasero-kamil |
| Build/Tests/QA | lasero-qa |
| Cross-domain integration | lasero-lead |

---

## Shared File Rules

These files must have exactly **one writer per task session**. Other agents propose patches; the owner implements them.

| File | Owner |
|------|-------|
| `MainWindow.xaml` | lasero-shell |
| `MainWindow.xaml.cs` | lasero-shell |
| `ViewModels/MainViewModel.cs` | lasero-shell |
| `Theme/LaseroTheme.xaml` | lasero-shell |
| `Theme/SharedUiStyles.xaml` | lasero-shell |
| `App.xaml` / `App.xaml.cs` | lasero-lead |

---

## Recommended Workflow

### For a multi-domain task:
1. Enable `lasero-lead` (if not already the active agent).
2. Ask it to run the `lasero-parallel-audit` skill.
3. Review the P0/P1/P2/P3 plan it produces.
4. lasero-lead delegates repairs to specialist agents (using worktrees for parallel work).
5. lasero-lead integrates and reviews each result.
6. Enable `lasero-qa` to run full regression (`lasero-qa-regression` skill).
7. Delegate any QA failures back to the appropriate owner.

### For a focused single-domain task:
1. Enable just the relevant specialist plugin (e.g., `lasero-designer`).
2. Give it a focused task within its ownership scope.
3. Have it build and test after the change.
4. If it touches a shared file, coordinate through lasero-lead.

---

## First Prompt for lasero-lead

```
You are lasero-lead for the Lasero desktop application at E:\lasero-desktop.

Activate the `lasero-parallel-audit` skill.

Run parallel read-only audits across all 6 specialist domains:
Shell, Designer, Core, Machine, Materials, KAMIL.

After collecting all audit results:
1. Synthesize findings into a single P0/P1/P2/P3 prioritized issue list.
2. Create implementation_plan.md with all findings.
3. Do NOT modify any production code during this audit phase.
4. Report back with the completed plan.
```
