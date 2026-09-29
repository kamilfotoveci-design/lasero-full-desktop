# Lasero Testing Standards

## 1. Test behavior, not implementation trivia

Prefer tests through production paths rather than private helpers.

## 2. Every bug fix needs a regression test

The test must reproduce the real defect, fail before the fix, and pass after it.

## 3. User-editing operations

Test commit, cancel, undo, redo, no-op, transformed objects, and non-default metadata where relevant.

## 4. Vector behavior

When relevant test open/closed paths, multiple subpaths, holes, reversed winding, non-identity transforms, layer preservation, GeometrySetId preservation, and exact undo restoration.

## 5. Import/export and precision

Use semantic round-trip tests where practical and tolerances appropriate to the operation. Avoid loose tolerances that hide defects.

## 6. Baseline awareness

Document known dirty-tree failures, do not attribute them to new work, and ensure no new failures appear.

## 7. Focused then broad

Run focused test class, relevant project tests, then the full suite when practical. Test names should explain condition, action, and expected behavior.

## 8. No false green

Compilation, one happy-path test, or a visual glance alone is not sufficient evidence.
