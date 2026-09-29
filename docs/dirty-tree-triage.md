# Dirty-tree triage (branch `design-system-tokens`, HEAD `c0b06c8`)

Read-only analysis produced from `git status`, `git diff --numstat` and the diffs themselves. Nothing was built, tested, staged or modified. Test outcomes quoted below come from the brief (HEAD 752/753 in an isolated worktree, only `MainWindowNavigationTests.SelectionPropertiesLiveInOneContextualBarOverTheCanvas` failing; full dirty tree 800/800). Anything marked "unclear" could not be established from the diff.

## 0. Inventory

| Bucket | Count | Disposition |
|---|---|---|
| Modified tracked files | 86 (+3780 / -1240) incl. 2 deletions | grouped below |
| Untracked source/docs (excl. bulk) | about 40 source files + 79 docs files + about 86 `.agents` + 7 `.uiqa` | grouped below |
| `.artifacts/` | 492 untracked files, about 964 MB (trace experiment builds, PNG/SVG comparisons, python scripts) | do not commit |
| `Lasero.Avalonia/` + `Lasero.Avalonia.Tests/` | 351 + 13 untracked files; 291 of them under `Lasero.Avalonia/publish/` (DLLs/PDBs) | do not commit (see G16) |
| `.claude/skills/` | 54 untracked entries; `ls -la` shows the three top-level entries are symlinks to `.agents/skills/avalonia*` | do not commit as symlinks |
| `.uiqa/*.png`, `bin/`, `obj/`, `dist/`, `artifacts/` | already ignored by `.gitignore` | ignore |
| `.uiqa/app-stderr.log`, `app-stdout.log` | untracked logs | do not commit |

Whole-tree noise to know about before staging anything:

- Git warns that LF will be replaced by CRLF for every modified file (working copy is LF). Expect line-ending churn to be normalised on `git add`; check `git diff --cached --stat` after staging.
- `MainWindow.xaml.cs` and `SceneViewModelTests.cs` gained a UTF-8 BOM on line 1. `SceneViewModel.cs` has two comments repaired from mojibake (`â€”` to an em dash). These are encoding-only hunks, harmless but they inflate the diffs.
- `SceneCanvasInteractionTests` (new) contains a source-text assertion with a literal `"\n"` inside a multi-line string. If a checkout ever materialises CRLF in `SceneCanvas.xaml.cs` that assertion would fail. Worth knowing when committing with CRLF conversion.

## 1. Groups at a glance, in safe commit order

Legend: 800 = needed for the 800/800 result. Risk is low/medium/high.

| # | Group | 800? | Risk | Recommendation |
|---|---|---|---|---|
| G1 | Test stability and tooling (JobCancellation timing, shot.ps1, `.uiqa` helpers, sln registration, test csproj x64) | yes (timing) | low | commit-as-is |
| G2 | Docs and agent config (CLAUDE.md, AGENTS.md, docs, `.agents` plugins/rules/lasero skills) | no | low | commit-as-is, after deciding on duplicate docs |
| G3 | SvgImporter editable-metadata | yes (2 tests) | low-medium | commit-as-is |
| G4 | Raster-import dialog removal (GCodeViewModel) | no | low | commit-as-is |
| G5 | Chat presentation (normalizer, ChatView, DisplayText, system prompt) | yes (3 tests) | low-medium | needs finishing (copy fixes, entry-point question) |
| G6 | Vector-path core helpers and node-edit canvas (snapping, hit tester, SceneCanvas, node toolbar, "Upravit uzly") | yes (about 38 tests) | medium-high | needs hunk split |
| G7 | Designer tool rail: shape picker and chat entry | yes (1 test hunk) | medium | needs hunk split |
| G8 | Inspector: inline machine control, MachineControlWindow deletion, Job tab removal | yes (fixes the HEAD failure) | medium | needs hunk split |
| G9 | Window title bar (`WindowTitleBar`, `WindowFrameHook`, 9 secondary windows, login reveal-password) | no | medium | needs hunk split |
| G10 | Bitmap trace / VTracer / Potrace | yes (2 tests) | high | needs hunk split and finishing |
| G11 | Text rendering, motion tokens and reduced motion (App ctor, UiAccessibility, 2 ThemeToken tests) | yes (2 tests) | low | needs hunk split (App.xaml.cs) |
| G12 | Shell navigation: permanent sidebar, Kamil Designer-only, avatar alias removal, bottom-bar dividers | yes (2 ThemeToken tests, nav test) | medium-high | needs hunk split |
| G13 | Design-system polish sweep (components, Home/Device/Materials views, icons, dialog animation, microcopy, layer palette) | no | low-medium | needs hunk split (mixed with G9/G12) |
| G14 | Background removal via Gemini | no tests | high | needs finishing |
| G15 | Machine-safety leftovers (device-profile hook, `$30`/`$32` console confirm) | no tests | medium | needs finishing (tests) |
| G16 | Avalonia experiment, `skills-lock.json` Avalonia entries, `.claude/skills` symlinks | no | n/a | do not commit as is |
| G17 | Build junk and orphan assets (`.artifacts`, logs, KamilAvatar variants) | no | n/a | do not commit |

Why this order: G1 to G4 have no dependency on anything else. G5 must land before G12 because G12 removes the `Image.KamilAvatar` theme alias that HEAD `ChatView.xaml` and `MainWindow.xaml` still use (removing the alias first would throw at load). G6 to G8 are UI groups that only depend on HEAD. G9 must land before G10 because `BitmapTraceWindow.xaml` uses `components:WindowTitleBar`. G11 before G12 so the theme test file can be split cleanly. G13 last of the UI groups because it touches files shared with G9/G12. G14 and G15 are behaviour changes with no tests and should not ride along.

## 2. Groups in detail

### G1 Test stability and tooling

Files:
- `Lasero.Tests/JobCancellationSafetyTests.cs` +5/-4 (StatusSilenceTimeout 600 ms to 800 ms in two tests, comments updated, newline at EOF).
- `Lasero.Tests/Lasero.Tests.csproj` +2/-0 (`<PlatformTarget>x64</PlatformTarget>`; the comment says it matches Core/App).
- `.uiqa/shot.ps1` +3/-2 (new `-ProcessName` parameter, default `Lasero.App`).
- untracked `.uiqa/ctrlz.ps1`, `dblclick.ps1`, `invoke.ps1`, `keypress.ps1`, `releasemods.ps1` (UI automation helpers; contents not reviewed beyond names).
- `LaseroDesktop.sln` +6/-0 registers `Lasero.Persistence` (project already tracked: `Lasero.Persistence.csproj`, `ProjectFile.cs`). Why it was not registered before is unclear; nothing in the dirty tree references it.

Purpose: widen a timing margin in two cancellation tests, allow the screenshot script to target another process, register an existing project in the solution.
Mixed hunks: none.
Dependencies: none. The x64 PlatformTarget presumably avoids a native (OpenCV) bitness mismatch for tests; that motive is not visible in the diff (unclear).
Tests: the two edited tests themselves.
Risk: low. Recommendation: commit-as-is. Consider committing the sln line on its own, and leave the `.uiqa/*.log` files out.

### G2 Docs and agent config

Files:
- `CLAUDE.md` +52/-0 (external UI/UX references section, "ENGINEERING WORK RULE" section pointing at `docs/engineering/*`, `docs/ui-reference/*`, `docs/reference/LIGHTBURN_VECTOR_PARITY.md`).
- untracked `AGENTS.md`, `docs/AGENTS.md`, `docs/engineering/` (6 files), `docs/reference/` (`LIGHTBURN_VECTOR_PARITY.md`, `NODE_EDIT_PARITY_AUDIT_2026-09-16.md`, `engineering/` 6 files, `ui-reference/` 3 files), `docs/ui-reference/approved-patterns/` (4 files), `docs/MICROINTERACTIONS_AUDIT.md`, `docs/ui-polish-pass.md`, `docs/ux-interaction-audit.md`, `docs/vector-and-machine-architecture-audit.md`.
- untracked design reference dumps: `docs/stitch-chat|homepage|material|navrh/` (DESIGN.md, code.html, screen.png each; about 830 KB) and `docs/designer-refinement-prototype/` (about 685 KB: HTML prototypes, 31 icon files, `PHOSPHOR-LICENSE.txt`, AUDIT/HANDOFF/CLAUDE-PROMPT md).
- untracked `.agents/plugins/` (about 10 plugin folders, 64 KB), `.agents/rules/` (8 KB), `.agents/skills/lasero-engineering|lasero-machine-safety|lasero-regression-testing|lasero-svg-pipeline|lasero-vector-editor|lasero-wpf-quality`.

Purpose: engineering and UI-process documentation, repo-level agent rules and Lasero-specific skills.
Mixed hunks: none in tracked files. Note `docs/reference/engineering/*` and `docs/engineering/*` are the same six filenames but all six differ in content; which is canonical is unclear. `CLAUDE.md` only points at `docs/engineering`, so `docs/reference/engineering` looks like an older or alternate copy (inference, unconfirmed). `AGENTS.md` itself warns that older docs and audits may be stale.
Dependencies: `CLAUDE.md` references `docs/engineering/*` and `docs/ui-reference/*`, so those must be in the same commit or before it. The `.agents/skills/avalonia*` folders are NOT part of this group (see G16).
Tests: none.
Risk: low (no compile impact). Recommendation: commit-as-is, but first decide whether to drop `docs/reference/engineering/` and whether the Stitch HTML/PNG dumps belong in the repo.

### G3 SvgImporter editable-metadata

Files:
- `Lasero.Core/Import/SvgImporter.cs` +22/-4
- `Lasero.Tests/SvgImporterTests.cs` +26/-0

Purpose: `VectorPath` is now attached to an imported document only when every shape shares the same LayerId, LayerColor, PreferredMode and GeometrySetId and shape count equals subpath count; any dropped/degenerate subpath (fewer than 2 points, unclassifiable colour, fewer than 2 nodes) also invalidates it. Mixed-layer SVGs therefore stay renderable but lose node-editability (by design per the test name `MixedLayerSvgKeepsImportedShapeMetadataAndDisablesUnsafeNodeEditing`).
Mixed hunks: none.
Dependencies: none (Core only).
Tests: `MixedLayerSvgKeepsImportedShapeMetadataAndDisablesUnsafeNodeEditing`, `HomogeneousMultiSubpathSvgRemainsNodeEditable` (2 of the 47 new tests).
Risk: low-medium (user-visible: some previously node-editable multi-colour SVGs no longer are). Recommendation: commit-as-is.

### G4 Raster-import dialog removal

Files:
- `Lasero.App/ViewModels/GCodeViewModel.cs` +11/-7

Purpose: importing PNG/JPG/BMP no longer opens `RasterImportWindow`; it builds `RasterImportOptions` inline and calls `_scene.ImportRasterFile` directly, with a new status message pointing to the right-click menu.
Mixed hunks: none in this file. Side effects: `RasterImportWindow` and `RasterImportViewModel` are now unreferenced from app code (only `RasterImporterTests` still uses `RasterImportViewModel`); `RasterImportWindow.xaml` is modified only for the title bar (belongs to G9). The window is dead code after this change; whether it should be deleted is a product decision (unclear).
Dependencies: none for compilation.
Tests: no direct test of the new path. `RasterImporterTests` are unaffected.
Risk: low. Recommendation: commit-as-is. The claim in the message that image settings are reachable by right-click was not verified.

### G5 Chat presentation

Files:
- untracked `Lasero.App/ChatResponseNormalizer.cs` (59 lines)
- `Lasero.App/ViewModels/ChatViewModel.cs` +1/-0 (`ChatMessageItem.DisplayText`)
- `Lasero.App/Views/ChatView.xaml` +45/-37, `Lasero.App/Views/ChatView.xaml.cs` +39/-3 (borderless assistant messages, `LaseroAvatar` instead of raw `Image.KamilAvatar` ellipses, copy button, scroll-to-bottom button with stick-to-bottom logic, busy indicator turned into an Expander)
- `Lasero.Core/LaseroApi/LaseroChatClient.cs` +5/-0 (sends a `systemInstruction` telling the model to answer briefly in Czech without Markdown)
- untracked `Lasero.Tests/ChatPresentationTests.cs` (40 lines, 3 tests; one asserts default and clamp bounds of `WorkspacePreferences` assistant size, which already exist at HEAD)

Purpose: strip Markdown from model replies for display, restyle the full-screen chat, add copy and scroll affordances, and prompt the model for concise plain output.
Mixed hunks: the avatar swap in `ChatView.xaml` is the counterpart of the `Image.KamilAvatar` alias removal in `LaseroTheme.xaml` (G12). Committing ChatView first is safe (it no longer references the alias); the reverse order is not.
Dependencies: everything used already exists at HEAD (`LaseroAvatar`, `Glyph.Copy`, `Glyph.ChevronDown`, `Button.Icon`, `Button.Link`).
Important observation: HEAD `Views/Kamil/KamilAssistantHost.xaml` (unmodified in the working tree) already binds `{Binding DisplayText}` twice inside the `Chat.Messages` template, but at HEAD `ChatMessageItem` has no `DisplayText`. WPF binding failures are silent, so at HEAD the Kamil overlay message text is very likely not rendered. This group fixes that; it is the most valuable group to land early. (Inference from the diff; not run.)
Findings for "needs finishing":
- New strings in `ChatView.xaml`: "Prejsť na najnovšiu správu" (ToolTip and AutomationProperties.Name) is Slovak, not Czech. "Co chceš upravit na tomto návrhu?" uses informal address, while the project's brand-text rule in memory says neutral form. Both are copy issues, not code issues.
- After the rail/nav changes (G7, G12) nothing in the working tree navigates to `AppScreen.Chat` any more (`ShowChatCommand` is defined but no XAML binds it; HEAD `DesignerToolRail.xaml` bound it, the working tree replaces it with `OnChatClick` to `OpenKamilInDesigner`). The restyled full-screen `ChatView` may therefore be unreachable from the UI. Needs a product decision.
- Whether the Netlify `gemini` function forwards `systemInstruction` is unclear.
Tests: `ChatPresentationTests` (3 of the 47).
Risk: low-medium. Recommendation: needs finishing (fix the two strings, decide the ChatView entry point); the code itself can be committed as is.

### G6 Vector-path core helpers and node-edit canvas

Files:
- untracked `Lasero.Core/Scene/Snapping/SnapEngine.cs` (123), `SnapCandidateBuilder.cs` (116)
- untracked `Lasero.Core/Scene/VectorPathHitTester.cs` (108)
- `Lasero.App/Controls/SceneCanvas.VectorPathTool.cs` +564/-54
- `Lasero.App/Controls/SceneCanvas.xaml.cs` +151/-5, `SceneCanvas.xaml` +4/-0
- `Lasero.App/Views/NodeEditToolbar.xaml` +326/-54, `NodeEditToolbar.xaml.cs` +23/-0
- `Lasero.App/Views/SelectionPropertiesBar.xaml.cs` +23/-0 (`TargetCanvas` DP, `OnEnterNodeEditClick`), part of `SelectionPropertiesBar.xaml` +111/-50 (the "Upravit uzly" button)
- `Lasero.App/MainWindow.xaml`: the single hunk that adds `TargetCanvas="{Binding ElementName=DesignerCanvas}"` to `SelectionPropertiesBar`
- tests, all untracked: `SnapEngineTests.cs` (13 facts), `VectorPathHitTesterTests.cs` (3), `VectorPathJoinTests.cs` (8), `NodeEditPerformanceTests.cs` (2 facts + 2 theories, 6 InlineData), `SceneCanvasInteractionTests.cs` (3, source-text assertions)

Purpose (from the diff): snap-to-node and grid snap during node drags with an on-canvas indicator; a Core hit tester replaces the private `SegmentHit`; segment operations (convert line/curve, insert midpoint, delete segment, Shift+L/C/M), select-all/clear nodes, reverse path, cross-object endpoint join, node/segment context menus, public `EnterNodeEditMode`; canvas renders `VectorPath` cubics directly (no faceting at zoom), incremental add/remove instead of full `RebuildAll`, drag cancellation on lost capture and window deactivation, and an explicit "Upravit uzly" button in the selection bar and context menu.
Mixed hunks:
- `SceneCanvas.xaml.cs`: three concerns in one file: node-edit drag routing (`ScheduleExpensiveDragUpdate`, Shift+L/C/M), a pure rendering change (`BuildVectorPathGeometry`), and a pure performance change (incremental `OnObjectsChanged`). The last two could ship as a separate "canvas rendering/perf" commit, but `NodeEditPerformanceTests` and `SceneCanvasInteractionTests` do not obviously cover them (unclear).
- `SelectionPropertiesBar.xaml`: "Upravit uzly" button belongs here; the transparent `Field.Quiet` style, `BarDivider` removal (moved to shared styles), padlock-to-chain glyph change and slim ScrollBar removal belong to G13.
- `NodeEditToolbar.xaml` (+326/-54): almost entirely node-edit; whether it also contains pure visual restyle is unclear without a side-by-side.
Dependencies: `SceneCanvas.VectorPathTool.cs` requires `SnapEngine`, `SnapCandidateBuilder`, `VectorPathHitTester`; `NodeEditToolbar.xaml.cs` requires the new public canvas methods; the `SelectionPropertiesBar` button requires `EnterNodeEditMode` to be public and the `TargetCanvas` binding in `MainWindow.xaml`. `VectorPathEditor.ReverseSubpath` and `JoinAtEndpoints` already exist at HEAD, so `VectorPathJoinTests` should pass on HEAD alone. `PositiveCountConverter`, `SelectedNodeCount`, `IsSelectedSubpathClosed` exist at HEAD.
Tests: 38 of the 47 new test cases (3 + 8 + 3 + 13 + 3 + 8, counting theory rows).
Risk: medium-high (largest diff, interactive editor, mouse-capture/cancellation semantics, no UI-level automated test beyond source-text assertions). Recommendation: needs hunk split (MainWindow.xaml and SelectionPropertiesBar.xaml), then commit; optionally split the Core helpers plus their tests into a first commit (they are self-contained) and the canvas wiring into a second.

### G7 Designer tool rail: shape picker and chat entry

Files:
- `Lasero.App/Views/DesignerToolRail.xaml` +244/-95, `DesignerToolRail.xaml.cs` +225/-0
- `Lasero.App/MainWindow.xaml.cs`: the `public void FocusCanvas()` hunk
- `Lasero.Tests/MainWindowNavigationTests.cs`: the first two hunks (rail assertions: `Tag=` picker cells, `OnShapePickerItemClick`, `OnShapeButtonMouseRightButtonUp`; comment text)

Purpose: the shape "Tvary" menu becomes a single shape-tool button with click versus 400 ms long-press or right-click opening a picker, hover motion on rail buttons (36 px buttons, `Button.Tool` base), and the chat button now calls `OpenKamilInDesigner` instead of `ShowChatCommand`.
Mixed hunks: `DesignerToolRail.xaml` mixes the shape picker with general rail restyling (hover overlay, marker geometry, token sizes) that is arguably G13; separating them is possible but low value.
Dependencies: `ShapeToolIconConverter`, `ShapeToolTooltipConverter`, `IsShapeToolConverter` exist at HEAD (`Converters/ShapeToolConverters.cs` is tracked); `MainWindow.OpenKamilInDesigner` exists at HEAD.
Tests: the rail assertions in `MainWindowNavigationTests` (part of the 800; the test methods themselves already exist at HEAD).
Risk: medium (new gesture logic, timers, no behavioural test). Recommendation: needs hunk split (MainWindow.xaml.cs, MainWindowNavigationTests.cs).

### G8 Inspector: inline machine control

Files:
- deleted `Lasero.App/MachineControlWindow.xaml` (-65), `MachineControlWindow.xaml.cs` (-34)
- `Lasero.App/Views/DesignerInspectorView.xaml` +103/-21, `DesignerInspectorView.xaml.cs` +24/-7
- `Lasero.App/Views/MachinePanelView.xaml` +13/-152
- `Lasero.App/MainWindow.xaml.cs`: removal of `_machineControlWindow` field and `OpenMachineControl()`
- `Lasero.Tests/MainWindowNavigationTests.cs`: the inspector assertions hunk (`Text="Operace"`, `Text="Nastavení operace"`, `<views:MachinePanelView` now expected in the inspector, `OnMachineControlBackClick`, no `MachineControlWindow`)

Purpose: "Ovládání stroje" now swaps the inspector content to the shared `MachinePanelView` (an `IsMachineControlMode` dependency property) instead of opening a floating window; inspector headings lose all-caps; operation summary is a wrapping panel; `MachinePanelView` drops the fourth "Spuštění" job tab (`JobPanelTab`, its empty state and the job content, about 130 lines) and shrinks the tab grid from 4 to 3 columns.
Mixed hunks:
- `MachinePanelView.xaml` mixes the tab removal (functional) with button-style and `PanelTitle` swaps (G13). Also one line joined two elements on a single line (`Margin="0,6,0,10" />                        <TextBlock Text="Sériový port"...`), cosmetic.
- `DesignerInspectorView.xaml` mixes the machine-control mode with `Field.Quiet`, slider spacing and "Odstranit pozadí" ProgressBar/Cancel (G14: `IsIndeterminate`, `Scene.CancelBackgroundRemovalCommand`) and the operation summary WrapPanel.
Dependencies: `DesignerInspectorView` hunks for background removal reference `Scene.BackgroundRemovalIsIndeterminate` and `CancelBackgroundRemovalCommand`, which exist only in the dirty `SceneViewModel.cs` (G14). Committing the inspector without splitting those hunks will not build. `views:MachinePanelView` uses `MainViewModel` DataContext inheritance (assumed, unchecked).
Tests: `SelectionPropertiesLiveInOneContextualBarOverTheCanvas` is the test that fails at HEAD. From the diff, the HEAD assertion expects the exact string `<views:SelectionPropertiesBar HorizontalAlignment="Center" VerticalAlignment="Top" />`, but HEAD `MainWindow.xaml` already has more attributes after `Top` on following lines (inference). The same test method also asserts the new inspector state, so the test hunk and the inspector changes must ship together. The Job-tab removal has no test (a `JobPanelTab` search finds no remaining references).
Risk: medium (removes a user-visible job panel tab; the removed markup explicitly said job name and state live in the bottom status strip, but that was not verified). Recommendation: needs hunk split (DesignerInspectorView.xaml, MachinePanelView.xaml, MainWindow.xaml.cs).

### G9 Window title bar

Files:
- untracked `Lasero.App/Components/WindowTitleBar.xaml` (54), `WindowTitleBar.xaml.cs` (128)
- `Lasero.App/WindowFrameHook.cs` +36/-0 (Alt+Space opens the system menu; attach-once guard)
- `Lasero.App/PreviewWindow.xaml.cs` +1 (WindowFrameHook.Attach)
- Windows converted to `WindowStyle="None"` plus `WindowChrome` plus `WindowTitleBar`: `DeviceSettingsWindow.xaml` +13/-2, `KeyboardShortcutsWindow.xaml` +14/-2, `LoginWindow.xaml` +96/-24, `MaterialsWindow.xaml` +83/-6, `OnboardingWindow.xaml` +15/-4, `RasterImportWindow.xaml` +12/-1, `SettingsWindow.xaml` +13/-1, and `BitmapTraceWindow.xaml` (only the chrome part; see G10)
- `Lasero.App/LoginWindow.xaml.cs` +32/-0 (show/hide password toggle)
- `Lasero.App/LaseroDialogWindow.xaml` +12/-5 and `.xaml.cs` +43/-0 (entrance animation, button styles)

Purpose: consistent custom title bar across secondary windows; login window gains a reveal-password toggle and an in-button busy indicator; dialogs gain a short fade/scale entrance and explicit button styles.
Mixed hunks: `LoginWindow.xaml(.cs)` and `MaterialsWindow.xaml` mix the title bar with unrelated features (password reveal, tab restyle, button styles). `LaseroDialogWindow` is polish and could move to G13. `OnboardingWindow.xaml` also changes number weights (Bold to SemiBold).
Dependencies: all resources used by `WindowTitleBar.xaml` exist at HEAD (checked by key lookup). `RasterImportWindow.xaml` change is dead-code cosmetics after G4.
Tests: none. Risk: medium (custom chrome on 9 windows: resize borders, snap, DPI and caption-button behaviour not covered by tests; `LaseroDialogWindow` starts at `Opacity="0"` so a missed `Loaded` would leave a dialog invisible, the code handles the reduced-motion path). Recommendation: needs hunk split (LoginWindow, MaterialsWindow); leave BitmapTraceWindow.xaml to G10.

### G10 Bitmap trace: VTracer, Potrace, subpixel contours

Files:
- untracked `Lasero.Core/Trace/VTracerColorTracer.cs` (239), `PotraceCliTracer.cs` (199), `PotraceSvgParser.cs` (282)
- `Lasero.Core/Trace/BitmapTracer.cs` +121/-18, `BitmapTraceOptions.cs` +12/-6, `BitmapTraceResult.cs` +6/-3, `ContourExtractor.cs` +21/-3, `CompoundPathBuilder.cs` +2/-2, `BezierFitter.cs` +5/-4
- untracked `Lasero.App/Assets/Tools/vtracer.exe` (2,394,112 bytes, binary), `VTracer-LICENSE.txt`, `VTracer-SOURCE.txt`
- `Lasero.App/Lasero.App.csproj` +12/-0 (links the three tool files into output and publish), `THIRD_PARTY_NOTICES.md` +7/-0
- `Lasero.App/ViewModels/BitmapTraceViewModel.cs` +126/-26, `BitmapTraceWindow.xaml` +123/-19, `BitmapTraceWindow.xaml.cs` +145/-1 (mode selector, colour preview, zoom/pan preview, display-mode combo)
- `Lasero.App/ViewModels/SceneViewModel.cs`: the hunk in `ReplaceRasterWithTrace` that assigns traced objects to the result layer (+2 lines)
- `Lasero.Tests/BitmapTracerTests.cs` +41/-0 (circle radial-error check, colour-blocks test), `Lasero.Tests/SceneViewModelTests.cs` +80/-1 (new test `ReplacingBitmapWithColorTraceAssignsEditablePathsToFillLayersAndUndoRestoresBitmap` and `using Lasero.Core.Trace`)

Purpose: new `TraceMode.Color`; filled shapes are traced by Potrace (only if `LASERO_POTRACE_PATH` points at an executable) or by the bundled VTracer, falling back to the OpenCV pipeline when inversion or contrast is requested; subpixel (2x) contour extraction; defaults changed (MinimumFeaturePixels 8 to 2, SimplificationPixels 1.2 to 0.4, NoiseRemoval 0.2 to 0); fit tolerance now follows `SimplificationPixels` with a 0.2 floor; `NodeCount` added; traced colours become Fill layers with near-white layer disabled.
Mixed hunks:
- `BitmapTraceWindow.xaml`: title-bar chrome hunk (shell namespace, `WindowStyle="None"`, `WindowChrome`, row grid, `WindowTitleBar`, closing `Border`) belongs to G9; the rest is trace UI.
- `SceneViewModel.cs`: the two-line layer assignment is the only trace hunk in a file that also carries G14, G13 (palette) and encoding hunks.
- `SceneViewModelTests.cs`: the BOM/`using` change is incidental.
Dependencies: needs G9 (WindowTitleBar) for the XAML to compile. `SceneViewModelTests` new test needs `BitmapTraceResult.NodeCount` from this group. `Smoothness` still exists on `BitmapTraceOptions` and is validated, but `FitToleranceBasePx` no longer uses it; Potrace still does (`--opttolerance`). Whether Smoothness should remain a user-facing knob is unclear.
Tests: `Trace_ColorBlocks_ProducesEditableFilledLayersAndDisablesWhiteBackground` returns early when `VTracerColorTracer.IsAvailable` is false, so it passes vacuously without the binary; the test project references Lasero.App, which links the exe into output, so it probably runs (inferred). Potrace and its SVG parser have no tests at all. 2 of the 47 new tests are here (one in BitmapTracerTests, one in SceneViewModelTests; the radial-error assertion is an extra check in an existing test).
Risk: high (2.4 MB third-party binary added to git, default trace parameters changed for all users, external-process invocation, no Potrace coverage). The MIT licence and source pointer are included, and Potrace is opt-in via environment variable and fully qualified path. Recommendation: needs hunk split (BitmapTraceWindow.xaml, SceneViewModel.cs) and finishing (tests for Potrace parsing/args, decision on committing the binary versus fetching it at build time; `.artifacts/` trace experiments stay out).

### G11 Text rendering, motion tokens and reduced motion

Files:
- `Lasero.App/App.xaml.cs`: constructor hunk overriding `Window` metadata for `UseLayoutRounding`, `SnapsToDevicePixels`, `TextFormattingMode.Display`, `TextRenderingMode.ClearType`, `TextHintingMode.Fixed`
- `Lasero.App/UiAccessibility.cs` +4/-1 (zero durations for `Motion.VeryFast`, `Motion.Spatial`, `Motion.Panel` when animations are off)
- `Lasero.Tests/ThemeTokenTests.cs`: tests `WindowRenderingUsesDisplayClearTypeAndFixedHinting` and `MotionTokensUseRestrainedDesktopSpringAndRespectReducedMotion`

Purpose: pixel-grid text and 1 px lines on all windows, and make all motion tiers honour Windows "show animations". The `Motion.*` tokens, `Ease.Spring`, and the `Window` style with `TextHintingMode` already exist at HEAD (`LaseroTheme.xaml`), so only the startup override and `UiAccessibility` are new.
Mixed hunks: `App.xaml.cs` also carries the background-removal DI registration and the `GetRequiredService<MainWindow>()` change (G14, because `MainWindow`'s constructor gains a parameter). `ThemeTokenTests.cs` holds five new tests that belong to different groups (see G12 and the note below).
Dependencies: none beyond HEAD. Tests: the two named above (2 of 5 ThemeToken additions).
Risk: low. Recommendation: needs hunk split (App.xaml.cs, ThemeTokenTests.cs). `MinimizedAssistantIsOnlyTheBreathingArtwork` (the fifth ThemeToken test) exercises `KamilAssistantHost.xaml`, which is unchanged, so it can ride with this group; that it passes at HEAD is an inference.

### G12 Shell navigation: permanent sidebar, Kamil Designer-only, avatar alias

Files:
- `Lasero.App/MainWindow.xaml` +46/-121, `MainWindow.xaml.cs` +91/-126 (nav collapse removal: `ApplyNavRailWidth`, animation code, `IsNavCollapsed` handling; new `NavigationRailWidth = 164`; sidebar "Lasero Chat" button removed; Kamil host gets `x:Name="KamilHost"`, shown only on Designer, `AssistantClearance` margin binding removed; `AssistantResized` persisted to settings; `DeviceWizardOverlayHost.SettingsRequested` opens settings; bottom-bar dividers and margins)
- `Lasero.App/ViewModels/MainViewModel.cs` +0/-28 (`IsNavCollapsed`, `ToggleNav`)
- `Lasero.App/AppSettingsStore.cs` +0/-19 (`IsNavCollapsed`, `ExpandedNavWidth`, `CollapsedNavWidth`)
- `Lasero.App/Theme/LaseroTheme.xaml` +0/-74 (`Button.RailHandle`, `Image.KamilAvatar` alias)
- `Lasero.Tests/ThemeTokenTests.cs`: `ApplicationSidebarIsPermanentlyExpanded`, `AssistantAvatarsUseOneCanonicalArtworkAndReusableControl`

Purpose: remove the collapsible sidebar (fixed 164 px), restrict the Kamil assistant overlay to the Designer, drop the compatibility avatar alias now that no view uses it.
Mixed hunks:
- `MainWindow.xaml.cs` is the most mixed file in the tree: nav collapse (this group), background removal and DI (G14), machine window removal (G8), `FocusCanvas` (G7), Kamil resize persistence and device-wizard settings hook (unclear which fits best; here), BOM.
- `MainWindow.xaml` also has the `SelectionPropertiesBar` `TargetCanvas` hunk (G6) and cosmetic bottom-bar dividers (G13).
- `IconLabel.CompactMode` remains in `IconLabel.xaml.cs` although no one sets it now (dead property; the test only checks `MainWindow.xaml`).
Dependencies: `Image.KamilAvatar` removal requires G5 (ChatView) first; `MainWindow.xaml` also no longer uses it. `KamilAssistantHost.xaml.cs` already exposes `AssistantResized`/`AssistantSizeChangedEventArgs` at HEAD. HEAD's `ChatView.xaml` still binds `ShowChatCommand` nowhere after G7, see G5 note about `AppScreen.Chat` reachability.
Tests: the two ThemeToken tests named above (2 of the 5), plus the navigation test that must be green in the 800.
Risk: medium-high (touches startup layout and persisted settings; existing users' saved `IsNavCollapsed` JSON is simply ignored unless the serializer is strict, unchecked). Recommendation: needs hunk split.

### G13 Design-system polish sweep

Files (all cosmetic or token-level in the diff):
- Components: `EmptyState.xaml(.cs)`, `IconLabel.xaml(.cs)` (IsFilled/GridSize passthrough), `ParameterSlider.xaml` +37/-14 (thinner track, neutral fill, value input style), `ParameterRecommendationCard.xaml`, `ProcessStatusCard.xaml(.cs)` +10/-22, +102/-2 (animated chip colours; hard-coded literals with a comment that they mirror theme brushes), `ProjectThumbCard.xaml(.cs)` (animated hover), `DeviceStatusCard.xaml.cs` (default "—" to "Neznámo")
- Views: `HomeView.xaml` +26/-37, `DeviceView.xaml` +6/-6, `CanvasViewControls.xaml` +7/-7, `DeviceSetup/DeviceWizardOverlay.xaml` +3/-3 (`Glyph.Fill.Device` to `Glyph.Device`), `SelectionPropertiesBar.xaml` polish part, `DesignerToolRail.xaml` general restyle
- `DeviceSettingsWindow.xaml`, `SettingsWindow.xaml`, `MaterialsWindow.xaml` button-style parts
- `Theme/Icons.xaml` +6/-4 (Design/Materials/Device/Eye replaced by Lucide-derived paths)
- ViewModels microcopy: `HomeViewModel.cs` (dashes to "Nepřipojeno", "Neznámo", "Zatím žádný"), `ParameterRecommendation.cs` ("Neuvedeno")
- Layer palette: `SceneViewModel.cs` second swatch orange to cap green (57,78,59) and `Converters/LayerPaletteConverters.cs` orange name to "Tmavě zelená"
- `Lasero.Core/Layers/LayerSettings.cs` +6/-0 (`UsesFillInterval`; no consumer found anywhere in App, Tests or Core, so it is unused as of now)
- `Lasero.Core/Scene/SceneDocument.cs` +1/-1 (comment)
- Untracked assets `KamilAvatarCircular.png`, `KamilAvatarGenerated.png` (1.1 MB), `KamilAvatarSidebar.svg`: no source, XAML or csproj references them (only a theme test asserts the SVG is NOT referenced). See G17.

Purpose: unify buttons, glyphs, spacing and hover/status motion.
Mixed hunks: shares files with G6, G7, G8, G9, G12 (see above). Palette change is in `SceneViewModel.cs` alongside G10/G14/encoding hunks.
Dependencies: `Button.Secondary`, `Button.Outline`, `Button.Tool`, `Size.Control.*`, `Size.Icon.Hero`, `PanelTitle`, `Glyph.Fill.Materials` exist at HEAD. The palette colour change has no test (a LayerPalette test search finds none).
Tests: none directly; wide visual surface with no screenshot baseline in the diff.
Risk: low-medium (palette colour change may alter saved or previously mapped colour names; the em-dash to word replacements are visible copy changes). Recommendation: needs hunk split; then commit as several small commits (icons; components/motion; views; copy; palette).

### G14 Background removal via Gemini (cloud)

Files:
- untracked `Lasero.Core/BackgroundRemoval/GeminiBackgroundRemovalService.cs` (473), `IBackgroundRemovalService.cs` (18)
- `Lasero.Core/BackgroundRemoval/BackgroundRemovalService.cs` +6/-1 (local ONNX service now implements the interface)
- `Lasero.App/ViewModels/SceneViewModel.cs`: `BackgroundRemovalIsIndeterminate`, `BackgroundRemovalCancelRequested`, `CancelBackgroundRemoval`, `CommitBackgroundRemoval` now returns `bool`
- `Lasero.App/MainWindow.xaml.cs`: consent dialog, cancellable `RunBackgroundRemovalAsync`, temp-file cleanup, constructor takes `IBackgroundRemovalService`
- `Lasero.App/App.xaml.cs`: DI registration of the Gemini service using `AccountViewModel.GetIdTokenAsync` and `GetRequiredService<MainWindow>()`
- `Lasero.App/Views/DesignerInspectorView.xaml`: indeterminate bar, cancel button, tooltip

Purpose: replace the offline model download flow ("Stažení AI modelu") with an authenticated cloud call (consent dialog, then send the image to Lasero's Gemini proxy). The local `BackgroundRemovalService` stays but nothing registers or instantiates it any more, so the offline path is unreachable from the UI; the ONNX model notice in `THIRD_PARTY_NOTICES.md` is now stale (both product decisions, unclear).
Mixed hunks: see G8, G11, G12 (same files).
Dependencies: `MainWindow` ctor change requires the `App.xaml.cs` DI change in the same commit. `AccountViewModel.GetIdTokenAsync` exists at HEAD.
Tests: none. `SceneViewModelTests` still call `CommitBackgroundRemoval` (bool return is compatible). No test for the 473-line service, for cancellation, or for cleanup of the uncommitted result file.
Risk: high (privacy: uploads user images to a remote model; behaviour and product-direction change; untested network/parse code). Recommendation: needs finishing (tests with a fake `HttpMessageHandler`, product confirmation, decide the fate of the ONNX path and the notice).

### G15 Machine-safety leftovers

Files:
- `Lasero.App/ViewModels/ConnectionViewModel.cs` +2/-0 (subscribes to `IGrblDeviceProfileSource.DeviceProfileChanged` and sets `DetectedDevice`; the interface exists at HEAD)
- `Lasero.App/ViewModels/ConsoleViewModel.cs` +3/-1 (console commands that set `$30=` or `$32=` now go through the same confirm dialog as physical-motion commands)

Purpose: appears to follow HEAD commit `0c61efd` (layer power scaled by controller `$30`, laser-mode gate); the link is inferred, not stated in the diff.
Mixed hunks: none. Dependencies: none. Tests: none in the dirty tree.
Risk: medium (machine-facing). Recommendation: needs finishing (a small unit test for the regex and for the profile hook), then commit alone.

### G16 Avalonia experiment and agent skills tied to it

Files:
- untracked `Lasero.Avalonia/` (351 files; 291 under `publish/` are binaries; about 60 source files incl. `MIGRATION.md`, view models, views) and `Lasero.Avalonia.Tests/` (13 files)
- untracked `.agents/skills/avalonia`, `avalonia-layout-zafiro`, `avalonia-viewmodels-zafiro`, and the `.claude/skills/avalonia*` symlinks pointing to them
- `skills-lock.json` +18/-0 (only the three Avalonia entries)

State: not in `LaseroDesktop.sln`, so it does not affect the 800/800. Project memory records that Avalonia is abandoned and WPF is the only real target, so committing it is probably unwanted (decision belongs to the owner). Comments in `BitmapTraceResult.cs` still mention Lasero.Avalonia as a consumer of `Document.Shapes`.
Recommendation: do not commit as is. If kept, add `Lasero.Avalonia/publish/` to `.gitignore` and commit only source; otherwise move outside the repo. Do not commit the `.claude/skills` symlinks.

### G17 Build junk and orphan assets

- `.artifacts/` (964 MB, 492 files including 98 files in `trace-engines-build` plus other `trace-*-build` folders, compare scripts, before/after PNGs): experiment output for G10; delete or add to `.gitignore`.
- `.uiqa/app-stdout.log`, `.uiqa/app-stderr.log`: logs. `.uiqa/*.png` are already ignored.
- `Lasero.App/Assets/KamilAvatarCircular.png`, `KamilAvatarGenerated.png`, `KamilAvatarSidebar.svg`: unreferenced (see G13).
- Tracked but noteworthy: `desktop-current.png`, `desktop-designer-current.png`, `desktop-window-current.png`, `test-import.svg`, `Nový priečinok` exist at repo root (not part of the dirty set; not evaluated).

## 3. Which groups the 800/800 depends on

New test cases add up to 47, matching 753 to 800 total: ChatPresentation 3, NodeEditPerformance 8 (2 facts + 6 theory rows), SceneCanvasInteraction 3, SnapEngine 13, VectorPathHitTester 3, VectorPathJoin 8, ThemeToken 5, SvgImporter 2, BitmapTracer 1, SceneViewModel 1. Separately, the one test failing at HEAD (752 of 753 passing) turns green, giving 800 of 800.

| Test(s) | Requires |
|---|---|
| ChatPresentationTests (3) | G5 (`ChatResponseNormalizer`) |
| SnapEngine, VectorPathHitTester, NodeEditPerformance (22) | G6 Core helpers only |
| VectorPathJoinTests (8) | HEAD only (inferred: uses `VectorPathEditor` from HEAD) |
| SceneCanvasInteractionTests (3) | G6 canvas code (`SceneCanvas.xaml/.xaml.cs`); one test also inspects mouse-move and mouse-up regions that exist at HEAD |
| SvgImporterTests (2) | G3 |
| BitmapTracerTests (+1) | G10 |
| SceneViewModelTests (+1) | G10 (`NodeCount`, layer assignment hunk) |
| ThemeToken WindowRendering + Motion (2) | G11 |
| ThemeToken Sidebar + Avatars (2) | G12 (and G5 for ChatView) |
| ThemeToken MinimizedAssistant (1) | HEAD only (inferred) |
| MainWindowNavigationTests (fix) | G7 (rail hunk) and G8 (inspector hunk, MainWindow.xaml unchanged for this one) |
| JobCancellationSafetyTests | G1 (timing headroom) |

Groups not needed for the 800: G2, G4, G9, G13, G14, G15, G16, G17. They must still compile with everything else, so G9/G13/G14 hunks that share files with 800-relevant groups cannot simply be dropped without splitting.

## 4. Hunk-split cheat sheet (files that carry more than one group)

| File | Groups in it |
|---|---|
| `Lasero.App/MainWindow.xaml.cs` | G7 (FocusCanvas), G8 (OpenMachineControl removal), G12 (nav collapse, Kamil, wizard hook, bottom bar), G14 (bg removal, ctor, DI), encoding BOM |
| `Lasero.App/MainWindow.xaml` | G6 (TargetCanvas), G12 (nav, Kamil host, chat button), G13 (bar dividers, margins) |
| `Lasero.App/App.xaml.cs` | G11 (ctor overrides), G14 (DI + `GetRequiredService<MainWindow>`) |
| `Lasero.App/ViewModels/SceneViewModel.cs` | G10 (trace layer), G13 (palette), G14 (bg removal state/commands), encoding comment repairs |
| `Lasero.App/Views/DesignerInspectorView.xaml` | G8 (machine mode, headings), G13 (Field.Quiet, spacing), G14 (bg removal progress/cancel) |
| `Lasero.App/Views/SelectionPropertiesBar.xaml` | G6 (node-edit button), G13 (quiet fields, BarDivider removal, glyph change) |
| `Lasero.App/Views/MachinePanelView.xaml` | G8 (job tab removal), G13 (styles) |
| `Lasero.App/Views/DesignerToolRail.xaml` | G7 (picker), G13 (general restyle) |
| `Lasero.App/BitmapTraceWindow.xaml` | G9 (chrome), G10 (trace UI) |
| `Lasero.App/LoginWindow.xaml(.cs)` | G9 (chrome), password reveal and busy-in-button (feature, unlabelled) |
| `Lasero.App/MaterialsWindow.xaml` | G9 (chrome), G13 (tabs, buttons) |
| `Lasero.Tests/ThemeTokenTests.cs` | G11 (2 tests), G12 (2 tests), HEAD-only (1 test) |
| `Lasero.Tests/MainWindowNavigationTests.cs` | G7 (rail), G8 (inspector) |
| `Lasero.Tests/SceneViewModelTests.cs` | G10 only (plus BOM) |
| `Lasero.App/Theme/LaseroTheme.xaml` | G12 only |

## 5. Suggested procedure (no action taken)

1. Commit G1, G2, G3, G4 as they stand (excluding logs, Avalonia skills and `skills-lock.json`).
2. Commit G5 (fix the two strings first if desired). This alone repairs the silent `DisplayText` binding in the Kamil overlay.
3. Commit G6 Core helpers plus their tests, then the canvas wiring with `git add -p` on `MainWindow.xaml` and `SelectionPropertiesBar.xaml`.
4. Commit G7, then G8 (both need `git add -p` on `MainWindow.xaml.cs` and `MainWindowNavigationTests.cs`; expect the previously failing test to go green after G8).
5. Commit G9 (excluding `BitmapTraceWindow.xaml`), then G10 (decide first on the vtracer binary and add Potrace tests).
6. Commit G11, then G12, then the G13 pieces.
7. Hold G14 and G15 for tests and product sign-off. Park or drop G16 and G17.
8. After each commit, run the build and the full test suite; the brief's only known HEAD failure is the navigation test, so any other red result after a partial commit indicates a missed dependency from the tables above.
