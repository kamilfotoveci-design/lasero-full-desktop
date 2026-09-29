# LASERO Designer — Claude implementation handoff

## Current state

The user requested a premium, restrained visual refinement of the current Návrh page with its architecture and spatial layout preserved. The first mockup was rejected for excessive boxes, borders, coral, and a utility/web-form appearance. **Revision 2 is the current reference.** The latest correction restores the screenshot's LASERO laser-head icon for Device instead of Phosphor's CPU symbol.

Native WPF implementation has **not started**. All work in this handoff is an isolated prototype/documentation package. Application code, machine behavior, bindings and geometry logic were not modified. No .NET build or tests have been claimed for this phase.

Project: `E:\lasero-desktop`. Package: `docs/designer-refinement-prototype`.

## Files to use

| File | Purpose |
|---|---|
| `index.html` | Current self-contained visual prototype. Open in a full desktop browser. |
| `source/prototype.html` | Editable HTML/CSS/JS before asset embedding. CSS is in its style block. |
| `source/icons.json` | Actual icon markup used by the prototype. |
| `source/build.cjs` | Dependency-free Node build; uses the project's existing wordmark/avatar assets. |
| `icons/*.svg` | 31 individual SVGs for implementation; includes corrected Device. |
| `ICON-MAP.json` | Prototype name → proposed WPF glyph mapping and rendering model. Verify existing keys rather than assuming every proposed key exists. |
| `PHOSPHOR-LICENSE.txt` | MIT license for Phosphor assets. |
| `AUDIT.md` | Original source audit and revision-2 changes; revision 2 overrides older aesthetic recommendations. |
| `previous-v1.html` | Rejected first version, comparison only. Do not implement it. |
| `CLAUDE-PROMPT.md` | Ready-to-paste implementation request. |

Rebuild from the repository root:

```powershell
node docs/designer-refinement-prototype/source/build.cjs
```

When using a copied package outside the repository:

```powershell
node source/build.cjs E:\lasero-desktop
```

No npm install, server, network access or browser framework is needed to build/open the artifact.

## Non-negotiable direction

- Preserve title/project bar, continuous left tool rail, dominant central canvas, conditional floating context toolbar, right operation/laser inspector, lower-right canvas toolbar and bottom machine/job strip.
- Reduce visible chrome. Ordinary buttons, fields and operation rows should not have permanent outlines. Keep structural separators, popover boundaries, focus and canvas selection.
- Keep existing tool locations and functional controls. Do not add inspector cards, extra navigation, sample UI, or explanatory banners.
- Preserve Segoe UI Variable and the existing numeric/typography resource system. Body 13px; section/field labels approximately 12px. The prototype's small ruler/metadata samples are not permission to make production controls illegible.
- Use coral sparingly: active tool indicator, focus, selection outlines and selected nodes. The active tool icon and operation text stay dark. Ordinary laser parameter sliders are neutral.

### Tokens and dimensions

| Role | Reference |
|---|---|
| App | `#F5F5F2` |
| Workspace | `#E8E8E4` |
| Panels | `#FAFAF8` |
| Fields/hover | `#F3F3F0` / `#EFEFEB` |
| Primary/secondary text | `#1D1D1F` / `#6E6E73` |
| Muted metadata | `#9B9B96`; do not use for required body text |
| Structural border | `#E3E3DE` |
| Accent | `#B64B3A` |
| Selected tool | `#EEE9E5`, dark icon, 2px accent indicator |
| Selected operation | `#F3EFEA`, no border |
| Context fields | 29px high, 5–6px radius, transparent/subtle at rest, hover/focus affordance |
| Tool rail | 36px hit area, 18px icon |
| Operation row | 50px high, 7px color dot |
| Inspector | Existing width/resizing retained; mockup 320px, narrow reference 300px |
| Canvas toolbar | Single connected group, 8px radius, translucent off-white, minimal shadow |
| Motion | 120ms hover/press; about 140ms popovers; respect reduced motion |

Use these as visual targets within the current native layout. Do not override production minimum widths or remove panel resizing simply to copy an HTML number.

## Device icon correction

The user's screenshot is the current `Glyph.Device` laser head. Its geometry is:

```text
M8,3 H16 V9 H8 Z M3,5 H8 M16,5 H21 M10,9 V12 H14 V9 M12,12 V17 M9,16.5 L7.5,18 M15,16.5 L16.5,18 M5,18 H19 L21,21 H3 Z
```

Use this silhouette for machine/device navigation, with round caps/joins and approximately 1.6 units of stroke on its 24×24 grid. It is the user-requested laser-specific exception to the Phosphor family. The supplied SVG wraps the 24px geometry in a scale transform to share the other assets' 256px viewBox. In WPF, use the native 24px geometry directly; do not apply both scales. The resource already exists in `Lasero.App/Theme/Icons.xaml` and may require no geometry change.

### Important Phosphor/WPF rendering mismatch

The current `Components/IconGlyph.xaml` renders a stroked `Path` on a 24×24 `Canvas`, with transparent fill. Phosphor Regular SVGs are **filled outline silhouettes on a 256×256 grid**, not centerline strokes. Copying their path data into the current component without adapting rendering will create thick/doubled/clipped icons.

Use a scoped filled-icon renderer or an explicit opt-in rendering mode; normalize geometry by 24/256 where necessary. Keep existing stroke-mode behavior as the default for other screens and for Device. Preserve SVG fill rules and multiple paths. Do not globally change every `IconGlyph` to filled rendering. Preserve command semantics, automation names and tooltips.

Source: https://github.com/phosphor-icons/core. Keep the included MIT license with redistributed assets.

## Native implementation map

| File/area | Intended work |
|---|---|
| `Lasero.App/Views/DesignerToolRail.xaml` | Continuous ghost-tool strip, regular icons, neutral selected surface, 2px indicator, hover/focus states. |
| `Lasero.App/Views/SelectionPropertiesBar.xaml` | Compact prefix-label geometry fields; lighter surfaces; small centered chain; regular rotation icon; text controls and overflow stay in place. |
| `Lasero.App/Views/DesignerInspectorView.xaml` | Borderless operation rows and fields, simpler power/speed summaries, quiet actions on hover/focus, neutral sliders, section spacing. Preserve processing mode information elsewhere in the inspector. |
| `Lasero.App/Views/CanvasViewControls.xaml` | One connected low-contrast control group. |
| `Lasero.App/Controls/SceneCanvas.xaml.cs` | Subtle grid/rulers/artboard and precise selection adorners; do not alter coordinate conversion. |
| `Lasero.App/Controls/SceneCanvas.VectorPathTool.cs` | Restrained node/tangent styling while retaining drag behavior and invisible hit regions. |
| `Lasero.App/Theme/Icons.xaml`, `Components/IconGlyph.*` | Verify mappings, adopt filled icons safely and locally, preserve Device. |
| `Lasero.App/Theme/LaseroTheme.xaml`, `Theme/SharedUiStyles.xaml` | Reuse resource keys. Scope overrides to Designer to avoid unintended product-wide restyling. |
| `Lasero.App/MainWindow.xaml` | Touch only where required for Designer chrome; do not restructure shell/navigation or change workflows. |

The file paths above are repository-relative. Read their current contents before editing; there is substantial pre-existing uncommitted work.

## Behavior to preserve

- Actual X/Y/size/rotation editing, aspect lock, text editing and undo/redo.
- Operation order, layer color identity, visibility and output flags. Quiet actions must remain visible on keyboard focus; show off/error states even without hover.
- Current line/fill/both modes and **mm/min** speed units. The user provided mm/s as an aesthetic example, not a request to change machine units.
- Image processing and background removal workflows, including existing progress/failure states.
- Screen-space hit targets, pointer capture, multi-selection, snapping, panning, zoom, and keyboard navigation.
- Existing frame/start preflight and confirmations; real device states must remain derived from actual connection/controller state.

Visible handles may become smaller, but invisible targets must not shrink. The inspected node implementation uses 18px node hit targets and 16px tangent-handle hit targets. Verify current values before changing anything. Avoid new animation or full rerendering during pointer manipulation.

## Prototype limitations — do not port these

The bottom state selector is review-only. Machine status examples are simulations. Geometry numbers are mostly static styling samples; precise transforms/node dragging are not implemented. Background removal only toggles a sample surface. Some commands show explanatory toasts instead of executing. Undo is limited to some sample operation edits. Zoom is a sample SVG scale, not the actual WPF viewport. Artwork and laser settings are illustrative.

Do not put any of these shortcuts into production. The existing WPF behavior is the source of truth for functionality.

## Source/document conflicts

`DESIGN.md` and `CLAUDE.md` contain older cobalt/Inter or layout references. The current code had already moved to coral and Segoe UI Variable, and the user explicitly requested the latest restrained coral direction. Use current user-approved task constraints for this redesign; do not revert to cobalt to match stale prose. Update documentation only for changes actually implemented.

## Suggested delivery sequence

1. Record `git status`/baseline diff. Read the relevant current views and local instructions. Protect unrelated work; do not reset or clean the tree.
2. Implement scoped Designer typography/surfaces and the four surrounding control areas.
3. Integrate regular filled icon rendering safely, preserving the custom Device symbol.
4. Refine selection/rulers/nodes separately, retaining native interaction semantics and hit targets.
5. Build, test, and visually inspect the native app. Fix genuine regressions without broad architecture refactors.

## Acceptance and verification

- Same spatial layout and workflows as the current Designer; no extra UI, nested cards, or web-view replacement.
- No permanent outlines around standard fields/buttons/operation rows; focus stays visible.
- One dark regular icon treatment, with the laser-head Device icon; no CPU substitute, double-stroked silhouettes or clipped glyphs.
- Only the intended tool is active; selection/focus/node accent remains visible without coloring the rest of the inspector.
- All selection contexts work: none, shape, image, text, vector, node edit. Verify selected/multiple nodes, corner/smooth and open/closed paths.
- Hover and keyboard focus expose quiet operation actions; disabled/off states remain understandable.
- Numeric editing, units, parameter validation, ordering, transforms and undo retain real application behavior.
- Canvas selection uses approximately 1px lines and 6px visible handles at screen scale, with original larger hit targets.
- Inspect a typical 1920×1080 window and the actual minimum supported size; check 100%, 125%, and 150% DPI where available. Scroll the inspector on short windows; do not clip critical controls.
- Run the repository's build/test commands after native changes, inspect WPF binding/resource errors, and report actual results. Do not require a physical laser for tests or activate one during visual QA.

Prototype verification already performed: six state switches, context menus, synchronized power value/slider/summary, 29px context fields, 50px rows, single active-tool state and no observed browser error logs. These checks do not establish native WPF parity or exact high-DPI layout correctness.
