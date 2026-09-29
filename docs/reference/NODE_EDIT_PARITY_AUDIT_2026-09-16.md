# Lasero Vector Node-Edit — Code-Grounded Parity Audit (2026-09-16)

Phase 1 audit only. No implementation performed. Produced against HEAD `cc0bead` (branch state as of
2026-09-16), cross-checked against `docs/reference/LIGHTBURN_VECTOR_PARITY.md` (behavioral spec) and
`docs/reference/LIGHTBURN_VECTOR_PARITY_GAP_MATRIX.md` (prior gap matrix, produced 2026-09-14 after
commit `985fafa`). This document verifies that matrix against current code, extends it with file:line
citations, and adds the architecture/command-parity/UI sections the mission requested.

## Overview

The editable vector model is `Lasero.Core/Scene/VectorPath.cs` (`VectorPath` → `VectorSubpath[]` →
`VectorNode` records with `Anchor`/`HandleIn`/`HandleOut`/`Corner|Smooth` type, double precision
throughout, `CubicBezier` De Casteljau split/flatten). Pure editing operations live in
`Lasero.Core/Scene/VectorPathEditor.cs` (`AppendNode`, `Close`, `Open`, `RemoveNode`, `InsertNode`,
`ConvertNodeType`, `MoveHandle`, `ConvertSegmentToCurve/Line`, `DragSegmentPoint`, `BreakAtNode`,
`DeleteSegment`) — all pure, immutable, unit-testable without WPF. `VectorPathDragSession`
(`Lasero.Core/Scene/VectorPathDragSession.cs`) owns the pre-drag snapshot/restore contract used by every
live-drag interaction. `VectorPathSceneFactory.cs` flattens `VectorPath` → `ImportedShape.Points` and is
the only place `SceneObject.VectorPath` is attached (`Create`) or carried forward (`Rebuild`).

The interactive tool is `Lasero.App/Controls/SceneCanvas.VectorPathTool.cs` (1089 lines), a partial class
of `SceneCanvas` with a handful of dispatch hooks into the ~2200-line main file
(`SceneCanvas.xaml.cs`, tagged `// VECTOR PATH TOOL HOOK`). There is no formal state machine (the spec's
§73 suggestion) — state is the `DragMode` enum (`None/Select/Move/Resize/Rotate/Pan/Draw/PathTool/
NodeEdit/NodeMarquee/NodeSegmentDrag`) plus a set of nullable fields (`_draggedHandle`,
`_draggedSegment`, `_nodeDragOriginalPath`, `_nodeDragSession`, `_selectedNodeKeys`,
`_hoveredNodeKey`/`_hoveredHandleKey`/`_hoveredSegment`).

Three commits landed in the last week and are the newest work: `62b897a` (foundation: model + tool +
draw + basic node edit), `985fafa` (P0.1 transactional-drag fix + topology ops: segment drag,
line↔curve, break-at-node, delete-segment + `NodeEditToolbar.xaml`), `cc0bead` (curve-preserving SVG
import). `7a4aa34`/`627e890` moved boolean ops and added Offset Path onto Clipper2
(`Lasero.Core/Geometry/Clipper2VectorBooleanService.cs`, `Clipper2VectorOffsetService.cs`,
`VectorOffsetPlanner.cs`).

**Headline architectural finding**: Offset (`SceneViewModel.BuildOffsetObject`, line 1158) produces a
real `VectorPath` via `VectorPathSceneFactory.Create` and is immediately node-editable. **Boolean
Union/Subtract/Intersect/Exclude do not** — `CreateVectorObject` (`SceneViewModel.cs:1420-1453`), the
helper both `UniteSelection` (line 974) and `CombineSelection` (line 1043, backing Subtract/Intersect/
Exclude at 1100/1103/1106) use to build their result `SceneObject`, only sets `LocalShapes`/
`LocalBounds`/`Transform`/`IsVisible`/`IncludeInOutput` — it never sets `VectorPath`. Every boolean
result is a flattened polyline object with `IsVectorPath == false`, permanently un-node-editable until
someone re-draws it. **Group and Ungroup have the identical bug**: `GroupSelection` (line 923) and
`UngroupSelection` (line 948) also build their result via `CreateVectorObject`, so grouping or
ungrouping any object — including one with a live `VectorPath` — silently discards the editable curve
model and replaces it with a flattened polyline object. This is the single largest violation of
`LIGHTBURN_VECTOR_PARITY.md` in the codebase (§65/§66/§67 all state VectorPath must survive copy/
duplicate/group/ungroup) and is not mentioned in the 2026-09-14 gap matrix at all.

---

## Feature classification tables

### SELECTION

| Feature | Current Lasero behavior (file:line) | Status | Notes |
|---|---|---|---|
| Enter Node Edit | Double-click a `VectorPath` object → `EnterNodeEditMode` (`SceneCanvas.VectorPathTool.cs:267`) | COMPLETE | Only reachable by double-click; no dedicated toolbar/menu entry point exists (see command-parity table) |
| Leave Node Edit | `ExitNodeEditMode` (`:281`), restores in-flight drag first if any | COMPLETE | |
| Click node | `OnNodeOrHandleMouseLeftButtonDown` (`:614`) | COMPLETE | |
| Click segment | `HandleNodeEditCanvasMouseDown` single-click path, `t ∈ (0.08, 0.92)` (`:324-336`) starts a segment drag, not a segment *selection* | PARTIAL | No standalone "segment selected" state exists — clicking a segment always begins reshaping it immediately; there is no click-to-select-then-Delete-key flow. Delete-on-hovered-segment (`DeleteHoveredSegment`, `:998`) substitutes for this but requires hover, not click-select |
| Click handle | `OnNodeOrHandleMouseLeftButtonDown` with `tag.IsHandle` (`:622-627`) | COMPLETE | |
| Shift multi-select nodes | `:632-635` (toggle add/remove) | COMPLETE | |
| Box select (marquee) | `HandleNodeEditCanvasMouseDown`→`DragMode.NodeMarquee`, `FinishNodeMarquee` (`:355-395`) | COMPLETE for replace/Shift-add/Ctrl-subtract | Spec §6 enclosing-vs-crossing direction deliberately not implemented (documented deviation — points have no meaningful crossing selection) |
| Cross-subpath selection | `_selectedNodeKeys` is `HashSet<(int Subpath, int Node)>` — any combination across subpaths | COMPLETE | |
| Deselect | Empty-canvas click with `w<=2 && h<=2` in `FinishNodeMarquee` exits node edit if it missed the object (`:364-368`); no explicit "clear node selection without exiting" click documented in spec §5 is only reachable by clicking on the object's own body without hitting a node | PARTIAL | Spec's "first empty click clears selection, second exits" two-stage behavior is not implemented; a miss always attempts exit immediately |
| Click priority on overlaps | Node/handle elements are separate WPF `Ellipse`s added to `_selectionVisuals` and consume the click before segment/body dispatch runs (`DrawNodeDot`/`DrawHandle`, `:509-599`) | COMPLETE | Matches spec §4 priority (node/handle above segment above body) |
| Hit tolerance | `NodeHitSizePx = 18`, `HandleDotHitSizePx = 16` (invisible hit targets) vs `NodeVisibleSizePx = 10`/`HandleVisibleSizePx = 8` visible dots (`:45-48`); segment tolerance `SegmentInsertTolerancePx = 8` (`:50`) | COMPLETE | Screen-space pixels, per spec §4 |

### NODE MOVEMENT

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Single/multi drag | `UpdateNodeEditDrag` else-branch (`:745-766`), iterates `_selectedNodeKeys` | COMPLETE | |
| Keyboard nudge | `NudgeSelectedNodes` (`:942-960`), wired from `OnCanvasKeyDown` only while Node Edit active with a node selection | COMPLETE | Each arrow press is its own undo step — spec's "may coalesce" optional behavior not implemented, acceptable per spec wording |
| Constrained move (Shift, 0/45/90/135°) | `ConstrainToNearestEighthTurn` (`:794-802`), applied in `UpdateNodeEditDrag` (`:750-751`) | COMPLETE | |
| Snap while dragging | None | MISSING | No node/endpoint/midpoint/intersection/grid snap exists anywhere in the node-edit path — confirmed by `grep` for `Snap` across `Lasero.App/Controls/*.cs`: zero hits |
| Escape cancel | `CancelNodeEditDrag` (`:885-898`), restores `_nodeEditWorkingPath` and `LocalShapes` to the pre-drag session snapshot | COMPLETE | |
| No-op drag (click without move) | `FinishNodeEditDrag` (`:823-848`) guards with `ReferenceEquals` (never-moved) and `VectorPathsStructurallyEqual` (drag-back-to-start) before calling `CommitNodeEdit` | COMPLETE | Both guards present |
| Transformed objects (rotate/scale) | Every drag maps `screen → world → obj.Transform.Inverse(...)` (`:717`, `:733`) before touching local anchors; rendering applies `obj.Transform.Apply(...)` (`:451`, `:558`) | COMPLETE at the pointer-mapping level | Not independently verified live for shear/non-uniform scale in this pass — `ObjectTransform` structure not audited here |

### BEZIER

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Handle visibility | `DrawHandle` only called `if (node.HandleIn is {})` / `if (node.HandleOut is {})` (`:455-458`) — null handle draws nothing | COMPLETE | |
| Drag in/out handle | `_draggedHandle` branch of `UpdateNodeEditDrag` → `VectorPathEditor.MoveHandle` (`:135-149`) | COMPLETE | |
| Smooth vs corner node | `VectorNodeType` enum (`VectorPath.cs:41-45`); visual: square (Corner) vs circle (Smooth) in `DrawNodeDot` (`:530-532`) | COMPLETE | |
| Symmetric handles | `MoveHandle` re-points (not re-lengths) the opposite handle on Smooth (`VectorPathEditor.cs:135-149`); Alt breaks symmetry (`:740`, passed as `breakSymmetry`) | COMPLETE | Matches spec §11 ("lengths do not necessarily need to remain equal") |
| Line↔curve conversion (explicit op) | `ConvertSegmentToCurve`/`ConvertSegmentToLine` (`VectorPathEditor.cs:157-188`) | COMPLETE at model level | **Not independently exposed in UI** — no toolbar/menu/shortcut calls these directly; only reachable implicitly via segment-drag (curve) or never (line, since no "convert to line" UI action exists) |
| Drag straight segment → curve | `HandleNodeEditCanvasMouseDown` → `BeginSegmentDrag` → `UpdateNodeSegmentDrag` → `VectorPathEditor.DragSegmentPoint` (auto-baselines via `ConvertSegmentToCurve` first, `VectorPathEditor.cs:210-212`) | COMPLETE | Exact minimal-movement Bézier math, not approximation |

### NODE OPERATIONS

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Insert node at pointer (double-click) | `HandleNodeEditCanvasMouseDown` clickCount≥2 branch (`:313-322`) → `HitTestNearestSegment` (approximate t) → `VectorPathEditor.InsertNode` (exact De Casteljau split) | COMPLETE | |
| Insert midpoint (t=0.5) | `VectorPathEditor.InsertNode` supports any t including 0.5 exactly | COMPLETE at model level | No dedicated "insert midpoint" UI command — only the nearest-point double-click gesture |
| Delete node(s) | `DeleteSelectedNodes` (`:918-934`), public, called from `NodeEditToolbar` and `Delete` key (`SceneCanvas.xaml.cs:2026`) | COMPLETE | Refuses to go below 2 open/3 closed nodes (`VectorPathEditor.RemoveNode`, `:70-73`) |
| Delete segment | `DeleteHoveredSegment` (`:998-1006`), Delete key with no node selection and pointer over a segment | COMPLETE | Context-sensitive per spec §21/§64 |
| Geometry preservation on insert | `VectorPathTests.InsertNodeOnCurvedSegmentPreservesExactCurveShape` pins exact curve shape via resampling | COMPLETE, test-verified | |
| Degenerate path handling | `RemoveNode` refuses below minimum node count (`:70-73`); `DeleteSegment` drops any resulting subpath with `< 2` nodes rather than keeping corrupt geometry (`VectorPathEditor.cs:311-314`) | COMPLETE | |

### PATH TOPOLOGY

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Open/Close toggle | `ToggleSelectedSubpathClosed` (`:985-992`), toolbar button, label swaps | COMPLETE | No tolerance dialog (spec §28) — plain toggle only |
| Break at node | `BreakAtNode` (`VectorPathEditor.cs:247-278`), exposed as `BreakSelectedNode` (`SceneCanvas.VectorPathTool.cs:1011-1022`), toolbar "Rozdělit v uzlu" | COMPLETE, test-covered (`VectorPathEditorTopologyTests.cs`, `VectorPathTopologyCommitTests.cs`) | Guards against breaking at an open path's own endpoint (no-op) |
| Split (segment deletion producing 2 subpaths) | `DeleteSegment` (`VectorPathEditor.cs:288-315`) | COMPLETE | |
| Join endpoints — same path | `TryCloseByEndpointJoin` (`:856-879`), called from `FinishNodeEditDrag` on mouse-up when a dragged endpoint lands within `CloseHoverThresholdPx` (12px) of the subpath's other endpoint | COMPLETE | |
| Join endpoints — different paths | Not implemented | MISSING | No cross-`SceneObject` merge exists; needs a new "merge two objects" policy (LayerId/GeometrySetId reconciliation) |
| Auto-join | Not implemented | MISSING | No tolerance-based batch join anywhere |
| Continue drawing from endpoint | Not implemented — pen tool never hit-tests existing scene objects | MISSING | |
| Reverse path | Not implemented | MISSING | No `Reverse`/winding-flip operation exists in `VectorPathEditor.cs` or anywhere else searched |
| Break Apart (whole object → separate objects) | Not implemented as a named vector operation | MISSING | `UngroupSelection` (line 948) does something structurally similar for compound geometry sets but is scoped to Group/Ungroup semantics, not "Break Apart a single compound path", and does not preserve `VectorPath` anyway |
| Segment trim | Not implemented | MISSING | |
| Extend path | Not implemented | MISSING | |
| Align segment angle | Not implemented | MISSING | Lower priority per spec |

### COMPOUND PATHS

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Outer + holes, multiple subpaths | `VectorPath.Subpaths: IReadOnlyList<VectorSubpath>` (`VectorPath.cs:138`); node-edit iterates all subpaths (`DrawNodeEditOverlay`, `:445-463`) | COMPLETE at data-model level | In practice the hand-drawn tool only ever produces one subpath per object; multi-subpath objects currently arise only from SVG import (`cc0bead`) and bitmap trace (`ReplaceRasterWithTrace`, `SceneViewModel.cs:290-327`) |
| Node identity across edits | Node identity is positional `(Subpath, Node)` index pairs, not stable IDs | ARCHITECTURALLY LIMITED | Mitigated by always recomputing selection from the committed result, but no durable per-node GUID |
| Node edit outer vs inner contour independence | `ReplaceSubpath`/`ReplaceSubpathWithMany` only ever touch the addressed subpath index (`VectorPath.cs:152-173`) | COMPLETE | |
| Fill semantics unified contract | See "ARCHITECTURAL FINDINGS" below | **NOT UNIFIED** | Still open per gap matrix's own Phase E finding — `Clipper2VectorBooleanService`/`ToolpathBuilder`/offset each handle winding/fill differently |

### SNAPPING

| Feature | Status | Notes |
|---|---|---|
| Node/endpoint/midpoint/intersection/grid snap | MISSING | Zero `Snap*` symbols found under `Lasero.App/Controls/*.cs` related to node edit |
| Bypass modifier | MISSING (nothing to bypass) | |
| Join feedback | PARTIAL | `TryCloseByEndpointJoin` performs the join on mouse-up but there is no live "about to join" preview indicator |
| Tolerance | Present only for the join gesture (12px) and segment insert (8px), not a general concept | PARTIAL |

### TRANSFORMS

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Translate/rotate/scale editing | Pointer mapping goes through `obj.Transform.Inverse`/`.Apply` uniformly (`:717/733/451/558`) | COMPLETE at mapping level | |
| Non-uniform scale / shear | Same code path, not independently tested for shear in this pass | UNVERIFIED | Flagged for follow-up, not confirmed broken |
| Inverse pointer mapping | `ObjectTransform.Inverse(currentWorld, obj.LocalPivot)` used consistently | COMPLETE | |

### UNDO/REDO

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Node drag commit | `CommitNodeEdit` → `SceneViewModel.CommitVectorPathEdit` → one `ReplaceObjectsCommand` (`SceneViewModel.cs:403-423`) | COMPLETE, test-covered (`VectorPathNodeEditTransactionTests.cs`) | |
| No-op = no history | `FinishNodeEditDrag` reference/structural-equality guards (`:836-841`) | COMPLETE | |
| Redo determinism | Not independently re-derived in this pass; relies on `ReplaceObjectsCommand`'s general contract | ASSUMED COMPLETE, not re-verified | |
| Topology ops (break/join/close/delete-segment) | Each routes through the same `CommitNodeEdit`/`ReplaceObjectsCommand` path | COMPLETE, test-covered | |
| Boolean ops | `Execute(new ReplaceObjectsCommand(...))` in `UniteSelection`/`CombineSelection` (`:1018`, `:1089`) | COMPLETE for undo mechanics | Undo correctly restores pre-boolean originals with any `VectorPath` intact; it is only the forward result that loses editability |
| Group/Ungroup | Same `ReplaceObjectsCommand` pattern (`:943`, `:967`) | COMPLETE for undo mechanics | Same caveat as boolean ops |

### METADATA

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| LayerId preserved (node edit) | `VectorPathSceneFactory.Rebuild` carries `previous.LayerId`/`PreferredMode` onto every rebuilt shape (`VectorPathSceneFactory.cs:49-53`) | COMPLETE | |
| GeometrySetId preserved (node edit) | `Rebuild` reuses `previous?.GeometrySetId` (`:44`) | COMPLETE | |
| Transform preserved (node edit) | `Rebuild` copies `existing.Transform` verbatim (`:60`) | COMPLETE | |
| Object identity preserved (node edit) | `Rebuild` copies `existing.Id` (`:56`) | COMPLETE | |
| Live-drag metadata (mid-gesture) | `VectorPathDragSession.BuildPreviewShapes` carries `reference.LayerColor`/`LayerId`/`GeometrySetId` from the pre-drag snapshot onto every preview frame (`:25-42`) — the P0.1 fix `985fafa` shipped | COMPLETE | |
| LayerId/metadata on boolean/group/ungroup results | `CreateVectorObject` (`SceneViewModel.cs:1420-1453`) does not set `LayerId` on the returned `SceneObject` directly — relies on `targetShape.LayerId` baked into the `ImportedShape`s passed in | COMPLETE for layer metadata, but VectorPath is lost regardless | |

### UI/UX

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Node/handle visuals | `DrawNodeDot`/`DrawHandle` (`:509-599`) | COMPLETE | Square=Corner, circle=Smooth |
| Selected state | `isSelected` → `SelectionBrush` fill vs `SelectionHandleFill` (`:529`) | COMPLETE | |
| Hovered state | Separate hover tracking for node/handle/segment, dot grows +2px and thickens stroke on hover (`:528, 535, 585, 592`) | COMPLETE | Deliberately distinct from Selected |
| Open-endpoint visual | Not specially marked — renders identically to any other node | MISSING | Spec §3 asks for open endpoints to be visually unmistakable |
| Smooth/corner distinction | Square vs circle (`:530-532`) | COMPLETE | |
| Segment hover | `DrawSegmentHoverHighlight` (`:469-499`) | COMPLETE | |
| Cursor changes | `Cursors.Hand` on node/handle hit targets only (`:517`, `:575`) — no distinct cursor for segment/join/snap/curve-drag/box-select states | PARTIAL | Spec §62 wants distinct cursors per interaction type |
| Contextual toolbar | `NodeEditToolbar.xaml`/`.xaml.cs`, bound to `SceneCanvas.IsNodeEditActive`/`SelectedNodeCount`/`SelectedNodeType`/`IsSelectedSubpathClosed` (`SceneCanvas.VectorPathTool.cs:1044-1088`); swapped with `SelectionPropertiesBar` in `MainWindow.xaml` | COMPLETE | Corner/Smooth toggle, Open/Close, Break at Node, Delete — 5 actions |
| Context menu | `d5d5612` added a general right-click context menu (boolean ops, offset, trace, background removal) | PARTIAL | Not verified whether it has node-edit-specific entries (line↔curve, insert midpoint, reverse, break apart) — not independently re-read this pass |
| Keyboard shortcuts | Delete (node/segment), Escape (cancel/exit), Enter (finish path), Shift (constrain/add-select), Ctrl (marquee subtract), Alt (break handle symmetry), arrows (nudge) | COMPLETE for what exists | No shortcut for Corner/Smooth toggle, Open/Close, Break at Node, line↔curve, reverse, join, break-apart — toolbar/right-click only |
| Snap indicators | MISSING | No snap system exists |

### PERFORMANCE

| Feature | Current (file:line) | Status | Notes |
|---|---|---|---|
| Rebuild-on-every-drag anti-pattern | `RenderVectorPathLive` (`:810-815`) re-flattens the whole `VectorPath` (adaptive Bézier subdivision, recursion depth up to 20) and calls `UpdateObjectGeometry(obj)` on every pointer-move frame | POTENTIAL RISK, not measured | No frame-time instrumentation or throttling found. `RedrawSelectionOverlay()` also rebuilds the entire overlay visual tree from scratch on every hover-state change, not incrementally |
| O(n) hit testing | `HitTestNearestSegment` (`:403-433`) samples every segment at 40 fixed points per call, invoked from double-click insert, segment-drag start, idle-hover polling, and delete-on-hover, un-throttled | POTENTIAL RISK, not measured | O(segments × 40) per call, polled on every idle pointer-move sample while node-edit is active |

---

## LightBurn/Inkscape node-command parity table

| Command | Lasero equivalent | Where exposed |
|---|---|---|
| Select nodes | Click / Shift-click / marquee | Canvas only (no menu) |
| Insert node | Double-click on segment | Canvas gesture only, no menu/shortcut |
| Insert midpoint | `VectorPathEditor.InsertNode(t=0.5)` exists at model level | **Not exposed in UI at all** |
| Delete node | `DeleteSelectedNodes` | `Delete` key, `NodeEditToolbar` button |
| Delete segment | `DeleteHoveredSegment` | `Delete` key while hovering a segment with no node selected |
| Line → curve | `ConvertSegmentToCurve` exists at model level | **No direct UI command** — only reachable implicitly by dragging a straight segment |
| Curve → line | `ConvertSegmentToLine` exists at model level | **Not exposed in UI at all** |
| Smooth node | `ConvertNodeType(Smooth)` | Right-click toggle, `NodeEditToolbar` button |
| Corner node | `ConvertNodeType(Corner)` | Right-click toggle, `NodeEditToolbar` button |
| Break path (at node) | `BreakAtNode`/`BreakSelectedNode` | `NodeEditToolbar` button only, no shortcut |
| Join nodes (same path) | `TryCloseByEndpointJoin` | Implicit drag-to-endpoint gesture only |
| Join nodes (different paths) | **MISSING** | — |
| Close path | `ToggleSelectedSubpathClosed`/`VectorPathEditor.Close` | `NodeEditToolbar` toggle, no shortcut |
| Extend endpoint | **MISSING** | — |
| Trim segment | **MISSING** | — |
| Align segment | **MISSING** | — |
| Reverse path | **MISSING** | — |
| Break apart | **MISSING** as a vector-topology op | `UngroupSelection` covers a related but distinct concept and does not preserve `VectorPath` |

---

## Architectural findings (LocalShapes/VectorPath violations)

1. **Group destroys VectorPath.** `SceneViewModel.GroupSelection()` (`SceneViewModel.cs:923-946`) builds
   its result through `CreateVectorObject` (`:1420-1453`), which never sets `SceneObject.VectorPath`.
   Grouping objects — including ones with a live, node-edited `VectorPath` — always produces a
   flattened, non-node-editable object. Violates `LIGHTBURN_VECTOR_PARITY.md` §67.

2. **Ungroup destroys VectorPath.** `SceneViewModel.UngroupSelection()` (`:948-971`) has the identical
   defect — each split-out part is built via `CreateVectorObject`.

3. **Boolean ops (Union/Subtract/Intersect/Exclude) destroy VectorPath.** `UniteSelection` (`:973-1026`)
   and `CombineSelection` (`:1043-1097`) both call `CreateVectorObject` for their result — permanently
   flattened, `IsVectorPath == false`. Inconsistent with **Offset**, which correctly produces a
   node-editable result via `BuildOffsetObject` → `VectorPathSceneFactory.Create`
   (`SceneViewModel.cs:1158-1168`) — the fix pattern already exists in-repo, just not applied here.

4. **Copy/Duplicate correctly preserve VectorPath.** `SceneObject.Clone()` (`SceneObject.cs:228`) and
   the serializer round-trip (`SceneViewModel.cs:1491`, `:1576`, `Lasero.Persistence/ProjectFile.cs:63`)
   all carry `VectorPath` through unchanged. No violation found here.

5. **Import/parametric objects have no VectorPath except SVG (as of `cc0bead`) and bitmap trace.** DXF
   import does not exist in this codebase at all. Parametric primitives (rectangle/ellipse) still have
   no path form — known, already-documented gap, not new.

6. **No unified compound-path fill contract** (§41 of the spec) remains open — unchanged since the
   2026-09-14 gap matrix's own finding.

---

## What's missing for P0 trustworthy node editing (observed gaps, no priority judgment beyond flagging)

- **Group/Ungroup/Boolean results silently lose node-editability** — the single most consequential
  finding in this audit; a user who groups, ungroups, or performs any boolean operation on a carefully
  node-edited path gets back an object they can no longer node-edit, with no warning.
- **No snapping of any kind** in node edit (node/endpoint/midpoint/intersection/grid).
- **No node identity stability** across structural edits (positional-index selection keys only).
- **Cross-object path continuation/joining is entirely missing.**
- **Reverse path, Break Apart (as a real vector-topology operation), Trim, Extend, Align-segment are all
  unimplemented.**
- **Curve↔line is not user-reachable as an explicit, discoverable command** — model-level and
  test-covered, but curve→line has no caller anywhere in the UI layer.
- **No performance measurement exists** for the per-frame full-reflatten-on-drag and the un-throttled
  O(segments×40) hover hit-test polling.
- **Open-endpoint visual distinction is missing.**

---

## Addendum (2026-09-17) — status update + curve-preserving Group proposal

Since this audit was written, a follow-up session:
- Fixed the Group/Ungroup/boolean-ops VectorPath loss described above (`SceneViewModel.CreateVectorObject`
  now always attaches a straight-segment `VectorPath` consistent with its `LocalShapes` — see
  `BuildStraightVectorPath`). Curve fidelity from the original sources is still NOT preserved through
  Group (see proposal below) — only through Node Edit, Offset, and SVG import.
- Exposed Convert-to-Line, Convert-to-Curve, and Insert-Midpoint in the UI (toolbar buttons +
  Shift+L/Shift+C/Shift+M shortcuts), all targeting the currently-hovered segment — the model-layer
  operations already existed and were already tested; this pass was UI wiring only.
- Added a reusable, WPF-free snapping engine (`Lasero.Core/Scene/Snapping/SnapEngine` +
  `SnapCandidateBuilder`) and wired it into node/endpoint drag (full geometry + grid) and handle/segment
  drag (grid only), with a live visual indicator and a Ctrl bypass.
- Added an open-endpoint visual ring and distinguished cursors for segment-hover and join-capable
  endpoint drag.

None of the "what's missing" bullets above were closed by this pass except the two called out
explicitly (curve↔line/insert-midpoint UI exposure, and snapping); cross-object joining, reverse path,
break apart, trim, extend, align-segment, and node identity stability remain open.

### Curve-preserving Group — proposed design (NOT implemented this pass)

**Problem**: `SceneViewModel.GroupSelection` (`SceneViewModel.cs:923`) collects every source's geometry
via `item.GetWorldShapes()` — an `ImportedShape`-only (flattened-polyline) API — and feeds the result
into `CreateVectorObject`, which now wraps it in a straight-segment `VectorPath`
(`BuildStraightVectorPath`). A source that had real Bézier curves loses them the moment it is grouped,
even though the *editability* bug (VectorPath being entirely absent) is now fixed.

**Proposed fix**, in the same shape as the existing Offset/BuildOffsetObject precedent:

1. Add a `SceneObject.GetWorldVectorPath()` (or a free function taking a `SceneObject`) that, when
   `VectorPath is not null`, returns a NEW `VectorPath` with every node's `Anchor`/`HandleIn`/
   `HandleOut` transformed via the same `Transform.Apply(position, LocalPivot)` already used for
   anchors elsewhere in this file (`DrawHandle`, `DrawNodeEditOverlay`) — handles are stored as
   absolute local positions, not deltas, so this needs no new math, just applying the existing
   transform to three positions per node instead of one.
2. In `GroupSelection`, alongside the existing `worldShapes` collection loop, also collect each
   source's world-space `VectorPath.Subpaths` in the SAME per-source order `worldShapes` already
   iterates in (the model already guarantees `VectorPath.Subpaths.Count == LocalShapes.Count` in the
   same order — `VectorPathSceneFactory`/`Rebuild` maintain that invariant, so this is not a new
   assumption). A source with no `VectorPath` falls back to a straight-segment subpath built from its
   own `ImportedShape.Points` (the existing `BuildStraightVectorPath` logic, applied per-source instead
   of over the whole merged list) — mixed vector/non-vector selections degrade gracefully rather than
   failing.
3. Concatenate every source's subpaths into one combined `VectorPath`, then recenter it into the new
   group's local space by subtracting `(centerX, centerY)` from every anchor AND handle (a pure
   translation — the group's own `Transform` is `Identity with { X = centerX, Y = centerY }`, so this
   is exact, not an approximation).
4. Set `LocalShapes = combined.FlattenAll()` zipped with per-shape metadata exactly as
   `CreateVectorObject` already does today, and `VectorPath = combined`.
5. `GeometrySetId` tagging (the existing "give each source's shapes a shared legacy id" loop just above
   the `CreateVectorObject` call) is untouched by this — it operates on `ImportedShape` records, a
   different, independent piece of metadata from `VectorPath`'s subpath list.

**Why not implemented in this pass**: it is well-scoped but not "extremely local" — it needs a new
cross-cutting helper (`GetWorldVectorPath`, likely on `SceneObject` or `ObjectTransform`), a rewritten
`GroupSelection` collection loop, and its own dedicated test suite (curve fidelity across a group,
mixed VectorPath/non-VectorPath sources, rotated/scaled sources, undo/redo, and an Ungroup round-trip
check — Ungroup would need the mirror-image treatment to split the combined `VectorPath` back into
per-part subpaths, which `UngroupSelection` does not currently attempt at all even for the straight-line
case). That is a full follow-up phase, not a same-pass addition.
