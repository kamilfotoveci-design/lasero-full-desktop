# Lasero Definition of Done

A task is not done just because it builds.

## Behavior-changing work is complete only when:

- root cause or desired behavior is understood
- scope is clear
- smallest coherent implementation is used
- unrelated changes are preserved
- relevant regression/behavior tests exist
- focused tests pass
- relevant build passes
- broader tests are run when practical
- pre-existing failures are separated from new failures
- UI/UX is visually verified when applicable
- keyboard behavior is verified when applicable
- undo/redo and cancel behavior are verified for editing/transient operations
- transforms and layer/metadata are preserved where relevant
- no unnecessary dependency or debug/demo content remains

## UI, vector, and performance checks

UI work checks hover/focus/active/disabled, loading/error/empty, narrow layout, supported themes, restrained motion, and native Lasero feel.

Vector work preserves the editable source of truth, compound-path semantics, exact undo, and transform-safe editability.

Performance work includes baseline and after measurements with no correctness regression.

## Final report

Include files changed, behavior changed, tests, build/test result, pre-existing failures, and remaining limitations.
