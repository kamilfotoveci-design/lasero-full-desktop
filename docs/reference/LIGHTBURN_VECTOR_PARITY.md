# LASERO — LightBurn Vector Editing Behavioral Specification

## Purpose

This document defines the behavioral target for Lasero vector editing.

LightBurn is used as a benchmark for interaction semantics, predictability, vector editing capability, and workflow quality.

The objective is not to visually clone LightBurn. The objective is for Lasero vector editing to feel predictable, immediate, non-destructive, precise, easy to understand, safe to undo, and suitable for real laser-production workflows.

Where Lasero already provides a cleaner or better interaction, keep the superior Lasero behavior unless doing so creates inconsistency with the vector model.

---

# 1. Core architectural principle

## 1.1 Editable paths are authoritative

A generic editable vector path must be represented by a dedicated vector model.

```text
VectorPath
 ├─ Subpath[]
 │   ├─ Closed
 │   └─ Node[]
 │       ├─ Position
 │       ├─ IncomingHandle
 │       ├─ OutgoingHandle
 │       └─ node / handle semantics
 ├─ transform
 ├─ layer/output metadata
 └─ geometry semantics
```

Rendered shapes must be derived from this model.

```text
VectorPath
    ↓
geometry builder
    ↓
render geometry / LocalShapes
```

Never make this a normal editing workflow:

```text
LocalShapes
    ↓
reverse-engineer
    ↓
VectorPath
```

`LocalShapes` may remain temporarily necessary for legacy subsystems during migration, but must not become the authoritative representation for an editable path.

---

# 2. Object types

Lasero should conceptually distinguish between:

```text
Parametric object
Editable vector path
Group
Raster/image
Text
```

A parametric rectangle should retain width, height, corner radius, and transform until explicitly converted to a generic path.

Converting a parametric object to a path must intentionally discard the higher-level editing parameters while preserving visual geometry, position, layer, transforms, output semantics, and undoability.

---

# 3. Entering Node Edit mode

A selected editable path can enter Node Edit mode.

When Node Edit is activated:

```text
selected VectorPath
        ↓
display editable nodes
        ↓
display applicable handles
```

If no object is selected, clicking an editable path while the tool is active makes it the active node-edit object.

Grouped objects cannot be directly node-edited until ungrouped.

Parametric objects must be converted to path first.

The active editable object must be visually unmistakable. Show path, nodes, active nodes, Bézier handles when relevant, hovered segment/node/handle, and open endpoints.

---

# 4. Pointer hit-testing priority

Recommended priority while Node Edit is active:

```text
1. selected node
2. node
3. selected Bézier handle
4. Bézier handle
5. open endpoint
6. segment
7. active path body
8. other vector object
9. canvas
```

A node must never be difficult to select because the path underneath it steals the click.

Hit tolerances should be based primarily on screen-space pixels, not document units.

---

# 5. Node selection

Clicking a node selects it, clears previous node selection unless a modifier is held, does not modify geometry, and does not create an undo entry.

Use Shift + click to add/remove a node or handle from the current node selection.

Moving one selected item moves the selected set.

Recommended empty-canvas behavior:
- first empty click clears node selection
- second click or Escape leaves node editing

---

# 6. Box-selection of nodes

Support drag-selection while in Node Edit.

Left → right: enclosing selection.

Right → left: crossing selection.

Modifiers:

```text
none        replace node selection
Shift       add to node selection
Ctrl        subtract/toggle if desired by Lasero convention
```

---

# 7. Node dragging

On pointer down, capture immutable committed VectorPath state.

On pointer move:

```text
preview = original state + current drag delta
```

Do not mutate the committed state frame-by-frame.

On pointer up, commit exactly one edit:

```text
BeforeState
AfterState
```

Push one undo entry.

Escape during drag restores BeforeState, with zero geometry mutation and zero undo entry.

---

# 8. Constrained node movement

Use Shift + drag to constrain movement to 0°, 45°, 90°, 135°, etc.

The constraint should use the drag starting position as origin.

Display a lightweight temporary guide.

---

# 9. Keyboard nudging

Recommended:

```text
Arrow                 normal increment
Shift + Arrow         larger increment
Ctrl + Arrow          fine increment
```

One held-key sequence may be coalesced into a single undo transaction if practical.

---

# 10. Bézier representation

A curved segment consists of:

```text
anchor A
A.outHandle
B.inHandle
anchor B
```

Handles must be stored as part of editable vector geometry, not inferred repeatedly from flattened curves.

The model must support at minimum:
- Corner
- Smooth

Optionally:
- Symmetric

---

# 11. Smooth node behavior

For a smooth node, incoming and outgoing tangents remain collinear when either handle angle is changed.

Handle lengths do not necessarily need to remain equal.

---

# 12. Corner node behavior

Incoming and outgoing handles are independent.

Changing one must not alter the other angle.

---

# 13. Convert smooth ↔ corner

The underlying operations must exist even if Lasero uses different shortcuts.

Example:
- corner → smooth
- smooth → corner

---

# 14. Dragging Bézier handles

Dragging a handle previews continuously, affects only dependent geometry, obeys smooth/corner constraints, supports angle constraint, commits on mouse-up, cancels completely on Escape, and creates one undo transaction.

Moving a handle must never cause unrelated subpaths to change, layer metadata loss, transform loss, node reordering, or path closure state change.

---

# 15. Dragging curve segments

Existing curve:

```text
drag curve body
→ modify curve
```

Straight line:

```text
drag straight segment away from line
→ convert line to Bézier
→ shape follows pointer
```

Undo must restore the original straight segment.

---

# 16. Convert line to curve

```text
LineSegment
→
CubicBezierSegment
```

Initial geometry should remain visually equivalent before the user modifies handles.

---

# 17. Convert curve to line

```text
BezierSegment
→
LineSegment
```

Preserve endpoints exactly.

---

# 18. Insert node anywhere on segment

For line: split line at closest projected point.

For Bézier: split cubic Bézier at parameter t using mathematically exact curve splitting, e.g. De Casteljau subdivision.

Adding a node to a curve must not visually alter the curve.

---

# 19. Insert midpoint node

For Bézier, use t = 0.5, not simply midpoint of endpoint positions.

---

# 20. Delete node

For internal node:

```text
A → B → C
```

becomes approximately:

```text
A → C
```

Preserve path continuity as sensibly as possible.

For endpoint, remove the endpoint and adjacent segment.

Handle degenerate tiny paths safely.

Never leave corrupt geometry.

---

# 21. Delete segment

Context-aware behavior:

```text
hover node    + Delete → delete node
hover segment + Delete → delete segment
```

Deleting a line/curve opens or splits the path.

---

# 22. Break path at node

A closed loop becomes one open path with two endpoints at the break location.

For an internal node of an open path, behavior must be explicitly defined and tested.

---

# 23. Join endpoints by dragging

When an open endpoint enters the join snap region of another compatible endpoint, show join snap feedback.

On mouse-up:
- same open path → close path
- different open paths → combine into one path

Escape before mouse-up must not join.

---

# 24. Continue existing path while drawing

Support:

```text
DrawPath
→ hover existing endpoint
→ endpoint highlights
→ click
→ continue same path
```

Likewise, a new segment connecting two existing open paths should unify them.

---

# 25. Drawing straight paths

Target workflow:

```text
click
move
click
move
click
...
```

Each click creates a new anchor.

Terminate with Escape or right-click.

Close by clicking the original start node.

---

# 26. Drawing curves

Preferred:

```text
mouse-down at node
drag
→ generate tangent handle
release
→ anchor created
```

Lasero may initially implement controlled Bézier creation first.

---

# 27. Close Path

Selected open path → Close Path creates a closing segment.

Do not silently move endpoints without communicating that behavior.

---

# 28. Close paths with tolerance

Target:

```text
Tolerance: X mm

Mode:
[ Move endpoints together ]
[ Connect with segment ]
```

Useful for imported DXF/SVG repair.

---

# 29. Auto-Join

Support tolerance-based joining.

Auto-join must operate on endpoints only.

It must not merge arbitrary nearby internal nodes.

---

# 30. Break Apart

Do not confuse:
- Break path at node
- Break Apart entire object

---

# 31. Segment trim

Desired behavior:

```text
hover
→ highlight affected segment
→ click / shortcut
→ trim
```

Preview before destructive operation.

---

# 32. Extend path

Only open endpoints are valid.

Preview the extension before commit when possible.

---

# 33. Align segment angle

Support aligning a segment to the nearest:
- 0°
- 45°
- 90°

Lower priority than core editing.

---

# 34. Snapping

Support at minimum:
- Node
- Endpoint
- Midpoint
- Intersection
- Grid

Later:
- Object center
- Line projection
- Guidelines

Snapping should display visible feedback for the active snap type.

---

# 35. Snap modifiers

Lasero must have a quick modifier to bypass snapping during precision work.

---

# 36. Alignment guides

Keep as later parity.

---

# 37. Primitive transforms

Selected objects should expose move, resize, rotate, and shear.

Critical rule: transforming an editable path must not destroy editability.

Recommended:

```text
VectorPath local geometry
+
SceneObject transform
```

Bake only when an operation explicitly requires it.

---

# 38. Node editing transformed paths

Node editing must work correctly with translation, rotation, scale, and possibly shear.

Pointer input:

```text
screen
→ world
→ inverse object transform
→ local vector coordinates
```

Rendering:

```text
local vector
→ object transform
→ world
→ screen
```

No transform should need to be destructively applied merely to enter Node Edit.

---

# 39. Selection behavior

At object level support:
- click
- Shift + click
- box-select

Grouped objects behave as single objects in normal selection.

---

# 40. Compound vector objects

A single editable vector object must be capable of containing multiple subpaths.

Example:

```text
O
```

contains outer contour and inner contour/hole.

Do not automatically turn each contour into an independent scene object unless explicitly requested.

---

# 41. Holes and fill semantics

All subsystems must agree on what constitutes a filled region.

The same vector should produce the same topology in:
- canvas rendering
- boolean input
- offset input
- toolpath generation
- SVG export
- hit testing
- preview

There must be one documented compound-path fill contract.

---

# 42. Winding

Do not make user-facing correctness depend on arbitrary imported winding unless required by the geometry library.

Normalize topology/containment before geometry operations if necessary.

Tests must include:
- CW outer + CCW hole
- CCW outer + CW hole
- CW outer + CW hole
- CCW outer + CCW hole

Equivalent semantic results should be confirmed where appropriate.

---

# 43. Boolean operation eligibility

Define operands explicitly:

```text
Operand A
Operand B
```

Each operand may contain multiple subpaths.

---

# 44. Boolean Union

Expected result:

```text
A ∪ B
```

Preserve disconnected islands, holes, and nested contours.

Undo restores both original operands exactly.

---

# 45. Boolean Subtract

Expected:

```text
A - B
```

Selection order matters.

Lasero should make operand order visible.

Potential UX:
- Subtract B from A
- Subtract A from B

---

# 46. Boolean Intersection

Expected:

```text
A ∩ B
```

Preserve all resulting islands and holes.

---

# 47. Boolean Exclude / XOR

Define:

```text
A XOR B
```

as regions contained in exactly one operand.

Keep this deterministic with compound paths.

---

# 48. Boolean preview

Recommended options:
- Union
- Subtract A-B
- Subtract B-A
- Intersect
- Exclude

No scene mutation until confirmation.

---

# 49. Boolean transaction behavior

Opening preview: no undo entry.

Switching preview type: no undo entry.

Cancel: original objects unchanged.

Confirm: one undo entry.

Undo: exact original objects restored, including identity, layer, transform, metadata, and vector geometry.

---

# 50. Offset

Support:
- Outward
- Inward
- Both

---

# 51. Offset corner styles

Support:
- Round
- Bevel
- Miter/Corner

Use a sensible miter limit.

---

# 52. Offset options

Strongly copy the transaction model even if all options are not implemented initially.

---

# 53. Offset preview transaction

While offset settings change, source geometry remains untouched.

Generate temporary preview geometry.

Cancel discards preview.

OK commits once.

---

# 54. Offset holes

For a filled ring shape:

Outward offset:
- exterior boundary moves outward
- hole boundary moves inward

Inward offset:
- exterior boundary moves inward
- hole expands outward

This must be based on semantic topology, not blindly applying the same signed distance to every contour.

---

# 55. Grouping vs compound paths

Do not conflate Group and Compound VectorPath.

A group is an organizational structure containing scene objects.

A compound path is one vector geometry containing multiple contours/subpaths.

---

# 56. Undo / redo contract

Every vector operation must be transactional.

One user gesture = one history action.

```text
mouse down
100 pointer moves
mouse up
```

must equal one undo action, not 100.

---

# 57. Exact undo

Undo must restore semantic object state, not merely something visually similar.

Restore:
- object identity where contract requires
- VectorPath
- subpaths
- nodes
- handles
- closed state
- transform
- layer
- GeometrySetId
- metadata
- selection-relevant identity where practical

---

# 58. Redo

```text
initial
→ edit
→ undo
→ redo
```

must return the same semantic state as the first committed edit.

---

# 59. No-op transactions

These must not create history:
- click node without moving
- open preview and Cancel
- drag and return exactly to start
- enter and leave Node Edit
- select/deselect node
- hover operation

unless another explicit persisted state change occurs.

---

# 60. Rendering during drag

Live editing must feel immediate.

Avoid scene-wide rebuilds, serialization, boolean resolution of the entire document, or toolpath regeneration during every pointer move.

Only regenerate geometry necessary to show the edited path.

---

# 61. Performance targets

Simple path: < 8 ms typical preview update.

Medium path: < 16 ms.

Complex imported path: maintain interactive movement wherever possible.

Defer expensive derived work until commit.

---

# 62. Cursor feedback

Different feedback for:
- node
- handle
- segment
- join endpoint
- snap point
- curve drag
- box select

---

# 63. Hover feedback

Before destructive/contextual operations, highlight the target.

Examples:
- hover node + Delete → node highlight
- hover segment + Delete → segment highlight
- trim → removed part highlighted
- endpoint join → join candidate highlighted

---

# 64. Context-sensitive editing

The active hover target must be determined before executing commands.

Example:

```text
Delete
```

becomes:
- node hovered → delete node
- segment hovered → delete segment
- selected nodes → delete selected nodes

---

# 65. Geometry preservation through copy/paste

Copying an editable vector must preserve:
- VectorPath
- handles
- subpaths
- closed states
- layer semantics
- transform

Paste must create new scene identity without degrading it to anonymous LocalShapes.

---

# 66. Duplicate

Duplicate should produce:
- new object identity
- deep copied vector model
- same geometry
- same layer
- same editable capability

No shared mutable node collections.

---

# 67. Group / ungroup preservation

Grouping a vector must not destroy VectorPath.

Ungrouping must recover the exact editable vector child.

---

# 68. Import

SVG/DXF import should generate editable vector geometry whenever representable by Lasero vector model.

Do not immediately flatten curves into dense polylines unless absolutely necessary.

Preferred:

```text
SVG cubic Bézier
→ cubic Bézier nodes/handles
```

not:

```text
SVG cubic
→ hundreds of line segments
```

---

# 69. Precision

Use double precision throughout core vector geometry unless there is a compelling technical reason not to.

Avoid repeated transforms that permanently rewrite coordinates and accumulate drift.

---

# 70. Fill vs Line operation

A path's editable geometry should not depend on whether the laser layer is Line, Fill, or Offset Fill.

These are output semantics.

---

# 71. Open path safety

Open paths must remain legal.

Do not assume every vector is fillable.

Lasero should reliably detect open and closed subpaths.

---

# 72. Status feedback

For precise vector work, optionally show:
- x
- y
- dx
- dy
- segment length
- angle

during drawing/editing.

Not P0, but valuable professional UX.

---

# 73. Required interaction state machine

Do not let behavior emerge from random booleans spread throughout `SceneCanvas.VectorPathTool.cs`.

Suggested states:

```text
Idle

ObjectSelected

NodeEdit
 ├─ NoNodeSelection
 ├─ NodeSelection
 └─ MultiNodeSelection

DraggingNode
DraggingHandle
DraggingSegment
BoxSelectingNodes

DrawingPath
DrawingBezier

BooleanPreview
OffsetPreview
```

Transitions must be explicit.

---

# 74. Pointer transaction state

A drag state should hold:

```text
OriginalVectorState
TargetElement
StartPointerWorld
StartPointerLocal
SelectedElements
ModifierState
SnapState
```

Preview state is calculated from that original snapshot.

Do not make pointer movement cumulative.

Bad:

```text
frame 1 modifies geometry
frame 2 modifies frame 1
frame 3 modifies frame 2
```

Preferred:

```text
every frame =
OriginalGeometry + current total drag delta
```

---

# 75. LightBurn parity acceptance suite

## Basic editing

### LB-VEC-001
Create open 3-node line path.

Expected: 3 nodes, 2 segments, open.

### LB-VEC-002
Drag middle node.

Expected: only middle node changes.

### LB-VEC-003
Cancel drag.

Expected: exact original vector restored, no undo entry.

### LB-VEC-004
Commit drag → Undo → Redo.

Expected: original → edited → original → edited.

## Bézier

### LB-VEC-010
Convert line to curve. Geometry remains initially equivalent.

### LB-VEC-011
Drag curve segment. Line converts to editable curve.

### LB-VEC-012
Smooth node. Handles become collinear.

### LB-VEC-013
Convert smooth node to corner. Handles become independently angular.

### LB-VEC-014
Curve → line. Handles removed; endpoints unchanged.

## Node operations

### LB-VEC-020
Insert node on line. Visual geometry unchanged.

### LB-VEC-021
Insert node on Bézier at pointer. Visual geometry unchanged.

### LB-VEC-022
Insert midpoint on Bézier. Use t=.5.

### LB-VEC-023
Delete internal node. Path remains valid.

### LB-VEC-024
Delete segment. Path opens/splits correctly.

## Selection

### LB-VEC-030
Single-select node.

### LB-VEC-031
Shift-select second node. Both selected.

### LB-VEC-032
Drag selected node set. All selected nodes move same delta.

### LB-VEC-033
Constrain selected nodes. Last-selected anchor determines constrained delta.

### LB-VEC-034
Selection does not mutate geometry.

## Path topology

### LB-VEC-040
Break closed path at node. Result becomes open.

### LB-VEC-041
Join two endpoints. Result becomes one path.

### LB-VEC-042
Join start/end of same path. Result becomes closed.

### LB-VEC-043
Continue drawing from open endpoint. Same path extended.

## Compound paths

### LB-VEC-050
Outer rectangle + inner rectangle hole. One vector object, two subpaths.

### LB-VEC-051
Transform compound path. Hole relationship survives.

### LB-VEC-052
Node edit outer contour. Inner contour unchanged.

### LB-VEC-053
Node edit inner contour. Outer contour unchanged.

## Booleans

### LB-VEC-060
Union two rectangles.

### LB-VEC-061
Subtract B from A.

### LB-VEC-062
Subtract A from B.

### LB-VEC-063
Intersect.

### LB-VEC-064
XOR.

### LB-VEC-065
Boolean containing holes.

### LB-VEC-066
Boolean nested contours.

### LB-VEC-067
Boolean reversed winding. Equivalent topology.

### LB-VEC-068
Boolean Undo. Exact operands restored.

## Offset

### LB-VEC-070
Positive/outward rectangle offset.

### LB-VEC-071
Negative/inward rectangle offset.

### LB-VEC-072
Outward compound-path offset. Hole contracts.

### LB-VEC-073
Inward compound-path offset. Hole expands.

### LB-VEC-074
Round join.

### LB-VEC-075
Bevel join.

### LB-VEC-076
Miter/corner join.

### LB-VEC-077
Offset preview Cancel. No mutation.

### LB-VEC-078
Offset Confirm → Undo. Original restored.

## Transforms

### LB-VEC-080
Translated object enters node mode. Correct node positions.

### LB-VEC-081
Rotated object node drag. Pointer→local transform correct.

### LB-VEC-082
Scaled object node drag. Correct local movement.

### LB-VEC-083
Undo transformed edit. Exact transform + vector restored.

## Metadata

### LB-VEC-090
Node edit preserves LayerId.

### LB-VEC-091
Node edit preserves GeometrySetId.

### LB-VEC-092
Boolean result uses explicit metadata policy.

### LB-VEC-093
Offset result uses explicit metadata policy.

---

# 76. Implementation order for Lasero

## Phase A — authoritative vector foundation

First determine whether existing:
- VectorPath
- VectorSubpath
- VectorNode

can support this specification.

If yes, extend them; do not replace them.

Establish:

```text
VectorPath authoritative
LocalShapes derived
```

for editable vectors.

## Phase B — transaction-safe interaction

Implement:
- node selection
- node drag
- multi-node drag
- handle drag
- Escape cancel
- Undo/Redo
- transform-safe editing

Nothing else until these are solid.

## Phase C — proper Bézier semantics

Implement:
- Corner
- Smooth
- line ↔ curve
- segment drag
- insert node
- delete node

## Phase D — path topology

Implement:
- open / closed
- break
- join
- continue path
- delete segment
- auto-join

## Phase E — compound geometry

Implement:
- multiple subpaths
- holes
- consistent fill contract
- winding normalization if needed

## Phase F — booleans

Only once compound semantics are proven.

Implement:
- Union
- Subtract
- Intersect
- Exclude
- preview
- undo

## Phase G — offset

Implement:
- inward
- outward
- both
- holes
- join styles
- preview

## Phase H — snapping and professional polish

Then:
- node snap
- endpoint snap
- midpoint snap
- intersection snap
- grid snap
- hover feedback
- cursors
- box-select
- keyboard shortcuts
- status measurements

---

# 77. Non-negotiable Lasero rules

Treat these as engineering invariants:

```text
1. Editable vectors never depend on reconstructing state from rendered LocalShapes.
2. Pointer preview never corrupts committed geometry.
3. One gesture = one undo transaction.
4. Escape fully cancels in-progress edits.
5. Transforms never silently destroy editable vectors.
6. Grouping never destroys editable vectors.
7. Copy/duplicate never shares mutable node storage.
8. Holes have identical meaning in rendering, booleans, offset and toolpath generation.
9. Geometry operations never silently lose layer metadata.
10. No feature is considered complete without Undo/Redo tests.
```

---

# 78. What NOT to copy from LightBurn blindly

We are benchmarking behavior quality, not reproducing every legacy LightBurn design choice.

Do not blindly copy:
- every shortcut
- toolbar layout
- icons
- dialog design
- legacy selection quirks
- implementation architecture
- visual styling

Lasero can improve unclear interactions if semantics remain predictable and professional.

---

# 79. Definition of "LightBurn-level vector editing"

Lasero reaches the required baseline when a user can draw, select, node-edit, curve-edit, join, break, close, transform, boolean, offset, undo, and redo without worrying that:
- the vector will corrupt
- holes will disappear
- metadata will be lost
- undo will not restore it
- a transform will make it uneditable
- a click will affect the wrong thing

The main goal is not the number of features. The main goal is trust.

A professional editor should make the user confident that:

> When I grab this node, exactly this node will move, nothing else will mysteriously break, and Ctrl+Z will put everything back.

That's the standard Lasero should hit.

---

# Claude handoff instruction

Use this file as the behavioral source of truth for Lasero vector editing.

Before implementing, compare the current architecture against this specification and produce a gap matrix:

```text
Requirement | Current implementation | Gap | Architectural blocker | Proposed smallest change | Risk
```

Especially determine whether the existing `VectorPath`, `VectorSubpath`, and `VectorNode` model can satisfy the specification without introducing another competing editable-vector representation.

Prefer extending the existing model.

The desired end-state is:

```text
editable VectorPath geometry = authoritative
LocalShapes = derived
```

Do not rewrite everything at once.

From the gap matrix, propose the smallest implementation sequence that makes basic node editing trustworthy first.

No P1 feature work until the vector foundation is proven by regression tests.
