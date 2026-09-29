# LASERO Designer — audit and proposed visual direction

## Revision 2 — visual restraint

The user rejected the first styling direction. Revision 2 supersedes the original visual-token and icon recommendations below, while retaining the audited application structure.

- Removed permanent borders from standard fields, buttons, operation rows, the context toolbar and canvas toolbar. Borders remain for structural panel separation, popovers, focus and selection.
- Context fields are 29px high, transparent at rest, with compact X/Y/W/H prefixes and tabular values. Hover/focus reveals their editing affordance.
- The rail keeps 36px hit areas, dark 18px icons, a neutral selected surface and only a 2px coral indicator.
- 30 UI icon mappings use Phosphor Regular; Device uses the user-requested LASERO laser-head geometry. The regular icons come from the official repository: https://github.com/phosphor-icons/core. SVG geometry is embedded in the HTML; the MIT license is included in PHOSPHOR-LICENSE.txt. No icon-font or network runtime dependency.
- Operation rows are 50px high, borderless, with 7px color dots, simplified power/speed summaries, and quiet actions revealed on hover or focus. Disabled output/visibility remains discoverable at rest.
- Laser sliders are neutral thin tracks with small outlined thumbs. Names and material controls have no default input outlines. Existing processing modes and mm/min units are preserved.
- Interaction accent is #B64B3A; text #1D1D1F; secondary #6E6E73; panels #FAFAF8; fields #F3F3F0; workspace #E8E8E4; borders #E3E3DE. Selection rows use #F3EFEA. Coral has been removed from operation text, ordinary controls and sliders.
- Rulers and grid lines are lighter. The artboard retains a subtle shadow. Visible nodes are reduced to 6px and rotation handles to approximately 6px diameter; the mockup retains its larger invisible node targets. Native WPF interaction targets have not been changed.
- Existing spatial zones, toolbar location, panel width, content and state controls are retained. No extra product UI was added.

Validation: inspected rendered 29px fields, 50px operation rows, a single SVG icon coordinate system, state switching and browser error logs. Standard editor chrome has no persistent control outlines; ruler ticks and structural separators remain. The prior mockup is saved as previous-v1.html for comparison. Native application code remains untouched.

---

Date: 7 September 2026. Scope: the current Návrh screen in `E:\lasero-desktop`, the supplied screenshot, and the user-approved attached brief. This deliverable is a visual prototype, before WPF implementation.

## Evidence and existing structure

The implementation is a WPF desktop application. The current source, including existing uncommitted work, takes precedence over stale descriptive documents. No application code was changed for this phase.

| Area | Current source and behavior | Proposed refinement |
|---|---|---|
| Shell | `Lasero.App/MainWindow.xaml`: title/project controls, central workspace, right inspector, bottom connection and job strip | Preserve these zones. Use a compact 48px title bar and 44px job strip in the mockup. |
| Left tools | `Views/DesignerToolRail.xaml`: selection, text, shape menu, line/path, import; materials, device, chat and settings below | Keep placement and existing vector icon assets. Consistent 18px glyphs in 36px targets; neutral hover and a restrained coral active indicator. |
| Context toolbar | `Views/SelectionPropertiesBar.xaml`: appears only with selection; X/Y, width/height, aspect lock, rotation, text settings, overflow menus | Retain this hierarchy rather than moving geometry controls into the laser inspector. Compact grouped fields, aligned units, chain affordance, neutral text controls. Horizontal scrolling remains the narrow-window fallback. |
| Workspace | `Controls/SceneCanvas.xaml.cs`: bed, rulers, zoom/pan, selection, resize/rotation | A cooler warm-gray surround gives white paper a clearer edge; lighter grids and muted rulers reduce competition with artwork. Rulers in the mockup are aligned to the artboard. |
| Vector editing | `Controls/SceneCanvas.VectorPathTool.cs`: path drawing, node selection, Shift multi-selection, handles, corner/smooth changes | Node editing already exists. Propose clear filled selected nodes, hollow unselected nodes, square corners, round smooth points, thin tangent lines, and an explicit compact node context bar. |
| Operations | `Views/DesignerInspectorView.xaml`: ordered operations with color, processing summary, output and visibility controls | Keep ordered rows. Sentence-case headings, compact 53px rows, distinct selection tint, legible summaries, and consistent action placement. |
| Laser properties | Same inspector: material selection, line/fill/both, power, speed, passes, fill interval | Preserve fields and order. 32px numeric controls, right-aligned values, tabular numerals, measured separation between groups. |
| Image selection | Same inspector: background removal/restoration and raster processing information | Keep contextual image actions above the operation list; show an actual raster sample using the existing Kamil asset. |
| Navigation | `Views/CanvasViewControls.xaml`: undo/redo, fit, zoom, fullscreen | Preserve one small connected toolbar in the lower right. Kamil remains above it. |
| Machine state | Existing inspector and bottom strip expose connection/job state | Keep compact status text and dots. Review-only examples demonstrate disconnected, ready, busy, paused, error; machine actions remain disabled. |

## Findings that affect implementation

1. `DESIGN.md` describes cobalt selection and bundled Inter, while the current theme uses coral `#E4513D`, warm surfaces, and Segoe UI Variable. It also describes older dimensions. Do not apply its older palette or font claims blindly. This phase records the discrepancy without rewriting the shared document.
2. The supplied screenshot and current code already have the correct spatial hierarchy. The improvement should come from typography, surface contrast, control grouping, and selection precision, not a new navigation architecture.
3. The contextual toolbar source explicitly explains why geometry fields were moved out of a full-width two-line toolbar. Preserve its conditional visibility and overflow strategy.
4. The current aspect-ratio control deliberately uses a padlock. The brief specifically requests a chain icon; the prototype demonstrates the existing `Glyph.Link` asset. A final WPF implementation should preserve the underlying lock behavior and accessible state.
5. Current node visuals are 9px nodes and 7px handles with larger invisible hit regions (18px and 16px). Refine visible geometry without reducing those interaction targets. Preserve hit targets at all zoom levels.
6. The source icon dictionary already provides a coherent family and laser-specific assets. Reuse it; a wholesale icon dependency change is unnecessary.
7. The operation panel should scroll internally on shorter windows. Do not shrink labels or remove laser parameters to force everything into view.
8. The repository contains extensive pre-existing modifications and untracked files. Prototype work is isolated under `docs/designer-refinement-prototype`; no cleanup, source reset, or broad rewrite is warranted.

## Visual direction

Warm graphite with restrained burnt coral. The proposed interaction color is `#BD4A38`, with a pale `#F8EEE9` selection surface; the darker text accent improves readability against white. The logo uses the existing supplied asset. Neutral primary/secondary text is `#242522` / `#686963`; panels `#FAFAF8`; fields `#F3F3EF`; workspace `#EAEAE6`; borders `#DFDFDA`.

Use the incumbent Segoe UI Variable stack, 13px body, 12px labels, medium/semibold section labels, and tabular numeric values. Controls use 6px radii, the floating toolbar 8px. Hover/press is neutral, focus is visible coral, and reduced motion suppresses transitions. Artwork colors in the bottom palette represent operations, not additional interface accent colors.

The mockup uses a small engraving project: a wooden tag/coaster outline, a leaf path, editable lettering, and a raster portrait. All material and laser values are illustrative, not validated machining presets.

## Review the prototype

Open `index.html` directly in a desktop browser. It is self-contained, with embedded local assets and no network dependency. Use the bottom review strip to switch between **Bez výběru / Objekt / Obrázek / Text / Vektor / Uzly**, or click artwork and operation rows. The review strip is a prototype control, not proposed production UI.

- Object/text/vector/image selection and node overlays demonstrate their different contexts.
- Text font, weight and size controls affect sample lettering; renaming the text operation updates the sample text.
- Power and speed inputs synchronize with sliders and the operation summary.
- Operation reordering, output/visibility toggles, processing modes, palette selection, and limited operation undo/redo are interactive.
- Project, shape, layer/object, and overflow menus demonstrate compact popup styling.
- Node state offers selected/multiple nodes (Shift-click), corner/smooth visual treatments, and open/closed path examples.
- Zoom buttons scale the sample artwork; fit returns the reference view. This is illustrative viewport behavior, not the WPF viewport implementation.
- Image background control demonstrates surface removal/restoration only; it does not run image segmentation.

Limitations: position/size/rotation fields demonstrate control styling, not real geometry transformations. Node dragging, import/export, actual alignment, duplication, deletion, full undo history, project persistence, machine settings, chat, and material libraries are not implemented. Commands outside the prototype scope report that limitation. The machine-status selector is explicitly labeled as an example and cannot enable laser execution.

## Validation

- Browser-checked all six selectable states and their pressed indicators.
- Checked image action visibility, node open/closed action labeling, and overflow menu entries.
- Changed power from 30 to 42: numeric input, slider value and operation summary all reflected 42.
- Detected and repaired wrapping operation actions; measured row height after repair approximately 53 CSS pixels.
- Checked internal panel scrolling, conditional toolbar overflow, and absence of whole-document horizontal overflow at the observed narrow desktop viewport.
- No browser error logs were reported during the tested interactions.
- The in-app browser's viewport override did not remain stable, so these checks do not establish exact 1366px/1920px or Windows DPI parity. Native WPF rendering and high-DPI verification belong to the implementation phase.
- No WPF build or .NET tests were run: no application code, bindings, geometry, persistence or machine logic changed.

## Implementation after visual review

1. Apply scoped Designer styles and semantic tokens first, retaining all existing bindings and command gates.
2. Refine the rail, contextual toolbar, ordered operation rows, and numeric/slider controls in their existing views.
3. Refine canvas rulers and selection/node rendering separately, preserving screen-space hit targets, undo, and coordinate conversion.
4. Verify native states at normal and high DPI, narrow and full-width windows; build and run the existing .NET test suite. Update the design documentation to describe the approved final implementation.

The current request ends at this audit and reviewable mockup. The attached brief calls for showing the proposed direction before major implementation.

