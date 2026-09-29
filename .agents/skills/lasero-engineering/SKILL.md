---
name: lasero-engineering
description: Apply Lasero-specific engineering workflow to cross-cutting changes, audits, architecture decisions, and feature implementation in the desktop repository.
metadata:
  short-description: Lasero engineering workflow
---

# Lasero engineering

Use this skill for meaningful work spanning Lasero subsystems or when the task needs an audit-first plan. For focused vector, SVG, WPF, machine, or testing work, use the corresponding specialist skill too when helpful.

## Workflow

1. Read the root `AGENTS.md`, relevant instructions under `.agents/`, and the engineering references for the affected area.
2. Inspect the current code and dirty tree. Do not infer current status from README, HANDOFF, or dated audits without confirming the implementation.
3. State the concrete problem and trace input → authoritative state → mutation/command → undo/redo → rendering/persistence/output as applicable.
4. Identify the smallest coherent change and relevant regression cases before editing.
5. Preserve unrelated user changes. Do not reformat or clean unrelated files.
6. Implement, run the focused tests and build required for the change, then inspect the complete diff.
7. Report evidence, validation, pre-existing failures, and remaining gaps.

## Architecture guardrails

- The document/model is authoritative; UI code presents it. Keep `Lasero.Core` free of WPF references.
- `VectorPath` is the editable vector source of truth. Do not allow it and flattened render/toolpath geometry to drift.
- One editing gesture creates one undo transaction. Preview from immutable pre-gesture state; cancel restores it exactly.
- Machine safety gates are behavior, not decoration. Preserve `JobPreflight.Evaluate` and physical-machine truth.
- Verify the current solution/project structure: WPF and Avalonia projects may coexist, while older project docs can describe an earlier architecture.

## Severity

Use P0 for safety, data loss, corruption, or core workflow blockers; P1 for common workflow breakage or substantial quality loss; P2 for bounded polish or uncommon cases. Explain severity in terms of impact and reproducibility, not implementation size.

## References

Read `docs/engineering/ENGINEERING_WORKFLOW.md` for the engineering loop. For feature-specific details, load only the relevant specialist skill and project reference. Dated parity audits are evidence snapshots, not current truth; confirm each finding against code.