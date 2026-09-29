# Lasero Undo / Redo Contract

Undo/redo is part of the editing model, not an optional convenience.

## 1. One gesture = one command

Examples:

```text
mouse down
120 pointer moves
mouse up
```

must produce one undo entry.

## 2. Preview is not committed state

Live preview must not corrupt the committed object.

Preferred:

```text
OriginalState
+ current total delta
→ PreviewState
```

Avoid cumulative mutation.

## 3. Cancel means exact restoration

Escape or cancel must restore the exact pre-operation semantic state.

Cancel must create no undo entry.

## 4. No-op actions create no history

Examples:
- click without movement
- enter/exit edit mode
- open preview and cancel
- drag back to exact original state
- selection-only changes unless intentionally persistent

## 5. Undo restores semantics

Undo should restore:
- geometry
- object identity where required
- transforms
- layer
- GeometrySetId
- editable path state
- metadata
- group membership where relevant

## 6. Redo is deterministic

```text
initial
→ operation
→ undo
→ redo
```

must return the same committed semantic result.

## 7. Commands must own immutable before/after state

Do not hand commands mutable references that may later be changed by live editing.

Snapshot or immutable state must be captured at the transaction boundary.

## 8. Multi-object operations

Boolean, group, ungroup, delete, duplicate, reorder, and batch edits must restore exact object sets and ordering where relevant.

## 9. Tests required

Every new editable operation should include:
- commit
- cancel
- undo
- redo
- no-op where applicable
