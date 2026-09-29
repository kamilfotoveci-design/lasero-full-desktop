---
name: lasero-regression-testing
description: Design or review Lasero regression coverage for geometry, undo/redo, serialization, UI interaction, GRBL simulation, and multi-project builds.
metadata:
  short-description: Lasero regression testing
---

# Lasero regression testing

Use when adding, reviewing, or planning tests for Lasero changes. Read `docs/engineering/TESTING_STANDARDS.md` and `UNDO_REDO_CONTRACT.md`, then inspect the current solution and test projects instead of assuming an old test count or structure.

## Choose the right boundary

- Pure geometry, parser, path, preflight, and G-code behavior: test through Core/public production paths when possible.
- ViewModel and command behavior: exercise user-visible state and generated commands, not private helper trivia.
- WPF/Avalonia interaction: use the project's existing UI automation/harness if available; otherwise document the uncovered interaction and verify rendered behavior manually when practical.
- Persistence: test semantic save/load round-trips, schema compatibility, and recovery behavior where relevant.
- Machine behavior: use the virtual transport for deterministic protocol tests; never count simulator results as hardware certification.

## Regression matrix

For editable operations, cover commit, cancel, no-op, undo, redo, transformed objects, multi-selection, layer/metadata preservation, and path closure/subpaths where relevant. For import/export, use representative fixtures and semantic geometry comparisons. For machine work, cover command order, acknowledgements/errors, status/alarm/disconnect, bounds, power scaling, and the real/virtual routing seam as applicable.

Add the narrow regression that fails on the defect before the fix. Run the focused test, relevant project tests, broader solution tests when practical, and the relevant build. Separate baseline failures from new failures; do not report green from compilation alone.