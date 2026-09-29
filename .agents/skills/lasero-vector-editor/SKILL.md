---
name: lasero-vector-editor
description: Implement or audit Lasero vector editing, including paths, Bézier nodes, snapping, transforms, booleans, offsets, grouping, and topology operations.
metadata:
  short-description: Lasero vector editing
---

# Lasero vector editor

Use for changes to editable geometry or canvas editing behavior. First read `docs/reference/LIGHTBURN_VECTOR_PARITY.md`, `docs/engineering/UNDO_REDO_CONTRACT.md`, and the current vector model/editor implementation. The dated `NODE_EDIT_PARITY_AUDIT_2026-09-16.md` is not authoritative for current feature status.

## Invariants

- `VectorPath` is authoritative editable geometry; flattened `ImportedShape` points are derived/compatibility geometry. Keep subpath ordering aligned with shape metadata.
- Preserve anchors, handle-in/out, node types, closure, transforms, layer IDs, geometry-set IDs, visibility/output flags, and object identity where the operation promises preservation.
- Bézier handles are transformed as geometry, not reconstructed from flattened render points.
- A pointer gesture previews from an immutable snapshot and commits exactly once. Escape/cancel and no-op gestures create no history entry; undo/redo restore semantic state exactly.
- Multi-subpath contours and holes must retain consistent fill, rendering, boolean, offset, toolpath, and save/load behavior.
- Snapping must use the correct coordinate space and screen-space tolerance across zoom. Preserve explicit bypass behavior.
- Keep geometry finite and valid. Avoid excessive flattening nodes and unnecessary whole-scene recomputation during pointer movement.

## Change workflow

Trace pointer/key input → interaction mode and selection → path mutation → scene command → derived shape/render invalidation → persistence/toolpath. Use pure Core operations when possible and keep WPF hit targets as interaction views only. Check open/closed paths, multiple subpaths, transformed objects, groups, multi-select, cancel, no-op, undo, redo, save/load, and snapping for relevant changes.

When a boolean or conversion cannot preserve exact cubic geometry, state the representation change and test geometric deviation. Do not call a result curve-preserving merely because it remains node-editable as line segments.

## Current-code caveat

Feature status changes quickly. Inspect `VectorPathEditor`, `SceneCanvas.VectorPathTool`, `VectorPathSceneFactory`, `SceneObject`, and `SceneViewModel` before acting. Current code may already provide snapping, reverse, cross-object joining, and curve-aware group/ungroup even where dated audits say otherwise.