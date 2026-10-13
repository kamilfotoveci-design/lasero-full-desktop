# Lasero vector workflow parity audit — 2026-10-12

## Scope

Audited object grouping, compound paths, Boolean operations, split operations, processing layers,
selection order, destructive edits, geometry fidelity, and undo/redo. Compared current behavior with
official LightBurn, Inkscape, and Illustrator guidance. The products do not use identical semantics;
Lasero should adopt one explicit convention instead of combining their labels with mixed behavior.

## Competitor behavior

| Workflow | LightBurn | Inkscape | Illustrator |
|---|---|---|---|
| Pairwise Boolean | Exactly two shapes/groups; subtraction uses second-selected operand against first | Union and several operations accept multiple selected paths; Difference uses stacking order | Pathfinder uses front/back stacking semantics for Minus Front |
| Multi-shape merge | Weld accepts any number; Boolean Union stays pairwise | Union accepts multiple paths | Unite accepts overlapping paths; Merge has separate attribute rules |
| Group vs compound path | Group preserves children as one transform/selection unit; ungroup releases members | Group/Ungroup distinct from Combine/Break Apart | Group/Ungroup distinct from Pathfinder; groups remain represented in Layers panel |
| Preview | Boolean Assistant previews result and commits on OK | Path actions apply immediately | Pathfinder supports destructive results and live effects |
| Layers | Color-coded processing operations and machine settings | Structural document hierarchy | Structural hierarchy; Merge Selected and Flatten Artwork are distinct |

Sources: [LightBurn Boolean Tools](https://docs.lightburnsoftware.com/latest/Reference//BooleanTools/),
[LightBurn Grouping](https://docs.lightburnsoftware.com/2.1/Reference/Grouping/),
[Inkscape Boolean Operations](https://gitlab.com/inkscape/inkscape-docs/manuals/blob/master/Inkscape-Beginners-Guide/source/boolean-operations.rst),
[Inkscape keyboard reference](https://inkscape.org/sl/doc/keys.html),
[Illustrator Pathfinder](https://helpx.adobe.com/uk/illustrator/desktop/manage-objects/reshape-transform-objects/pathfinder-panel-overview.html),
[Illustrator Group/Ungroup](https://helpx.adobe.com/illustrator/desktop/manage-objects/select-objects/group-ungroup-objects.html),
[Illustrator Merge/Flatten Layers](https://helpx.adobe.com/illustrator/desktop/manage-layers/create-and-organize-layers/merge-and-flatten-layers.html).

## Findings

### P1 — Group, compound path, and Break Apart are conflated

`SceneViewModel.GroupSelection` replaces selected scene objects with one `SceneObject` containing
their shapes. There is no persistent group/member tree. `UngroupSelection` then emits one object per
`LocalShape`; it cannot restore original object boundaries after grouping a compound SVG or an
object that already contains multiple contours. Selection panel also exposed two labels for this
same command. The duplicate is removed and the remaining command is labeled **Oddělit obrysy** to
describe its actual effect. True Group/Ungroup parity needs a document-level group node and stable
child identity; labels alone cannot supply that model.

### P1 — Boolean operations silently discard Bézier controls

Boolean input comes from `SceneObject.GetWorldShapes()` (flattened points), not authoritative
`VectorPath`. `VectorPath.DefaultFlattenToleranceMm` is 0.05 mm. `Clipper2VectorBooleanService`
returns polygon rings, and `CreateVectorObject` wraps those rings as straight segments. Boolean
results therefore remain node-editable, but original curves are irreversibly approximated. Group /
ungroup uses world `VectorPath` and preserves handles; Boolean operations do not. Next geometry work
needs either curve-aware Boolean support or an explicit approximation preview/tolerance before commit.

### P1 — Layer color merge was not undoable

Choosing a color already used by another processing layer reassigns shapes to that layer and removes
the old layer. The destination layer's speed, power, passes, and fill interval then govern the moved
geometry. This bypassed `SceneCommandStack`, so Ctrl+Z could not reverse the reassignment. Color
recolor/merge now snapshots layer settings, ordering, assignments, and selected layer in one undoable
command.

### P1 — Deleting a layer could delete a mixed-layer object wholesale

`DeleteLayerCommand` removes each `SceneObject` using the selected layer. A grouped/imported object
may also contain geometry assigned to other layers, so deleting one layer could erase unrelated
geometry. UI now blocks deletion when one object spans the layer and explains why. Safe completion is
to split only the targeted contours, preserve remaining object identity/metadata, and put the whole
operation in one command.

### P1 — “Sjednotit” and “Svařit” were duplicates

Both called the same unlimited-selection union. Lasero now follows the laser workflow distinction:
Boolean Union and Subtract/Intersect/Exclude require exactly two selected objects; Weld accepts two
or more. Pairwise operations use canvas stacking order for subtract (back shape minus front shape),
matching Illustrator and giving a stable visual rule. Tooltips explain operand count and order.
Inkscape's multi-selection Union remains achievable through Weld.

### P2 — No Boolean result preview

Lasero commits Boolean results immediately. LightBurn's Boolean Assistant lets the operator compare
Union, Subtract, and Intersection before confirming. Undo is available, but there is no live preview
or Cancel path. Add preview as a later transaction-level feature, not as separate destructive
commands.

### P2 — Layer settings still sit outside geometry undo

Layer speed, power, passes, mode, visibility, output enable, reorder, and duplicate are mostly direct
property/collection edits, not scene commands. Color-based assignment/merge is now undoable, but
Ctrl+Z still does not consistently rewind all processing-layer changes. A dedicated layer command
contract is needed; do not hide machine parameter changes inside a geometry operation.

### P2 — Split operations need more explicit result semantics

`Divide` accepts exactly two selected objects and creates up to three result objects (A−B, A∩B,
B−A); disconnected islands in each region remain one multi-contour object. `Fracture` accepts two
or more objects and partitions every overlap. These differ from Inkscape's Division/Cut Path and
Illustrator's Divide/Trim. Current labels/tooltips should explain which boundaries are cut and
whether disconnected pieces remain grouped; separate connected components only if the user asks for
that result.

## Changes in this audit pass

- Separate pairwise Boolean eligibility from multi-object Weld.
- Explain exact-two and subtract stacking semantics in both selection bar and canvas context menu.
- Make layer color reassignment/recolor/merge one undoable action, including redo and selected-layer
  restoration.
- Prevent layer deletion from destroying mixed-layer objects.
- Remove duplicate “Rozdělit skupinu” / “Rozdělit na části” actions and label actual contour splitting.

## Remaining implementation order

1. Add persistent group/member model; separate Group/Ungroup from compound-path Combine/Break Apart.
2. Add contour-preserving layer delete and explicit layer merge command with a destination-settings
   choice.
3. Add Boolean preview/confirm/cancel; expose operand A/B and result layer before commit.
4. Replace silent Boolean curve flattening with curve-aware operations or a visible tolerance/output
   preview.
5. Make layer processing edits and reorder/visibility changes undoable where an operator expects
   Ctrl+Z to apply.
6. Clarify Divide, Fracture, Cut Path, and Break Apart in tooltips and result selection.

## Verification policy

No tests or build run, per user instruction. Geometry, undo/redo, project persistence, laser output,
and WPF visual behavior still require validation before claiming production parity.

## Follow-up implementation in the same working tree

- Group/Ungroup now changes a persisted group ID on the original objects. Geometry, Bézier handles,
  text sources, individual layers, and object identities stay in place. A canvas click selects all
  available group members. Duplicate/Paste creates new group identities for the copies, including
  nested ancestry. Regrouping retains the previous membership and Ungroup releases one level.
- Break Apart is a separate contour operation. Editable text must first be converted to paths, and
  path/contour count mismatches are rejected.
- Layer deletion retains contours on other layers inside the same object, including aligned
  VectorPath subpaths, and restores the original object on Undo. Mixed editable text or inconsistent
  path caches are disabled rather than partly deleted.
- Layer merge is explicit in the layer context menu. The operator selects the destination and whether
  its settings or the source settings win. The earlier color collision shortcut now directs the user
  to that action. Layer row actions, reordering, duplication and direct inspector property edits enter
  the Undo stack.
- Boolean Union, Subtract, Intersect, Exclude, Divide, Fracture and Fill Between Paths prepare a
  before/after preview and commit only after confirmation. Pairwise operations treat a selected group
  as one operand. The dialog names the result layer and states when Bézier curves will be approximated
  to polygon segments at the current 0.05 mm flattening tolerance.

### Limitations before the curve and linked-offset follow-up below

- Boolean output is polygonal. The preview makes the approximation explicit; it does not provide
  mathematically exact curve-preserving clipping. Imported SVG curves were flattened on import before
  this workflow. Exact Bézier Boolean output needs a different geometry engine and SVG source model.
- Groups store membership and nested ancestry on each child rather than a separate group node. They
  keep members and processing layers intact; regrouping retains earlier membership for Ungroup. The inspector still does not
  expose a navigable group tree or an explicit enter-group editing mode.
- Divide/Fracture still emit one object per computed region, which may contain disconnected islands.
  Cut Path and dynamic/live Boolean effects need separate geometric implementations.
- No tests or build were run, following the user's current instruction.

## Curve and live offset follow-up

- Boolean Union/Weld, Difference, Intersection, Xor, Divide, Fracture, and Fill Between Paths now
  use curve-aware path operations when the source has a `VectorPath`. The resulting cubic controls
  remain in the editable model; flat render and toolpath shapes are derived from it. Legacy artwork
  that has no `VectorPath` still enters these operations as its existing polygon outlines.
- Filled islands are emitted as separate objects. A hole stays with the island it cuts out.
- The offset dialog offers a linked result. Its source IDs, operation parameters, and result kind
  persist in the project. Source changes, Undo/Redo, and project load regenerate the cached outline;
  missing sources suppress machine output until they return. Editing a linked result through the
  offset dialog updates its recipe in one undoable replacement.
- SVG export already writes editable cubic segments as SVG `C` commands; the new Boolean result
  follows that path. The current SVG importer likewise preserves supported curves when all imported
  contours share editable metadata. Unsupported or mixed metadata stays flat on import.
- Build and tests remain unrun at the user's request. Visual, geometry, persistence, and machine
  behavior require the user's own verification before any claim of full competitor parity.
