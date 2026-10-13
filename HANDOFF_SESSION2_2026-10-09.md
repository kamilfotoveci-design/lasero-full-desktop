# LASERO handoff, session 2 (2026-10-09)

Branch `design-system-tokens`. Nothing is committed. Preserve all uncommitted work; do not reset, clean or stash.
Earlier handoff (icons, Kamil 2.0): `C:\Users\Ruzovka\Documents\Codex\2026-09-30\cont\CLAUDE_HANDOFF_LASERO_2026-10-09.md`.

## Skills installed this session (project `.claude/skills/`, listed in CLAUDE.md)
wondelai (20): refactoring-ui, ux-heuristics, web-typography, top-design, design-everyday-things, microinteractions,
steve-jobs-design-review, clean-code, refactoring-patterns, software-design-philosophy, pragmatic-programmer,
working-with-legacy-code, clean-architecture, domain-driven-design, release-it, improve-code-quality,
remove-technical-debt, design-code-architecture, architecture-optimization, technical-documentation.
Desktop UI (5, copied from the Codex install): desktop-ui-design, 07-fluent-windows-11-ui, 08-split-panel-workspace,
04-command-palette-ui, 12-workspace-ui. FlaUI.UIA3 5.0.0 added to Lasero.Tests (not used yet).
Decision: WPF-UI (lepoco/wpfui) is NOT adopted (would restyle the whole app). Improve our own UnitField instead.

## Done and verified by build + tests
- Test fixes: 4px-grid paddings, glyph key `Glyph.NodeRemove`, icon lookup reads FluentIcons.xaml, avatar/minimized tests match new design.
- Kamil: `ActionChips` (Trace image, Offset) from real scene state, bound to scene commands (KamilAssistantViewModel.cs);
  chips rendered in QuickAsk + Expanded; apply-recommendation tooltip warns when the layer has no material;
  tests in `Lasero.Tests/KamilActionChipsAndContextTests.cs`. "Nejnovější" pill now has a surface.
- Chat panel bigger: assistant default 480x680, max 960x1000, min height 480; legacy stored sizes (420/560/360) are treated as unset.
- Inspector: speed/power are typed fields (ParameterSlider `ShowSlider=False`), wider fields, 20px rhythm, default width 400.
- Shape tools: new `DesignerTool.Square` ("Čtverec", default shape tool, always equal sides); catalog now nine tools.
- Icons: `Glyph.Device` redrawn as the portal engraver (proposal A); new `EngraverIllustration` control in the device card.
  Proposals file: `.uiqa/engraver-proposals.html`.
- Text: no per-glyph red crawling outlines for text, ObjectStrokePx 1.4 -> 1.0, Text tool press-drag now moves existing text.
- Text rendering: App/theme switched from TextFormattingMode.Display + Hinting Fixed to Ideal + Auto (smooth text).
  Tests updated (ThemeTokenTests, LoginWindowRenderTests). NOT yet confirmed by a test run or by eye.
- Title-bar job progress line added in MainWindow.xaml (2px, bound to GCode.ProgressPercent, visible while GCode.IsJobActive).
  NOT yet visually verified. The status strip already had its own progress bar.

## Was delegated to agents when this was written (check `git status` for their results)
1. Device settings window in LightBurn layout (4 tabs: Rozměry/Jednotky, Základní nastavení, GCode, Další nastavení),
   wrench icon (`Glyph.Tools`) on the button and title bar, only wired options, tests for G-code-affecting ones.
2. App settings window in LightBurn layout (Editor, Units and grids, Display, Import/Export; no camera tab),
   new `EditorPreferences` in AppSettingsStore.cs, only wired options.
3. Text context menu per LightBurn: cut/copy/paste/duplicate, delete, select all, group/ungroup, lock, convert to path
   (Ctrl+Shift+C), convert to bitmap (Ctrl+Shift+B), edit text shape, preview (Alt+P), show properties. Only real commands.
4. Kamil chat typography/layout (ChatView.xaml, KamilAssistantHost.xaml, context band merged, 14-15 DIP, 320 DIP wrap).
The Kamil behaviour/context audit agent was stopped to save usage; policy/snapshot/chips already exist and are tested.

## Still open
- Visual QA in the running app (use `.uiqa/*.ps1`): text smoothness, title-bar progress line during a simulated job,
  inspector spacing, Kamil panel at 320/480/720, square tool icon, engraver illustration, new settings windows, text menu.
- Full `dotnet test` once at the end. Known flaky or pre-existing: TourOverlayRenderTests (dpi 144), GrblPortScannerTests,
  InlineTextEditorRenderTests, BeamSafetyTests/JobCancellationSafetyTests (timing).
- Raised by the user, not started: "LightBurn-like speed/power typed values everywhere" in other panels; improve UnitField
  (wheel/arrow/expression entry); Kamil dark theme not supported.
- Kamil: live backend replies cannot be verified from this repo; no machine was moved or fired.

## Usage tips
Use filtered tests, avoid screenshots unless verifying visuals, one agent per area, start a fresh session from this file.

## Agent results (all UNVERIFIED: nothing built or run; user checks manually)
- Chat layout (done): Kamil header one row, context line merged into header, 15 DIP body, user bubble right, Kamil plain left,
  full chat measure 640 (token Size.Chat.Measure), hover-only copy button (check binding ElementName=MessageRow),
  new KamilMessageBody.cs + JoinNonEmptyConverter.cs, placeholders "Dotaz pro Kamila…"/"Zpráva pro Kamila…".
  Kamil.ContextChip style removed. LaseroTheme.xaml was edited by several agents: check for collisions.
- App settings (done): tabs Nastavení editoru / Jednotky a mřížky / Zobrazení + kept Zařízení a bezpečnost and Účet.
  New EditorPreferences (AppSettings.Editor), SettingRow (code-only control), Toggle.Switch, SceneCanvas.EditorPreferences.cs,
  reduced-motion override (full effect after restart). Left out: inches, beginner mode, import/export tab, theme, curve quality.
  New tests EditorPreferencesTests, SettingsWindowTests never compiled.
- Device settings (done): 4 LightBurn tabs, wrench Glyph.Tools, new JobProgram (air assist M8/M7, return to start after job,
  framing power 0-10%), NumericField + DeviceSettingsRow controls, goldens in Lasero.Tests/Golden/gcode/. Changes the streamed
  job lines (safety-relevant): review Lasero.Core/Jobs/JobProgram.cs before hardware use. Left out: origin chooser, laser offset,
  inches, pulse width, OOB warning toggle, custom G-code.
- Merge later: DeviceSettingsRow and SettingRow are near duplicates.
- Text context menu agent: still running when this was written.
- Last known build error was MC3093 in SettingsWindow.xaml from an intermediate SettingRow.xaml; the file is now code-only
  (SettingRow.cs), so it is probably gone, but unconfirmed.
- Filtered tests to run first: JobProgramTests|DeviceSettingsPreferencesTests|DeviceSettingsWindowRenderTests|EditorPreferencesTests|
  SettingsWindowTests|LayoutGridTests|NeutralFormCopyTests|ThemeTokenTests|RedundantEntryPointsTests|RailAndMotionTests

## Text menu agent (stopped mid-way, edits on disk, unbuilt)
Text right-click menu written: Vyjmout/Kopírovat/Vložit/Duplikovat | Odstranit | Vybrat vše/Seskupit/Odebrat ze skupiny/Zamknout |
Převést na dráhu (Ctrl+Shift+C)/Upravit text | Náhled (Alt+P) | Zobrazit vlastnosti. Missing: Převést na bitmapu (Ctrl+Shift+B).
Files: CanvasContextMenuModel/States, SceneCanvas.ContextMenus, SceneViewModel, MainWindow.xaml.cs (key handling: check Alt+P and
Ctrl+Shift+C collisions), new CanvasContextMenuTextTests.cs. Agent never built or ran tests.
JobProgram.cs read-reviewed by lead: M5 before return, G90 + G0 only, laser never enabled, skipped on abort. Looks safe on paper.
