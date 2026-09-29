# Lasero Engineering Workflow

Use this workflow for meaningful engineering changes in Lasero.

## 1. Inspect before changing

Before coding:

- identify the exact subsystem
- inspect the current implementation
- inspect nearby patterns already used in Lasero
- identify current tests
- identify invariants that must not regress
- preserve unrelated dirty-tree changes

Do not start by rewriting architecture.

## 2. Define the problem precisely

State the problem in one sentence.

Examples:

Good:
> Node drag mutates committed geometry during preview and corrupts undo snapshots.

Bad:
> Vector editing is broken.

Good:
> Streaming chat messages cause layout churn because the entire conversation tree is rebuilt per token.

Bad:
> Chat is slow.

## 3. Research only when needed

Prefer, in this order:

1. existing Lasero implementation
2. repo guidance and local reference docs
3. official framework/library documentation
4. relevant GitHub issues/examples
5. external reference implementations

For WPF:
- prefer Microsoft/WPF documentation
- do not blindly port React/web patterns

For vector work:
- read `docs/reference/LIGHTBURN_VECTOR_PARITY.md`

For UI work:
- read `docs/ui-reference/*`

Keep research concise unless the task is architectural.

## 4. Choose the smallest coherent fix

Prefer:

```text
understand root cause
→ isolate smallest correct boundary
→ add regression test
→ implement
→ verify
```

Avoid:

```text
symptom
→ large rewrite
→ hope
```

Do not introduce a new abstraction unless the existing one is demonstrably insufficient.

## 5. Protect source-of-truth boundaries

Every subsystem should have a clear authoritative representation.

Examples:

```text
VectorPath = authoritative editable vector geometry
LocalShapes = derived rendering/legacy representation
```

Do not allow multiple mutable representations to drift independently.

## 6. Transactional user interactions

For user-editing operations:

```text
pointer down
→ capture original state

pointer move
→ derive preview from original state

pointer up
→ commit once

Escape
→ restore original state
```

One user gesture must equal one undo history action.

## 7. Tests before confidence

For bug fixes, add or extend a regression test that reproduces the bug.

For new behavior, test:
- happy path
- cancel path
- undo
- redo
- transforms if relevant
- metadata if relevant
- compound/multi-object behavior if relevant

Do not consider build success alone sufficient.

## 8. Verify in layers

After implementation:

1. focused tests
2. relevant project build
3. broader test suite if practical
4. visual/interaction verification when applicable
5. compare failures with documented baseline

Distinguish:
- pre-existing failures
- new failures

## 9. Preserve unrelated work

When the tree is dirty:

- do not reset unrelated files
- do not reformat unrelated code
- do not “clean up” nearby work unless required
- do not change architecture outside scope

## 10. Performance-sensitive work

Measure before optimizing.

Record:
- operation duration
- allocations if relevant
- UI frame time if relevant
- scene rebuild cost if relevant

Avoid speculative optimization.

## 11. Architecture changes

If a fix requires:
- new project/library extraction
- new authoritative model
- broad persistence changes
- cross-cutting scene representation changes
- large state-machine rewrite

stop and provide:
- current limitation
- smallest viable architecture change
- migration impact
- risks
- test strategy

Do not silently turn a bug fix into a platform rewrite.

## 12. Completion report

Report only what matters:

- root cause
- files changed
- behavior changed
- tests added
- build/test result
- pre-existing failures
- remaining risks/gaps
