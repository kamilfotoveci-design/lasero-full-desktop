# Handoff to Codex - 2026-10-08 (LightBurn-parity UI work + leftovers)

Read first: `docs/HANDOFF-CODEX-2026-10-06.md` (hard rules, environment, owner preferences - all still valid),
`AGENTS.md` (machine-safety rules are binding), `CLAUDE.md`, `DESIGN.md`, `docs/interaction-rules.md`,
`docs/lightburn-parity-backlog-2026-10-07.md`, `docs/performance-budget-2026-10.md`, `docs/layout-audit-2026-10.md`.

Working branch: `design-system-tokens` (tip when this was written: `8a76896` or later; run `git fetch` first).
Work only in a fresh worktree off `origin/design-system-tokens`. Rebase before every push, retry failed pushes 3x
(GitHub connectivity has been flaky).

## State at handoff

Pushed and green (last full runs: 1704 passed / 0 failed on `f3fefe6`; 1693 passed on the perf tree, only known timing
flakes): neutral-first palette with solid-red tint, type scale, layout system (4 px grid, shared components), Home
"Domů", one connect button + one homing control on Zařízení, neutral-form Czech copy everywhere, Tip dne card, splash,
tour, installer, test isolation from the real profile, `OnExit` safety fix, canvas performance fixes (pan 168 -> ~3-18 ms,
transform commit, undo/redo, node drag, bulk changes, G-code preview off the UI thread, raster preview off the UI thread).
Last installer built: `E:\lasero-installer-out\Lasero-Desktop-Setup-0.1.0.exe` (from `f3fefe6`, 12:59, unsigned). It does NOT
contain the perf commits `72263b4`, `34593fb`, `8a76896`. Rebuild after your work:
`.\build-installer.ps1 -Version 0.1.0 -SkipTests -OutDir E:\lasero-installer-out` (PowerShell, never Git Bash), verify with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /NOLAUNCH /DIR=<scratch on E:>` only.

A Claude subagent (performance) may still push: async job generation (`SceneJobBuilder`, branch `wip/perf-rest` if not
verified), off-thread SVG/raster import. Fetch before starting; do not duplicate it. Files owned by that work:
`Lasero.App/Controls/SceneCanvas*.cs` - coordinate by rebasing, keep hunks minimal there.

## Your tasks, in priority order (owner: "LightBurn backlog and the small leftovers are the priority")

### A. Small leftovers (do first, quick)
1. Stray divider at the end of the first row of the wrapped selection bar (Návrh, under ~1100 px) - cosmetic fix.
2. The connection wizard keeps its own "Připojit laser automaticky" button. Keep ONE connect entry: the wizard step should start
   the same Connect command (no second differently named button). Update `RedundantEntryPointsTests`.
3. Anything left in `docs/layout-audit-2026-10.md` under "Still open" (job preview window has its own breadcrumb header:
   move it to the shared `DialogFooter`/header pattern when you do task C).

### B. "Upravit obrázek" premium (LightBurn's Adjust Image, done better) - `RasterImportWindow` + `Lasero.Core.Raster`
Keep `ImageProcessor` / `RasterPlanner` / `GrblRasterGenerator` semantics and the golden tests byte-identical.
1. Side-by-side: left original, right processed result rendered at TRUE dot scale (dither at chosen DPI / line interval,
   not a blurry scaled preview); synchronized zoom/pan; 100 percent = real size on the material with a caption in mm;
   optional before/after drag divider; px and mm under each pane.
2. Controls grouped plainly: Režim (Práh, Floyd-Steinberg, Stucki, Jarvis, Atkinson, šedá/stupně šedi if the engine supports it),
   DPI and Interval čar kept in sync with exact conversion + validation (machine min spot size/laser mode where known) +
   plain Czech hint, Kontrast, Jas, Gamma, Zostření (radius + amount), Invertovat, Práh where relevant, Obnovit vše.
3. Předvolby: built-ins (Dřevo, Překližka, Kůže, Akryl, Papír, Fotografie, Logo) = image-processing values ONLY (never power or
   speed); user presets save/delete/import/export as JSON through an injectable store in the app settings folder.
4. Performance: processing off the UI thread, debounced (~120 ms), cancellable per slider change (no stale result applied),
   progressive preview (fast low-res pass, then full-res), UI thread never blocked > 16 ms, 12 MP image stays responsive,
   decoded source cached at display resolution. Measure with an in-process benchmark and report numbers.
5. Beginner help: one plain-language line under the mode selector, a "Doporučeno" badge on a sensible default for photos vs logos.
6. Tests: DPI<->interval conversion, preset round trip, debounce/cancel, synced zoom math, offscreen render at 1366x768 and
   1080x640 (no clipping), golden raster output unchanged.

### C. "Náhled úlohy" (job preview) - part of Generate -> Validate -> Preview -> Preflight -> Send, never a bypass of `JobPreflight`
1. Playback: scrub bar (drag/click/arrow keys), play/pause, speed 0.25x-8x, step.
2. Statistics in plain Czech computed from the same toolpath/G-code the machine receives: Odhadovaný čas (m:ss), Délka řezu,
   Délka přejezdů, Počet objektů; document the time model and label it an estimate.
3. Toggles: Zobrazit přejezdy (thin dashed), Stínovat podle výkonu, Legenda (operation colours + names), Zobrazit směr.
4. "Uložit obrázek" (PNG of the current view) with an injectable save dialog.
5. Progressive simulated burn: build toolpath layers once off the UI thread (reuse the perf agent's off-thread G-code preview
   builder), then clip/trim for the scrub position only; scrubbing < 16 ms per frame on a 500k-line job (in-process benchmark).
6. Premium styling per DESIGN.md, standard header + `DialogFooter` (Zavřít left, primary right).
7. Tests: time estimate vs hand-computed small jobs, scrub position -> trimmed length math, toggles, offscreen render,
   no UI-thread blocking.

### D. Operation colour strip (LightBurn's bottom colour palette, calmer)
Swatch row under the canvas or at the bottom of the inspector (decide with offscreen renders at 1366x768 / 1080x640): click a
swatch to assign the selected objects to that operation (existing operations model; create if missing; ONE undo step); shows
colour, name and object count; per-operation "Výstup" and "Zobrazit" toggles with plain labels + tooltips (no unexplained
red/green toggles). Tint rule: selected swatch gets the solid red ring, nothing else red. Keyboard accessible; no new
persistent settings.

### E. Job origin 3x3 + "Začít od"
Selector for the job origin (3x3 grid) and "Začít od" (Absolutní souřadnice / Aktuální poloha / Uživatelský počátek ONLY if the
job generator really supports them - do not invent modes), a one-sentence plain explanation ("Pálí se od ... materiálu"), and a
small preview of where the job lands in the work area. Wire only to existing job-origin plumbing; leave preflight bounds checks
untouched. If no plumbing exists: ship the UI model + tests and list the wiring as an owner decision instead of guessing
machine behaviour.

### F. Node tools sub-rail, trace simplification, context menu
1. Node sub-rail shown only while editing nodes: labelled icon buttons with tooltips + shortcuts (add node, delete node,
   smooth, corner, line, curve, break, join), bound to existing commands.
2. After Trace Bitmap: simplify the result in `Lasero.Core` (Douglas-Peucker/Schneider-style fit; preserve genuine Bezier data
   where possible; never produce NaN/zero-length corruption; tolerance parameter with a sensible default) and in the canvas
   draw node dots only near the cursor / on the selected segment (coordinate with `SceneCanvas*.cs` owners).
3. Context menu with icons and `InputGestureText` shortcut hints for existing commands.

## Do not copy from LightBurn
30-icon unlabeled toolbar, unlabeled red/green toggles, two identical "Rámovat" buttons, core controls hidden in tabs, dual-scale
rulers. Our edge is calm, labelled and guided.

## Owner rules that bite (see the 2026-10-06 handoff for the full list)
- NEVER drive the owner's real mouse/keyboard; verify with offscreen WPF renders, tests and silent installers only.
- Tests must never touch the real `%LOCALAPPDATA%\Lasero` (logs, session.dat, settings.json); never run `App.OnStartup`/splash
  in tests; never `Show()` a window on the visible desktop; close windows in `finally`; per-test timeouts.
- Run `dotnet build/test` in the background with a log on E:, `--blame-hang-timeout 3m`, kill only your own PIDs (never by
  image name). C: has ~3 GB free: `TEMP`/`TMP` on E:.
- Czech UI copy: neutral form (no tykání/vykání, no imperatives; infinitive button labels like "Připojit" are fine), no `?` or `!`,
  classic hyphens, plain words for beginners. Guards: `NeutralFormCopyTests`, `LayoutGridTests` (4 px grid), `PaletteTests`,
  `RedundantEntryPointsTests`, `ThemeTokenTests`.
- Typography/targets: body 15, secondary >= 14, nothing under 13 (except rulers/badges), hit targets >= 40 (rail 44), compact 36.
- One primary action per screen state; red = solid tint on active/ON only, never a wash.
- Report to the owner in short plain Czech/Slovak with an honest "not verified" list.

## Known flaky tests (rerun isolated before calling a failure real)
`JobCancellationSafetyTests`, `BeamSafetyTests`, `GrblPortScannerTests`, `AutoConnectTests`, `TourOverlayRenderTests`,
`SplashWatchdogClosesAnOrphanedSplash`. All WPF render tests share one xunit collection and the shared STA dispatcher
`InlineTextEditorRenderTests.Ui`; do not create your own dispatcher thread or call `Dispatcher.Invoke` nested inside it.

## Unverified so far (nobody has seen it live)
Hover/focus/pressed/animation timing, popups and context menus, multi-monitor DPI beyond offscreen, splash cold start with a
recovery backup, real laser behaviour, Release-build performance numbers, installer GUI pages, signing.

## Update (perf agent stopped): async job generation lives on `origin/wip/perf-rest` (`fa1722c`, UNVERIFIED)

The performance agent was stopped by the owner before its final full-suite run. Its last work is committed on branch
`wip/perf-rest` (based on `8a76896`): `Lasero.Core/Jobs/SceneJobBuilder.cs` (immutable scene snapshot + same build function),
async cancellable job generation in `GCodeViewModel.cs`, `Lasero.Core/Scene/SceneObject.cs` change,
`Lasero.Tests/AsyncJobGenerationTests.cs` and `AsyncJobMachineBase.cs`. Before merging: rebase onto the tip, build, run the
full suite, and require (1) sync == async equivalence on the same scene, (2) golden G-code tests green, (3) Start, Frame and
preflight force a synchronous rebuild whenever the prepared job is stale (a job prepared in the background must never be sent
after the design changed), (4) `NeutralFormCopyTests` green (the agent once overwrote neutral copy by mistake in
`GCodeViewModel.cs`: keep upstream's strings). If any of this cannot be satisfied quickly, drop the branch; it is not required
for the installer. Off-thread SVG/raster import parsing was NOT started. Do this BEFORE task C (job preview), because the preview
builds on the same off-thread toolpath builder.
