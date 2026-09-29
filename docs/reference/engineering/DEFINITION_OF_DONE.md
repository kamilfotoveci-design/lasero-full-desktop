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
- pre-existing failures are clearly separated from new failures
- UI/UX is visually verified when applicable
- keyboard behavior is verified when applicable
- undo/redo is verified for editing operations
- cancel behavior is verified for transient operations
- transforms are preserved where relevant
- layer/metadata is preserved where relevant
- no new unnecessary dependency is introduced
- no temporary debug/demo content remains

## UI work also requires:

- hover/focus/active/disabled states checked
- loading/error/empty states checked
- narrow layout checked
- dark/light theme checked if supported
- motion remains restrained
- result feels native to Lasero

## Vector work also requires:

- editable source-of-truth preserved
- no reverse reconstruction from derived rendering unless explicitly unavoidable
- compound paths/holes remain semantically consistent
- undo restores exact original state
- transforms do not destroy editability

## Performance work also requires:

- baseline measurement
- after measurement
- no correctness regression

## Final report should include:

- files changed
- behavior changed
- tests added/updated
- build/test result
- pre-existing failures
- remaining limitations
