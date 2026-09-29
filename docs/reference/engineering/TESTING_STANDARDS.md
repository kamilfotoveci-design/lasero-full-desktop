# Lasero Testing Standards

## 1. Test behavior, not implementation trivia

Prefer tests that exercise production paths.

Good:

```text
SceneViewModel
→ domain operation
→ command history
→ resulting scene
```

Weak:

```text
private helper returns expected intermediate value
```

Helpers can have unit tests, but core behavior needs integration-level coverage.

## 2. Every bug fix needs a regression test

The test must:
- reproduce the real defect
- fail before the fix
- pass after the fix

## 3. User-editing operations

Test:
- commit
- cancel
- undo
- redo
- no-op
- transformed object
- non-default metadata where relevant

## 4. Vector behavior

When relevant test:
- open path
- closed path
- multiple subpaths
- holes
- reversed winding
- non-identity transform
- layer preservation
- GeometrySetId preservation
- exact undo restoration

## 5. Import/export

Use round-trip tests where practical:

```text
import
→ internal model
→ export
→ re-import
```

Compare semantic geometry, not only text.

## 6. Precision

Use tolerances appropriate to geometry operations.

Avoid unnecessarily loose tolerances that hide defects.

## 7. Baseline awareness

If the dirty tree already has known failures:
- document them
- do not attribute them to new work
- ensure no new failures appear

## 8. Focused then broad

Run:
1. focused test class
2. relevant project tests
3. full suite when practical

## 9. Naming

Test names should explain:
- condition
- action
- expected behavior

Example:

`CommitVectorPathEdit_PreservesLayerAndGeometrySet_AfterUndoRedo`

## 10. No false green

A task is not complete because:
- code compiles
- one happy-path test passes
- UI “looks okay”

Relevant behavior must be covered.
