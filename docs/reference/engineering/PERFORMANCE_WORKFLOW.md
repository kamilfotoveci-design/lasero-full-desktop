# Lasero Performance Workflow

Use this before optimizing performance.

## 1. Define the slow interaction

Examples:
- node drag
- canvas zoom
- large SVG import
- toolpath generation
- streaming chat
- scene selection
- preview generation

Do not optimize vague complaints.

## 2. Measure first

Capture the relevant metric.

Examples:
- frame time
- operation latency
- allocations
- object count
- geometry rebuild time
- serialization time
- redraw frequency

## 3. Find the dominant cost

Prefer fixing the top bottleneck first.

Typical causes:
- scene-wide rebuild during local edit
- repeated serialization
- repeated coordinate transforms
- excessive object allocation
- UI tree recreation
- synchronous image processing on UI thread
- repeated geometry flattening
- unnecessary toolpath recalculation

## 4. Preserve correctness

Performance work must not weaken:
- undo/redo
- precision
- metadata
- transforms
- cancellation
- persistence

## 5. Prefer deferred expensive work

During live UI interaction:
- update only visible preview
- defer expensive derived state until commit

Example:

```text
pointer move
→ local preview only

pointer up
→ rebuild expensive dependent state
```

## 6. Avoid premature caching

Caching is allowed only when:
- invalidation rules are clear
- authoritative source is known
- correctness can be tested

Do not create stale duplicated state.

## 7. UI targets

For interactive editing aim for:
- simple interactions: under 8 ms typical update
- medium interactions: under 16 ms where practical
- complex operations: keep UI responsive and show progress

## 8. Compare before/after

Record:
- baseline
- change
- new result

Do not claim performance improvement without evidence.

## 9. Report

Report:
- measured bottleneck
- metric before
- change
- metric after
- correctness tests run
