# Lasero Bug Fix Workflow

Use this workflow for defects and regressions.

## 1. Reproduce

Before fixing:

- identify exact steps
- identify expected behavior
- identify actual behavior
- determine whether it is deterministic
- locate the first state divergence

If possible, create a focused automated reproduction.

## 2. Find the root cause

Do not patch only the visible symptom.

Trace:
- input
- state mutation
- transformation
- command/transaction boundary
- rendering/output

Ask:
- where did the wrong state first become possible?
- which invariant was violated?
- why did existing tests miss it?

## 3. Add a failing regression test

The test should fail for the original bug and pass after the fix.

Avoid tautological tests.

Prefer testing the real production path.

## 4. Implement the smallest safe fix

Do not refactor unrelated code.

If a helper is introduced:
- keep it narrow
- keep it domain-appropriate
- avoid creating a second source of truth

## 5. Verify cancellation and undo

For user-editing bugs always test commit, cancel, undo, redo, and no-op interaction.

## 6. Verify metadata and transforms

Where relevant preserve object identity, layer, GeometrySetId, transform, selection semantics, and editable vector data.

## 7. Verify the original bug

When practical, temporarily remove the critical fix, confirm the regression test fails, then restore it.

## 8. Report

Report root cause, exact fix, regression test, focused result, broader result, and pre-existing failures.
