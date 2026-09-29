# Lasero Undo / Redo Contract

Undo/redo is part of the editing model, not an optional convenience.

## 1. One gesture = one command

Many pointer moves between mouse down and mouse up produce one undo entry.

## 2. Preview is not committed state

Derive live preview from an immutable original state; never cumulatively corrupt committed state.

## 3. Cancel and no-op

Escape/cancel restores the exact pre-operation semantic state with no history entry. No-op actions such as click without movement or opening and cancelling a preview create no history.

## 4. Undo and redo restore semantics

Restore geometry, identity, transforms, layer, GeometrySetId, editable path state, metadata, and group membership where relevant. Undo followed by redo must be deterministic.

## 5. Command ownership and multi-object operations

Commands own immutable before/after state. Boolean, group, ungroup, delete, duplicate, reorder, and batch edits restore exact object sets and ordering where relevant.

## 6. Tests required

Every new editable operation includes commit, cancel, undo, redo, and no-op coverage where applicable.
