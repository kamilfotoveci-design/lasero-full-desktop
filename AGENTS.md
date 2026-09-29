# Lasero engineering rules

Lasero is a production Windows laser-design application. Prioritize reliability, geometry correctness, user trust, machine safety, and regression prevention over feature speed.

## Before meaningful changes

Before modifying editor, geometry, document, rendering, machine, or job code:

1. Inspect the current implementation and relevant project instructions first.
2. Trace the data flow from user input through authoritative state, mutation, undo/redo, rendering, persistence, and output as applicable.
3. Identify existing tests and the smallest missing regression coverage.
4. Prefer the smallest safe architectural change; preserve unrelated dirty-tree work.
5. Implement, test, build, and review the final diff for regressions and unintended duplicate state.

Follow `docs/engineering/ENGINEERING_WORKFLOW.md`, `TESTING_STANDARDS.md`, and `UNDO_REDO_CONTRACT.md` where applicable. Confirm project structure and target frameworks from the current solution and project files; older docs and audits may be stale.

## Architecture and source of truth

- Treat domain/document state as authoritative. WPF controls and visuals are views; transient pointer state may live in controls when needed, but must not duplicate committed document state.
- Keep geometry independent from WPF wherever practical. `VectorPath` is the editable path source of truth; flattened shapes are derived/legacy geometry and must remain consistent with it.
- Keep one owner for each persistent or machine state. Do not create parallel stores, viewmodels, renderers, or theme systems without an explicit architecture decision.
- Preserve dependency boundaries. `Lasero.Core` must not reference WPF.
- Prefer explicit services for snapping, coordinate transforms, undo/redo, editor interaction, machine capabilities, and preflight when the existing design warrants them.

## Vector editing

For vector changes, consider open and closed paths, multiple subpaths and holes, transformed objects, groups, multi-selection, snapping, node handles, save/load, path validity, and undo/redo. Preserve genuine Bézier data through operations whenever mathematically and architecturally possible. Do not silently turn curves into coarse polylines or generate excessive nodes. Geometry must not contain NaN/Infinity, invalid segment references, broken closure, zero-length corruption, or inconsistent node/handle state.

## UI behavior

Do not patch a visible symptom before tracing the interaction state and source of truth. Preserve mouse capture and cancellation behavior across release outside the canvas, lost capture, deactivation, and focus changes. Verify zoom and DPI behavior. Follow the existing WPF architecture and Lasero design system; do not port web patterns literally.

## Machine safety

Machine safety has higher priority than convenience. Every physical job follows:

`Generate → Validate → Preview → Preflight → Send`

Never bypass `JobPreflight.Evaluate`, fabricate machine states, weaken bounds checks, or send output whose validity is unknown. Treat `$30` power range, `$32` laser mode, M3/M4 semantics, work bounds, firmware capabilities, alarms, disconnects, and stream cancellation as safety-critical. UI power is percentage-based and must be mapped to the connected controller's actual S range. A catalog entry or simulator pass is not hardware verification; claim production support only for exact configurations tested on real hardware.

## Scope and completion

Keep fixes in scope; broad refactors need a concrete reason. For meaningful behavior changes, add or update regression tests and run the relevant tests/build. Build success alone does not establish correctness. Check undo/redo, save/load, warnings, crash paths, and visual behavior when relevant. Report changed files, behavior, verification, pre-existing failures, and remaining limitations.

## Skill routing

Use the most relevant available Lasero skill before specialized work:

- `lasero-engineering` for cross-cutting work, architecture, audit-first planning, and source-of-truth decisions.
- `lasero-vector-editor` for vector geometry, nodes, Bézier paths, snapping, transforms, booleans, offsets, joining, or splitting.
- `lasero-svg-pipeline` for SVG import/export, transforms, text-to-path, unsupported SVG features, or vector interchange.
- `lasero-wpf-quality` for WPF UI, rendering, interaction, DPI, focus, input, windows, or visual defects.
- `lasero-machine-safety` for GRBL, G-code, serial communication, machine profiles, power scaling, preflight, or physical-machine behavior.
- `lasero-regression-testing` when designing, adding, or reviewing regression tests and validation coverage.

Skills add task-specific workflows; they do not override explicit user scope or repository architecture.