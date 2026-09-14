# LASERO vs LightBurn Vector Parity — Gap Matrix

Produced per the "Claude handoff instruction" in `LIGHTBURN_VECTOR_PARITY.md`. Format:
`Requirement | Current implementation | Gap | Architectural blocker | Proposed smallest change | Risk`

Conclusion up front: the existing `VectorPath`/`VectorSubpath`/`VectorNode` model
(`Lasero.Core/Scene/VectorPath.cs`) already satisfies the spec's §1 authoritative-model shape almost
exactly (nodes with in/out handles, Corner/Smooth types, subpaths, double precision throughout). **No
competing representation is introduced. The existing model is extended, not replaced**, per the
handoff instruction's explicit preference.

## Phase A — authoritative vector foundation

| Requirement | Current | Gap | Blocker | Smallest change | Risk |
|---|---|---|---|---|---|
| §1 VectorPath authoritative, LocalShapes derived | `VectorPath`→`Flatten()`→`ImportedShape.Points` is already one-directional (see boundary comment atop `VectorPath.cs`) | None for path-tool objects. Imported SVG/parametric objects still have no `VectorPath` at all (LocalShapes only) | Import/parametric-to-path conversion is a separate, larger project (§2, §68) | None this pass — out of scope | — |
| §2 Object types (parametric vs path vs group vs raster vs text) | `SceneObject` already distinguishes via `IsRaster`/`IsText`/`IsVectorPath`; parametric primitives (rect/ellipse) have no path form, only flattened `LocalShapes` | "Convert to path" for parametric objects does not exist | None — additive | Deferred (not required for node-edit trust; P1) | Low |
| §10 Bézier representation, double precision | `VectorNode(Anchor, HandleIn, HandleOut, Type)`, `Position(double,double,double)` | Satisfied | — | — | — |
| §11/§12 Smooth vs Corner semantics | `VectorPathEditor.MakeCollinear`/`MoveHandle` | Satisfied | — | — | — |

## Phase B — transaction-safe interaction

| Requirement | Current | Gap | Blocker | Smallest change | Risk |
|---|---|---|---|---|---|
| §7 pointer-down snapshot, preview from original+delta, one commit, Escape restores | Fixed this session's P0.1 pass: `VectorPathDragSession` snapshot + `FinishNodeEditDrag`/`CancelNodeEditDrag` restore-before-touching-command-stack | Satisfied | — | — | — |
| §59 no-op transactions (click without move, cancel, drag-back-to-start) | Click-without-move: fixed (reference-equality guard). Drag-back-to-start: **not** guarded (still commits a same-geometry replacement) | Minor: returning to start still creates an undo entry | None | Add value-equality check alongside the existing reference check | Low |
| §8 Shift-constrain node drag to 0/45/90/135° | Not implemented | Missing feature | None | Add angle snapping in `UpdateNodeEditDrag`'s translate branch, anchored at drag start | Low |
| §9 Arrow-key node nudge | **Bug**: arrow keys always call the whole-object `Nudge()` (`SceneCanvas.xaml.cs:2056-2061`), even while Node Edit is active with a node subset selected — moving the wrong thing | Node Edit intercepts nothing; nudging 1 of N selected nodes moves the whole object transform instead | None | Add a Node-Edit-active branch in `OnCanvasKeyDown` that nudges only `_selectedNodeKeys` via the existing `CommitNodeEdit`/translate path, falling back to `Nudge()` only when no nodes are selected | Low |
| §6 box-select modifiers (Shift add, Ctrl subtract) | Shift add exists (`FinishNodeMarquee`); Ctrl subtract/toggle absent | Missing modifier | None | Add Ctrl branch | Low |
| §6 enclosing vs crossing selection direction | Not distinguished | For **point** node selection this distinction is not meaningful (LightBurn's crossing-selection matters for extended shapes/segments, not point hits) | — | Deliberate deviation — documented, not implemented | — |
| §73 explicit interaction state machine | `DragMode` enum (`None/Select/Move/Resize/Rotate/Pan/Draw/PathTool/NodeEdit/NodeMarquee`) plus scattered nullable fields (`_draggedHandle`, `_nodeDragOriginalPath`, `_nodeEditObject`) act as an implicit state machine | Not the full state list in §73 (no distinct `DraggingSegment`/`BooleanPreview`/`OffsetPreview` states — those features don't exist yet either) | A full rewrite of `SceneCanvas.xaml.cs`'s ~2200 lines to a formal state machine is high risk for the return in this pass | Extend `DragMode` with `NodeSegmentDrag` for the new segment-drag feature (this pass); defer full rewrite until Boolean/Offset preview states are real features to model | Medium if attempted fully; Low for the incremental enum extension done here |

## Phase C — proper Bézier semantics

| Requirement | Current | Gap | Blocker | Smallest change | Risk |
|---|---|---|---|---|---|
| §16/§17 explicit line↔curve conversion | Implicit only (handles present/absent); no named operation | Missing explicit, reusable operation | None | Add `VectorPathEditor.ConvertSegmentToCurve`/`ConvertSegmentToLine` | Low |
| §15 drag straight/curved segment body | Not implemented — only double-click-to-insert-node hit-tests segments | Missing feature entirely | None | Add segment hit-test to mouse-down dispatch (priority below node/handle per §4, already the case since those route away first), new `DraggingSegment`-equivalent drag path using exact-point-constraint Bézier math | Medium (new geometry math + new interaction path) |
| §18/§19 insert node / insert midpoint | `VectorPathEditor.InsertNode(subpath, segmentIndex, t)` already exact (De Casteljau), already covers t=0.5 | Satisfied at the model level. No dedicated "insert midpoint" UI command (only double-click-nearest-point) | None | Deferred — model already correct, UI convenience only | Low |
| §20/§64 delete node, context-sensitive Delete | `DeleteSelectedNodes()` + `Delete` key already routes to it while Node Edit is active | Satisfied for nodes | — | — | — |
| §21/§64 delete segment (context-sensitive) | Not implemented — no segment selection/hover state exists | Missing feature | Needs segment hover/selection, which §15's segment hit-test also needs | Add `VectorPathEditor.DeleteSegment` (open/split) + wire to right-click-on-segment | Medium |

## Phase D — path topology

| Requirement | Current | Gap | Blocker | Smallest change | Risk |
|---|---|---|---|---|---|
| §22 break closed path at node | Not implemented | Missing | None | Add `VectorPathEditor.BreakAtNode` | Low-Medium (open-path internal-node case needs an explicit, tested definition — this pass defines it as "split into two subpaths") |
| §23 join endpoints by dragging (same path → close) | Not implemented as a drag gesture (`ToggleSelectedSubpathClosed` exists but is a toolbar button, not a drag-to-join) | Missing gesture | None | On node-drag mouse-up, if the dragged node is an endpoint and lands within snap tolerance of the other endpoint of the *same* subpath, call `Close()` instead of translating | Low |
| §23 join endpoints (different paths → combine) | Not implemented | Missing — requires merging two distinct `SceneObject`s into one, including LayerId/GeometrySetId reconciliation policy | Needs a new scene-level "merge two objects" command/policy, not just a `VectorPathEditor` operation | Deferred — genuinely separate design decision (which object's layer wins?) | High if rushed |
| §24 continue drawing from existing open endpoint | Not implemented | Missing — the "Čára" draw tool never looks at existing scene objects while drawing | Needs hit-testing against other objects' `VectorPath` while `DrawingPath` is active, plus the same object-merge question as above when the continued path is later closed/finished | Deferred | Medium-High |
| §27/§28 Close Path / Close-with-tolerance dialog | `ToggleSelectedSubpathClosed()` covers plain Close (no tolerance, no endpoint-moving option) | Tolerance dialog missing | None | Deferred (P1 polish, useful mainly for import repair per spec) | Low |
| §29 Auto-Join (tolerance-based, endpoints only) | Not implemented | Missing | None | Deferred | Low |
| §30 Break Apart (whole object) vs Break path at node | Distinct concepts per spec; neither "Break Apart" exists today (Ungroup exists but is a different operation on compound geometry sets) | Missing "Break Apart" | None | Deferred | Low |
| §31 Segment trim | Not implemented | Missing | Needs segment selection (same prerequisite as §15/§21) | Deferred | Medium |
| §32 Extend path | Not implemented | Missing | Needs open-endpoint extension math + preview | Deferred | Medium |
| §33 Align segment angle | Not implemented | Missing, explicitly lower priority per spec | None | Deferred | Low |

## Phase E — compound geometry / fill contract

| Requirement | Current | Gap | Blocker | Smallest change | Risk |
|---|---|---|---|---|---|
| §40 one object, multiple subpaths (holes) | `VectorPath.Subpaths` already supports N subpaths; `VectorPathSceneFactory` flattens each to its own `ImportedShape` sharing one `GeometrySetId` | Satisfied at the model level | — | — | — |
| §41 one documented fill contract across canvas/boolean/offset/toolpath/export/hit-test | **Not** unified — this is audit finding #4/#5 from the original P0 pass (`Clipper2VectorBooleanService.Resolve` omits precision; `ToolpathBuilder` uses layer-wide EvenOdd; offset does not normalize winding) | Confirmed still open; this is its own large, separate geometry-engine task | Requires one topology-normalization pass shared by every consumer | Deferred — this is P0.2 from the original audit, unchanged | High |
| §42 winding independence | Not normalized | Same as above | Same | Deferred | High |

## Phase F/G — booleans / offset

Unchanged from the original audit's findings; no work done this pass. `Clipper2VectorBooleanService` and
`VectorOffsetPlanner` exist but do not yet implement the transactional preview (§48/§49/§53) or the
holes-aware offset semantics (§54) this spec requires. Deferred — large, separate work depending on
Phase E's fill contract being settled first (the spec's own §76 ordering: booleans only "once compound
semantics are proven").

## Phase H — snapping and polish

Nothing implemented this pass (§34/§35/§36 snapping, §62/§63 cursor/hover feedback, §72 status HUD).
Deferred per the spec's own ordering (§76: snapping is Phase H, after booleans/offset).

---

## What this pass actually implements

Per the spec's own instruction ("Do not rewrite everything at once... propose the smallest
implementation sequence that makes basic node editing trustworthy first. No P1 feature work until the
vector foundation is proven by regression tests"), this pass completes **Phase B in full** and the
**highest-value, lowest-risk parts of Phase C and D**:

- §9 node-level arrow-key nudge (fixes a real bug: arrow keys moving the whole object instead of the
  selected nodes)
- §8 Shift-constrained node drag (0/45/90/135°)
- §6 Ctrl box-select subtract
- §59 drag-back-to-start no-op guard
- §16/§17 explicit `ConvertSegmentToCurve`/`ConvertSegmentToLine`
- §15 drag-a-segment-body interaction (line→curve on drag, curve reshape on drag), using exact
  minimal-movement Bézier control-point math, not an approximation
- §21 delete segment (open/split), wired to right-click on a segment
- §22 break closed path at a node
- §23 join-by-drag for the same-path case (closes the path)

Explicitly **not** attempted this pass (see rows above for why): cross-object path joining/continuing,
close-with-tolerance dialog, auto-join, break-apart, trim, extend, align-segment, the full formal state
machine rewrite, the unified compound-path fill contract, booleans, offset, and all snapping/hover/status
polish. These remain queued in the order the spec itself prescribes (§76).
