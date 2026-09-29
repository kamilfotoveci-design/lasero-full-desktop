# Lasero Performance Workflow

Use this before optimizing performance.

## 1. Define and measure

Name the slow interaction and capture the relevant metric: frame time, latency, allocations, object count, rebuild time, serialization time, redraw frequency, or toolpath time.

## 2. Find the dominant cost

Fix the top bottleneck first. Common causes include scene-wide rebuilds during local edits, repeated serialization/transforms, excessive allocation, UI-tree recreation, synchronous image processing, repeated flattening, and unnecessary toolpath recalculation.

## 3. Preserve correctness

Do not weaken undo/redo, precision, metadata, transforms, cancellation, or persistence.

## 4. Defer expensive work

During live interaction update only visible preview; regenerate expensive derived state on commit.

## 5. Avoid premature caching

Cache only when invalidation rules and authoritative source are clear and correctness can be tested.

## 6. UI targets and report

Aim for under 8 ms for simple interaction updates and under 16 ms for medium updates where practical. Report bottleneck, before metric, change, after metric, and correctness tests.
