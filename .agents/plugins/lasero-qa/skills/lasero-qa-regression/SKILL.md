---
name: lasero-qa-regression
description: >-
  Use when lasero-qa needs to run a full regression check of the Lasero desktop
  application after changes have been made. Covers build, tests, navigation,
  critical workflow, KAMIL overlay, machine safety, and multi-resolution layout.
---

# Lasero Full Regression Checklist

Run this skill after every integration or when explicitly asked to verify the application state.

## 1. Build verification

```powershell
dotnet build E:\lasero-desktop\LaseroDesktop.sln --configuration Debug
```

Expected: 0 errors, 0 warnings.

## 2. Test suite

```powershell
dotnet test E:\lasero-desktop\LaseroDesktop.sln
```

Expected: all tests pass. Baseline: 352 / 352.

Key test classes to verify:
- `JobPreflightTests` — all 8 gates
- `GCodeJobRunnerLifecycleTests` — Pause/Resume/Abort/Disconnect-during-job
- `GrblConnectionLifecycleTests` — connect, alarm, disconnect
- `VirtualLaserMachineTests` — beginner workflow end-to-end
- `SceneCommandStackTests` — undo/redo
- `ProjectFileSerializerTests` — save/load round-trip
- `ThemeTokenTests` — no literal font sizes in XAML
- `MainWindowNavigationTests` — screen switching

## 3. Navigation regression

Verify each screen is reachable and renders correctly:
- Home — project cards, device rail, recent materials
- Návrh (Designer) — canvas, tool rail, inspector, selection bar
- Zařízení (Device) — connection UI, machine status, jog pad
- Lasero Chat — Kamil chat panel, session list

Also verify:
- Rail collapse (icons-only) works and persists
- Inspector splitter can be dragged, width persists
- DesignerRail replaces NavRail correctly on Designer screen
- Status strip (bottom 48px) always visible on all screens

## 4. Critical workflow

Walk through this sequence:
1. Open app → Home shows
2. Click "Nový projekt" → switches to Designer, blank canvas
3. Import SVG → object appears on canvas
4. Click object → SelectionPropertiesBar appears with X/Y/W/H
5. Assign operation in inspector — Color, Mode, Speed, Power
6. Apply material from library → layer shows material name
7. Click "Generovat G-kód" or equivalent → WorkspaceCanvas shows preview
8. Connect Virtual Laser machine → status strip shows "Připojeno"
9. Frame → runs, marks `IsCurrentDocumentFramed = true`
10. Start → preflight passes, confirmation dialog, job runs
11. Pause → job pauses
12. Resume → job resumes
13. Stop → job aborts

Report any step that fails.

## 5. Machine safety regression

Verify these specific behaviors:
- Start is disabled if machine not connected
- Start is disabled if framing not done (with `RequireFramingBeforeStart = true`)
- Start is disabled if machine not Idle
- Preflight shows human-readable reason for block, not a raw GRBL code
- Stop is always reachable during an active job
- Alarm state is shown somewhere (currently a known gap — report as P1 if still missing)

## 6. KAMIL regression

- Minimized pill visible in canvas corner during Designer screen
- Click pill → QuickAsk opens, composer focused
- Type message → Expanded panel opens
- Esc collapses: Expanded → QuickAsk → Minimized
- Switch screen → Kamil state persists (not closed)
- KAMIL overlay does NOT cover Frame/Start/Pause/Resume/Stop buttons
- Long conversation → scrolls correctly, no layout break

## 7. Multi-resolution layout check

Use `.uiqa/shot.ps1` to capture screenshots, or resize the window manually.

| Resolution | Things to check |
|------------|----------------|
| 1366×768 | Rail can collapse to icons; inspector minimum 320px; status strip not clipped |
| 1440×900 | Standard check |
| 1536×960 | Windows laptop DPI; nothing overflows |
| 1920×1080 | Primary target; all panels comfortable |
| 2560×1440 | High-DPI; Inter font renders cleanly; nothing stretched |

## 8. WPF binding errors

Run the application in Debug mode and watch the Output window for:
```
BindingExpression path error
Cannot find governing FrameworkElement
```

These are runtime-only and don't fail the build. Document any found.

## 9. Issue report

For each issue found, report:
```
Severity: P0 / P1 / P2 / P3
Area: [Shell | Designer | Core | Machine | Materials | KAMIL]
Evidence: [test name | observed behavior | screenshot]
Likely owner: lasero-[agent]
Recommendation: [what to fix]
```
