# Lasero vector interaction contract — 2026-10-13

This is the intended WPF canvas contract for the current implementation and the next editing passes. Explicit deviations are tracked in `VECTOR_EDITOR_GAP_AUDIT.md`.

## Selection

- Click editable visible object: select. Click an already selected object: retain multi-selection for drag.
- Shift-click: add/remove a member. Ctrl-click: subtract/toggle according to the existing canvas convention. Alt-click overlapping artwork: cycle candidates front to back.
- Empty click: clear selection. Marquee left-to-right should enclose; right-to-left should cross. Hidden and locked objects never enter mutable selection; visible locked geometry may only act as a documented snap reference.
- `Ctrl+A`: visible unlocked objects in Select mode; nodes of active path in Node Edit mode. `Escape`: cancel current gesture, then leave node/group context, then clear selection, one layer per press.
- Double-click path: Node Edit. Double-click parametric shape: explicit conversion to path before node editing. Double-click text: inline text edit.

## Pointer gesture

1. Mouse down captures immutable original geometry/transforms and pointer position.
2. Movement below 3 screen pixels is a click, not a transform.
3. Preview derives from original state and current pointer; snapping tolerance is measured in screen pixels then converted to document units.
4. Mouse up commits one undo command if semantic state changed.
5. Escape, capture loss, deactivation or unload cancels and restores original state; no undo entry.

## Node Edit

- Node/handle targets take priority over segments, filled path body and canvas. Visible target sizes stay usable at high DPI and zoom.
- Click selects node; Shift-click extends/toggles. Drag selected nodes moves that set. Drag handle edits curve; direct segment drag reshapes curve without cumulative drift.
- Double-click segment inserts node. Delete removes selected nodes or hovered segment according to context; invalid empty contours are removed, and deleting final contour deletes object via history.
- Open/close, break/join, reverse and line/curve conversion operate on authoritative `VectorPath` and keep `LocalShapes` synchronized. Changes affecting ambiguous multi-layer contour ownership must fail visibly rather than move geometry between layers.

## Snapping and layers

- Move uses selection bounds anchors; node drag uses the actual node anchor, preserving pointer grab offset. Candidate list is captured at gesture start so moving geometry cannot snap to its own live preview.
- Hidden objects are excluded. Temporary bypass uses Ctrl during a live node gesture. Layer visibility, edit lock and laser output remain separate document properties.
- Compound fill uses Nonzero winding across canvas, hit testing, Boolean input, offset and SVG. Holes must have opposing contour direction; open contours contribute stroke only.

## History and output

- Copy captures value state at Copy time. Paste duplicates IDs/groups and reconciles layer recipes in one undoable operation.
- Undo/Redo restores geometry, contour metadata, layer ownership, group membership and object identity. One drag equals one command.
- Machine jobs remain under Generate → Validate → Preview → Preflight → Send. UI editing never bypasses preflight.

This contract is not a claim that every line is already implemented. The audit lists known deviations and manual checks. No build or tests were run at user request.
