# Lasero vector editor gap audit — 2026-10-13

Scope: current WPF editor in `Lasero.App`, editable geometry in `Lasero.Core`, project persistence and SVG interchange. This is a code audit, not an application or hardware verification. User requested no tests or build in this pass.

## Current capabilities observed in code

- Canvas: click and Shift/Ctrl selection, marquee, Alt cycling through overlapping hits, selected-bounds dragging, move/resize/rotate, pan/zoom, one scene command per completed drag. Object visibility and lock checks use `SceneViewModel.CanSelectObject`.
- Nodes: draw paths, edit anchors and handles, drag segments, insert/delete nodes, break/join/open/close/reverse paths, and node marquee. `VectorPath` retains cubic controls; `LocalShapes` is derived for rendering and toolpaths.
- Geometry: pairwise Boolean preview, Weld, Divide/Fracture, compound paths, linked offset, grouping without flattening children. SVG export emits cubic path commands.
- Document: scene command stack, layer visibility/lock/output settings, clipboard, save/load, raster import and trace.

## Changes in this pass

- Lost mouse capture or window deactivation now cancels every active canvas gesture, restoring move/resize/rotate previews without creating history.
- Move snapping uses selection bounds anchors and a movement threshold. Node dragging can snap to other eligible visible objects.
- Node deletion drops degenerate contours; deleting the final contour uses an undoable object deletion.
- Node edit rebuild/preview retains each contour's layer, colour, geometry set and output mode. Canvas cubic rendering and fill hit testing use the same per-layer compound grouping.
- Curve Boolean input now uses Nonzero winding, matching canvas and offset fill; source groups normalize before region splitting.
- Clipboard captures geometry and layer settings at Copy time. Cross-project Paste reconciles layers in one undo step.
- SVG export uses current layer visibility/colour/mode, Nonzero compound fill, cubic commands, millimetre dimensions and Y-axis reflection; missing visible raster sources fail explicitly. SVG import now skips inline `display:none` / `visibility:hidden` geometry and rejects unsupported visible text, images, references, clips and masks.

## Open gaps / priority

| Priority | Gap | Impact |
|---|---|---|
| P0 | SVG importer still lacks CSS stylesheet cascading and many advanced SVG constructs. Unsupported visible content now fails for direct text/image/use/clip/mask cases, but external files need round-trip review. | Complex SVG can still differ from source; inspect before generating a machine job. |
| P0 | Removing a contour from a multi-layer editable path is rejected when the remaining metadata cannot be mapped unambiguously. | User must separate paths before topology-changing edits. Rejection prevents silent reassignment. |
| P1 | Region splitting still relies on flattened containment for topology classification. | Tiny, tangent or nearly touching curved islands need further kernel-level handling. |
| P1 | Marquee uses box intersection in both drag directions; Escape after marquee start does not restore prior selection. | Selection differs from professional left-to-right enclosing/right-to-left crossing convention. |
| P1 | `Ctrl+A` during Node Edit selects scene objects rather than nodes. | Keyboard workflow changes context unexpectedly. |
| P1 | Snapping has no hysteresis or true smart guides; candidate generation can be expensive in large SVG scenes. | Jitter and latency possible in dense documents. |
| P1 | Group editing has no enter-group mode or navigable object tree. | Individual editing in nested groups needs extra steps. |
| P1 | SVG import with mixed contour metadata loses editable `VectorPath` model for the whole imported object. | More imported artwork remains polygonal for node editing. |
| P1 | No measured 100/1,000-object or 10,000-node performance data. | Responsiveness claims remain unverified. |

## Interaction benchmark

The interaction model follows Lasero's own shortcuts rather than mixing three conflicting sets. [LightBurn Edit Nodes](https://docs.lightburnsoftware.com/latest/Reference/EditNodes/) documents direct node/handle/segment dragging and snapping; [Illustrator Direct Selection](https://helpx.adobe.com/illustrator/using/tool-techniques/direct-selection-tool.html) documents Shift multi-selection and direct segment reshaping. [Illustrator Pathfinder](https://helpx.adobe.com/uk/illustrator/desktop/manage-objects/reshape-transform-objects/pathfinder-panel-overview.html) and [LightBurn Grouping](https://docs.lightburnsoftware.com/2.1/Reference/Grouping/) make grouping, Boolean results and component ownership distinct. Code changes above improve these workflows but do not establish parity.

## Manual checks for operator

1. Draw two overlapping same-winding paths and a compound path with a hole. Compare canvas fill, click hit, Boolean result and SVG export.
2. Move a selected shape by grabbing away from its centre; align its edge and centre at several zoom levels. Cancel during drag with Escape and Alt+Tab.
3. Edit a multi-layer compound path's existing nodes; confirm each contour keeps its layer. Try deleting a contour and note the explicit limitation.
4. Copy text/vector, edit the original, Paste; pasted content should reflect Copy time. Repeat after loading another project.
5. Export SVG with curves, holes, hidden layers and bitmap; open in an external vector editor and compare orientation and size.

No tests, build, WPF run or laser operation performed in this pass per user instruction.
