# Lasero Engineering Workflow

Use this workflow for meaningful engineering changes in Lasero.

## 1. Inspect before changing

Identify the exact subsystem, current implementation, nearby Lasero patterns, current tests, and invariants that must not regress. Preserve unrelated dirty-tree changes. Do not start by rewriting architecture.

## 2. Define the problem precisely

State the problem in one sentence. Describe the first wrong state, not a vague symptom.

## 3. Research only when needed

Prefer existing Lasero implementation, repo guidance, official framework/library documentation, useful GitHub examples, then external references. For WPF prefer Microsoft/WPF documentation and do not port React patterns literally. For vector work read `docs/reference/LIGHTBURN_VECTOR_PARITY.md`; for UI work read `docs/ui-reference/*`. Keep research concise.

## 4. Choose the smallest coherent fix

Understand the root cause, isolate the smallest correct boundary, add a regression test, implement, and verify. Do not introduce an abstraction unless the existing one is demonstrably insufficient.

## 5. Protect source-of-truth boundaries

Every subsystem needs one authoritative representation. Editable `VectorPath` geometry is authoritative; `LocalShapes` is derived rendering/legacy state. Do not allow mutable representations to drift independently.

## 6. Transactional user interactions

Pointer down captures original state, pointer move derives preview from that state, pointer up commits once, and Escape restores the original. One user gesture equals one undo history action.

## 7. Tests before confidence

For bug fixes add a reproducing regression test. For new behavior test happy path, cancel, undo, redo, transforms, metadata, and compound/multi-object behavior where relevant. Build success alone is insufficient.

## 8. Verify in layers

Run focused tests, relevant build, broader tests when practical, visual/interaction verification when applicable, and compare failures with the documented baseline.

## 9. Preserve unrelated work

Do not reset, reformat, clean up, or change architecture outside scope.

## 10. Performance-sensitive work

Measure before optimizing, fix the dominant cost, preserve correctness, and defer expensive derived work until commit.

## 11. Architecture changes

If a fix requires a new project, authoritative model, broad persistence change, cross-cutting representation change, or large state-machine rewrite, document limitation, smallest change, migration impact, risks, and test strategy before proceeding.

## 12. Completion report

Report root cause, files changed, behavior changed, tests, build/test result, pre-existing failures, and remaining risks/gaps.
