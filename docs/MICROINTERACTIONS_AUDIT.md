# Microinteractions audit and polish pass (2026-09)

Read-only audit first (KamilAssistantHost, DeviceWizardOverlay, DesignerToolRail's shape picker,
LaseroTheme/SharedUiStyles motion tokens, a full `Storyboard|BeginAnimation|DoubleAnimation` sweep),
then a scoped implementation pass against the "must land" priority list. This document is that
audit's findings plus what was actually changed, what was deliberately left alone and why, and what
still needs a live pass to confirm feel/timing.

## What already had motion (left untouched, used as reference)

- `Lasero.App/Views/Kamil/KamilAssistantHost.xaml(.cs)` — full state-driven animation system
  (`ApplyState`/`AnimateSurface`/`Fade`), reduced-motion check, animation-cancellation via
  `BeginAnimation(..., null)` before starting a new one. Reference implementation; not modified.
- `Lasero.App/Views/DeviceSetup/DeviceWizardOverlay.xaml(.cs)` — sheet open/close, step crossfade,
  check-mark pop, staggered capability rows. Not modified. Its own report flagged that layer sizing is
  `Auto` rather than animated (a snap between very differently-sized steps). Investigated and left
  alone: animating `Width`/`Height` on these layers would violate the performance rule (GPU-friendly
  properties only) and the sizes involved (Intro vs. Scanning) differ enough that a size tween would
  read as a lurch rather than a polish; the existing crossfade already hides the jump reasonably well.
  Flagged as a "needs a live pass" item below rather than risked blind.
- `Lasero.App/Views/DesignerToolRail.xaml` — `ShapePickerPopup`/`ShapePickerItem` (the shape-tool
  long-press picker): open/close animation and hover-transition style. Reference implementation for
  popover motion and for the "Hover overlay fades, Selected overlay snaps" convention. Extended (not
  duplicated) to `RailTool`/`ShapeRailButton`, see below.
- `Lasero.App/Controls/SceneCanvas.VectorPathTool.cs` and the vector-path node/handle overlay — no
  animation, by design (see "Do not touch" below). Confirmed untouched.
- WPF's built-in `PopupAnimation="Fade"` on the `MenuItem`/`ComboBox` popups in `LaseroTheme.xaml`,
  `MainWindow.xaml`'s project-menu `Popup`, and `SelectionPropertiesBar.xaml`'s overflow-menu `Popup`.
  These already have *some* motion (the OS-level fade), so they were not in scope for the "any
  remaining un-animated popover" nice-to-have; left as-is.
- The implicit `Button` style in `LaseroTheme.xaml` (hover wash fade + press-scale to 0.96) is the
  base every `Button.*` style in the app derives from (`Button.Icon`, `Button.Ghost`, `Button.Tool`,
  `Button.GhostIcon`, `Button.Primary`, …), so ordinary icon-only `Button`s across toolbar/rail/chrome
  already share one consistent hover/press convention through inheritance. No change needed there.

## Motion tokens (confirmed / extended)

`Lasero.App/Theme/LaseroTheme.xaml` already had `Motion.Fast` (100ms), `Motion.Base` (180ms) and
`Ease.Out` (CubicEase, EaseOut). Two were missing from the spec's own baseline and have been added,
same file, same convention:

- `Motion.VeryFast` = 90ms (spec's 80-100ms band; existing `Motion.Fast` already covered
  100-140ms, so this fills the faster end rather than duplicating it)
- `Motion.Panel` = 200ms (spec's 180-220ms "dialog/sheet" band, distinct from `Motion.Base`'s
  general 180ms so a full-panel entrance can be told apart from an ordinary hover/press transition)

No parallel naming scheme was introduced; both new keys follow the existing `Motion.<Name>` pattern
and the existing single shared `Ease.Out`.

## Implemented (from the "must land" list)

1. **`LaseroDialogWindow.xaml(.cs)` entrance animation** (highest leverage: every dialog raised
   through this shell gets it, including the background-removal model-download prompt). The `Window`
   itself is not `AllowsTransparency`, so `Window.Opacity` is a no-op — the fade/scale/translate runs
   on the dialog surface `Border` instead (`DialogSurface`, with a `DialogScale`/`DialogOffset`
   `TransformGroup`), driven from a `Loaded` handler using `Motion.Panel` + `Ease.Out`, honouring the
   same `SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0` reduced-motion check as
   KamilAssistantHost/DeviceWizardOverlay. A real backdrop dim/fade over the owner window was not
   added — there is no existing scrim mechanism for the owner side of a `ShowDialog()` call, and
   building one is out of scope for this pass; deferred, see below.

2. **Clickable-card hover feedback.** Audited `Card`/`MetricCard`/`ThumbnailCard` in
   `SharedUiStyles.xaml` (pure `Border` styles, no hover state at all — correct for the static/
   informational cards that use them, e.g. `MaterialsWindow.xaml`'s swatch cards, which stay
   untouched per spec) and found the one genuinely clickable, currently-plain card:
   `Lasero.App/Components/ProjectThumbCard.xaml(.cs)` (Home's "recent project" card). It had an
   instant `BorderBrush` swap on hover and nothing else. Replaced with a short colour transition
   (`Brush.PanelBorder` → `Brush.Accent`, resolved once via `TryFindResource` rather than a second
   hardcoded copy of the theme) on a local per-instance `SolidColorBrush`, plus a -1 DIP lift via a
   `TranslateTransform`, both on `Motion.Fast`/`Ease.Out`. `MaterialsWindow.xaml`'s material-swatch
   card is a container for its own already-animated `Button` cells (base Button hover/press), not
   itself clickable — correctly left with no card-level hover per the "static cards get none" rule.

3. **`DeviceSetupViewModel`'s status card.** The actual binding is `DeviceView.xaml`'s
   `<ProcessStatusCard Status="{Binding DeviceSetup.Status}" .../>` — the reusable
   `Lasero.App/Components/ProcessStatusCard.xaml(.cs)` component (shared by every long-running-
   operation surface in the app, not only device setup). It previously swapped the chip tint, the
   glyph tint and the title/description text purely via `DataTrigger`s — instant. Implemented:
   - A short colour transition on the status chip and glyph (`ColorAnimation` on two local, per-
     instance `SolidColorBrush`es — `_chipBrush`/`_glyphBrush` — rather than animating the shared
     `DynamicResource` brushes, for the same "would leak across every consumer" reason already
     documented on the `Button` template in `SharedUiStyles.xaml`). `Motion.Base`/`Ease.Out`.
   - A soft content "reveal" on the title/description (`ContentStack.Opacity` 0.5→1, not 0→1 — the
     bound text has already changed synchronously underneath by the time the animation runs, so a
     real 0-opacity dip would just be a blank flash mid-transition, which the "state must win, no
     stale/blank state" rule forbids).
   - `IconData` (the glyph shape itself) stays an instant `DataTrigger` Setter — swapping a
     `Geometry` has no meaningful animated form.
   - Found `Lasero.App/Components/DeviceStatusCard.xaml(.cs)` (a *different*, differently-named
     component with an actual status-dot `Ellipse`) while investigating — it is dead code, not
     referenced from any view in the app. Left untouched; flagged here in case a future pass wants
     to delete it, but that is a cleanup decision outside this task's scope.
   - No continuous pulsing was added anywhere in the connected state, per the explicit prohibition.

4. **Background-removal result crossfade — deferred.** Traced how `SceneCanvas.xaml.cs` paints a
   raster object: the `Image` visual is created once in `TryLoadRasterPreview`/`CreateObjectVisuals`
   and cached in `_rasterImageVisuals`; `OnObjectPropertyChanged` only reacts to `Transform`,
   `IsVisible`, `IncludeInOutput` and `LocalShapes` — **not** `RasterFilePath`/`RasterOptions`. There
   is no live "swap this object's bitmap in place" code path today; getting a new processed image onto
   the canvas after background removal necessarily goes through a full visual remove/recreate
   elsewhere in the scene-sync flow. Adding a crossfade here would mean either instrumenting that
   remove/recreate path (which the task explicitly says not to risk touching) or reaching into raster
   visual creation to retain the old `Image` during a fade — both are exactly the "invasive change to
   the raster rendering pipeline" the spec pre-authorizes deferring. Deferred, not implemented.

5. **Icon-button/tool-selection consistency pass.** The base `Button` style already gives every
   `Button.*`-styled icon button consistent hover/press motion (see above) — no change needed there.
   The real inconsistency found: `DesignerToolRail.xaml`'s `RailTool` (the actual Select/Rectangle/
   etc. tool-selection `RadioButton`s — the primary tool rail) and `ShapeRailButton` used plain,
   un-animated `Trigger` `Setter`s for both hover and selected state, sitting in the very same file as
   `ShapePickerItem` (the shape picker's own items), which already had the animated version of exactly
   this pattern. Extended `ShapePickerItem`'s convention to both: a `Hover` overlay `Border` that
   fades 0→1/1→0 over `Motion.Fast`/`Ease.Out` on `IsMouseOver`, and a `Selected` overlay `Border`
   that snaps to visible on `IsChecked`/`Tag="True"` — matching `ShapePickerItem`'s own choice to
   animate hover but not selection (selection is a state change that should read as immediate/certain,
   not eased). The 3px active-marker bar and the foreground colour swap remain instant `Setter`s, same
   as before and same as the rest of the app's convention (the base `Button` template doesn't animate
   its own foreground/text colour either).
   `Toggle.Icon`/`Toggle.LayerState` in `SharedUiStyles.xaml` (used by `CanvasViewControls`,
   `SelectionPropertiesBar`, and the layers panel's per-row eye/bolt toggles) have the same
   un-animated `Trigger`/`Setter` shape as the old `RailTool`. Not touched in this pass — flagged
   below as a deferred nice-to-have rather than risked with the remaining time budget, since it
   touches three more files each with their own layout/trigger nuance to verify.

6. **OBRÁZEK section (background removal) loading-state transition.** In
   `DesignerInspectorView.xaml`, the inline spinner `Border` (`ProgressBar` + status text) switched
   `Visibility` on `Scene.IsRemovingBackground` with a plain converter binding — instant appear/
   disappear. Changed to a `Style`/`DataTrigger` that fades `Opacity` 0→1 over `Motion.Fast` on
   appearance (`Ease.Out`) and resets `Opacity` back to 0 with a zero-length `Storyboard` on
   disappearance (so `Visibility="Collapsed"` still removes it from layout immediately — disappearing
   stays instant, matching "should read as near-instant" for this category, and priming the brush for
   the next fade-in). This satisfies triage item 4/"refine transition timing" for the one loading
   indicator that was still a plain snap; the "Odstranit pozadí"/"Obnovit pozadí" buttons themselves
   already used the base animated `Button` chrome and needed no change.

## Explicitly left instant, per the spec's own hard rules

- **`SceneCanvas.VectorPathTool.cs` and the vector-path node/handle overlay** — not touched at all.
  Node/handle positions during a drag track the pointer 1:1, no easing, per the tool's own spec.
- **Canvas pan/zoom/object drag/resize/rotate/snap, selection box and resize/rotate handle
  positions, undo/redo repaints** — not touched. These stay fully deterministic and immediate.
- **`ParameterSlider` (Power/Speed) value while dragging** — not touched; track update stays
  immediate. (A hover-scale on the thumb itself was in the "only if time allows" tier and was not
  reached this pass — see below.)
- **`KamilAssistantHost`'s avatar breathe-pulse/press-scale** — checked against the spec's suggested
  ranges (hover ~1.03, press ~0.97): its actual values are close enough (breathe pulse is a subtle
  cue, press-scale ~0.96) that redoing already-correct work was not worthwhile; left as-is.
- **`OnResizeDragDelta` (Kamil panel resize)** — confirmed no animation is attached to the drag delta
  itself; only `BeginAnimation(..., null)` calls to clear any residual animation before setting the
  live size. Resize stays immediate. Nothing to change.
- **Window maximize/restore** — not touched; that transition is OS-owned.
- **Toasts/notifications** — grepped for `toast`/`notification`/`snackbar`-named components across the
  app; none exist. `LaseroDialogWindow` is a modal dialog shell, not a toast. Section skipped
  entirely rather than inventing a notification system, per the instructions.

## Deferred / left for a follow-up pass (not "must land", or ran out of budget)

- **Backdrop dim behind `LaseroDialogWindow`.** No owner-side scrim mechanism exists; adding one
  would mean instrumenting every window that can own a dialog. Out of scope here.
- **Background-removal canvas crossfade** — see item 4 above; requires touching the raster visual
  create/remove path, explicitly de-risked by deferring per the task's own escape hatch.
- **`DeviceWizardOverlay` layer `Auto`-sizing snap** between very differently-sized steps (Intro vs.
  Scanning) — investigated, left alone; see above.
- **`Toggle.Icon`/`Toggle.LayerState` hover/selected animation** (CanvasViewControls,
  SelectionPropertiesBar align toggles, layers-panel eye/bolt toggles) — same treatment as
  `RailTool` would be a natural next step, not done this pass.
- **Sidebar active-indicator moving between rows** — "only if time allows" tier; not reached.
  (Also: this app's primary navigation is the icon rail in `DesignerToolRail.xaml`/`MainWindow.xaml`'s
  nav buttons, not a text-label sidebar list in the Linear/Raycast sense — the closest analogue,
  `RailTool`'s marker bar, still snaps to visible/hidden instantly, matching the rest of this pass's
  choice to leave selection state changes un-eased.)
- **Any remaining un-animated popover/dropdown** beyond what's listed above — the grep sweep found
  only the `MenuItem`/`ComboBox` popups (already `PopupAnimation="Fade"`) and the two reference
  popovers (`ShapePickerPopup`, Kamil's own popover). Nothing else turned up.
- **`ParameterSlider` thumb hover-scale** — not reached.
- **Collapsible/`Expander`-style sections in `DesignerInspectorView.xaml`** — none exist (checked);
  section skipped as instructed.
- **Title-bar min/max/close button hover/press consistency** — `Button.Chrome`/`Button.ChromeClose`
  in `LaseroTheme.xaml` were read during the audit; they already use the same `Trigger`-based
  hover-fill pattern as the rest of the chrome buttons (just without the shared `Hover`/`Press`
  overlay Storyboard the base `Button` style uses, since they're deliberately minimal 26-32px chrome
  buttons). Judged consistent enough with the rest of the app's window-chrome conventions specifically
  (not the general control conventions) to leave alone rather than risk destabilizing the one part of
  the UI users interact with on every single window action.

## Reduced motion

Every animation added in this pass (`LaseroDialogWindow`, `ProcessStatusCard`, `ProjectThumbCard`,
`RailTool`/`ShapeRailButton`'s hover Storyboards) checks `SystemParameters.ClientAreaAnimation &&
System.Windows.Media.RenderCapability.Tier > 0` before running anything, exactly matching the
property/expression already established in `KamilAssistantHost.xaml.cs` and
`DeviceWizardOverlay.xaml.cs`. No second reduced-motion mechanism was introduced. Where the check
fails, the end state is applied immediately via a direct property set (never left in a transitional
value).

## State-must-win discipline

`ProcessStatusCard.ApplyStatusColors` and `ProjectThumbCard.AnimateHover` both call
`BeginAnimation(..., null)` before setting the immediate/no-animation value, and every
`BeginAnimation(..., new ...Animation{...})` call replaces whatever was previously running on that
property rather than layering a second Storyboard on top — the same discipline
`KamilAssistantHost.ApplyState`/`Fade` and `DeviceWizardOverlay.SetLayer` already use. A status change
mid-animation (e.g. Progress → Error while the Progress→Success colour tween is still running)
therefore always lands on the correct final colour, not a stale in-between one.

## Build/test result

- `dotnet build LaseroDesktop.sln -c Debug -m:1` → **0 Warnings, 0 Errors**.
- `dotnet test LaseroDesktop.sln -m:1` → **466/466 passing** (unchanged from baseline; this pass is
  XAML/animation-only and added no new testable non-UI logic, so no new tests were written).

## What still needs a live pass

This entire task category is inherently hard to verify from source alone. The app was not launched
for this task per the explicit constraint, so none of the following has actually been *seen* moving:

- Whether `Motion.Fast` (100ms) reads as calm rather than curt for the `RailTool`/`ShapeRailButton`
  hover wash at actual frame rate.
- Whether the `ProcessStatusCard` chip/glyph colour tween and the 0.5→1 content reveal together read
  as "one calm cue" rather than two competing motions when a real Progress→Success/Error transition
  happens with real device-connection timing.
- Whether the `ProjectThumbCard` -1 DIP lift is perceptible/tasteful at real DPI, or too subtle/too
  much alongside the border-colour tween.
- Whether `LaseroDialogWindow`'s entrance (200ms fade+scale+translate on the content `Border`, window
  chrome itself static) looks right against `WindowChrome`'s rounded corners in practice — there is a
  theoretical risk of a visible seam between the window's own corner rounding and the Border's scale
  transform during the first ~200ms, which is very hard to assess without rendering it.
- Whether the OBRÁZEK spinner's `Motion.Fast` fade-in is fast enough to not feel laggy when background
  removal starts, given the model-download dialog and inline processing both compete for attention in
  that flow.
- General overall "does this feel like Linear/Raycast/Figma, not like a pile of independent
  animations" impression, which by definition can only be judged by watching the running app.
