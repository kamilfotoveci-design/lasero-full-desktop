# Release-candidate usability review

> Historical UI review from 2026-09-29. Its issue counts and open-item list are not the current release status. See [current release readiness](release-readiness-2026-10-05.md) for the 2026-10-05 recheck and [current machine scope](machine-priority-research-2026-10-05.md).

Date: 2026-09-29. Reviewer: final agent, fresh-eyes pass on origin/design-system-tokens (73dbb8f) built in an isolated worktree, driven through the .uiqa scripts with the virtual GRBL simulator. No real hardware was connected. Screenshots: `E:\rc-shots\` (01-launch, 02-designer, 03-rect, 06-ctx, 07-ctx-obj, 08-device, 09-connected, 10-frame, 11-start-confirm, 13/16/17-nodeedit, 14-overflow, 15-path-draw, 18-undo (before) vs 25/26-undo-fixed (after), 19-materials, 20-settings, 24-xy-fixed, 27-start-size (before) vs 29-start-size-fixed (after), 31-size-1366, 31-size-1920, 32-size-min, 33-selectall, 35-blocked-dialog).

Personas walked: A first-time beginner (launch, new project, draw, connect, start), B LightBurn maker (shortcuts, node edit, layers panel, frame, start dialog), C fast pro (V/R/E/L/T keys, Ctrl+A, arrows, Ctrl+Z, right-click, Save, reopen).

Test counts: before 1018 passed / 1 failed (1019); after 1021 passed / 1 failed (1022). The single failure is the known `MainWindowNavigationTests.SelectionPropertiesLiveInOneContextualBarOverTheCanvas`. `ThemeTokenTests.PressIsTheHoverWashAtAHigherStrength` passed in this run.

## RELEASE BLOCKERS

None found in what could be exercised without hardware. See the hardware checklist: the safety-critical behaviours (beam off on disconnect, pause) cannot be proven by simulator and are release gates for a human.

## FIXED ISSUES

1. P1 Job size and framing box included the machine origin. `GCodeParser` added the implicit start point (0,0) to the extent whenever the first move was a rapid. A job at x 38-233 / y 231-336 mm was reported as "Rozměr: 233 x 336,2 mm" in the Start dialog and Frame traced a rectangle from the bed corner. Now the start point counts only if the first move is a cutting move. Start dialog shows the true 292,6 x 106,4 mm. Test added (`FirstRapidTravelFromTheImplicitOriginIsNotPartOfTheJobExtent`). Files: `Lasero.Core/GCode/GCodeParser.cs`.
2. P1 X/Y fields lied for drawn paths. `SelectedX/Y` returned the transform offset, so a freshly drawn line showed X 0 / Y 0 while rectangles and text showed their centre, and typing a value moved the object relative to an invisible origin. Now always the world centre of the object (pivot + offset), setter converts back. Test added. File: `SceneViewModel.cs`.
3. P1 Undo after a node edit left a phantom node and stale selection outline, and the next edit could resurrect the undone change. Selection now follows the object Id to the restored instance after Undo/Redo, and node-edit mode re-syncs to it. Tests added. Files: `SceneViewModel.cs`, `SceneCanvas.xaml.cs`, `SceneCanvas.VectorPathTool.cs`.
4. P1 Pressing Start or Frame while blocked (for example "verify placement with Frame first") only changed a grey line in the status bar, so the button looked dead. The reason now appears in a dialog ("Spuštění není možné" / "Rámování není možné"). Files: `GCodeViewModel.cs` (`StartBlocked` event), `MainWindow.xaml.cs`.
5. P2 Rámovat, Pozastavit, Pokračovat, Zastavit and Spustit had no accessible name (screen readers and UIA saw empty buttons). Names added in `MainWindow.xaml`.

## REMAINING P1

1. No way to node-edit a rectangle, ellipse or polygon: double-click does nothing and there is no "Convert to curves" command anywhere (context menu, overflow, Edit menu). LightBurn users expect it. Needs a conversion command (feature, not done here).
2. Multi-selection hides X/Y/W/H fields; only align/flip/more remain. Pros expect group size and position.
3. Job-complete chip ("Dokončeno") and "Úloha připravena" stay green after the design is edited or Start is blocked; the state text can contradict the disabled-in-practice Start button.
4. Device header chip says "Připojeno - zjišťuje se typ zařízení" (also in Settings, Stav) after the device card already says "Gravírka je připravena". Stale wording after connect.

## POST-RELEASE P2/P3

P2
- Default new vector operation is Cut at 95 % / 350 mm/min with no material chosen. The Start dialog shows it, but a beginner is not warned it is a cut setting.
- Materials window uses default WPF tab headers ("Doporučené parametry", "Moje recepty") that do not match the rest of the app.
- Floating selection bar covers the top of the work area (y 340-400 mm) while objects are selected.
- No visible "Hotovo" in node-edit toolbar; exit is Esc/Enter only (hint text mentions it only while drawing).
- Shape tool group shows no active state when a shape tool is active (the rail glyph only changes for non-rectangle shapes).
- Opening `rc-test.lasero` leaves the project title "Nový projekt"; name does not follow the file.
- Status-bar completion message truncated at 1480 px ("Zkontrolujte výsledek na...").
- Operation list shows a horizontal scrollbar under the card at 1366 px and below; row buttons touch the edge.
- Start dialog wording "0,0 odpovídá pracovní nule stroje" is jargon.
- Empty-state Y ruler shows a clipped "400" label at the top edge.
- Home "Poslední úloha" shows an old completed job from earlier dev sessions ("0 s") on a fresh profile.

P3
- Start dialog estimate reads "Nevypočteno" by design until the simulation is opened.
- App has a single light theme, so Light/Dark verification is not applicable.
- Simulator completes any job in 0 s, so pause/stop UI can only be validated on hardware.

## REGRESSION RISKS

- `GCodeParser` bounding box change affects framing and preflight bounds for every job whose first move is a rapid (all generated jobs). Raster jobs use `RasterPlanner.Bounds`, unaffected. Loaded raw G-code that begins with a rapid from 0,0 no longer counts 0,0.
- `SelectedX/Y` semantics changed for objects whose pivot is not (0,0); typing values now positions the centre. Existing rectangle/text behaviour is unchanged (tests pass).
- `ReconcileSelectionWithScene` runs after every Undo/Redo and removes selected objects no longer in the scene; this alters selection after undoing an Add.
- Start/Frame blocked dialog is modal; any automation that clicks Start while blocked must dismiss it.

## MANUAL TEST CHECKLIST (real hardware)

1. Connect, then unplug USB or press Odpojit during a burn: beam must go off immediately.
2. Frame at 1 %: dot must be visible on a dark and a light material and never mark it; frame must match the job outline (fix 1 changes its size).
3. Frame a job placed away from the bed corner: the frame must sit around the artwork, not run from the origin.
4. Hold-to-fire / positioning laser: only fires while held, releases on window focus loss and on Esc.
5. Pause during a fill and a cut: beam off at once, Pokračovat resumes at the same spot without a burn mark.
6. Zastavit mid job: beam off, machine idle, Start again begins from the top.
7. Start with Frame requirement on: blocked until Frame completes, blocked again after any move or nudge.
8. Alarm/limit error mid job: banner text, beam off, Start disabled until reset.
9. Save, close, reopen a project with cut and fill layers and confirm settings persist.
10. Autosave recovery after killing the app: offer appears, restored design matches.

## Statement

No known P0 release blocker remains in the code paths that could be exercised with the simulator and static review. Hardware-only safety behaviour (checklist items 1, 4, 5, 6, 8) is unverified and must be confirmed by a human before shipping.
