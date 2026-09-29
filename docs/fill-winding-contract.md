# Fill / winding contract (Phase 2)

Status: migration steps 1 and 2 IMPLEMENTED (decisions D-A NonZero per group, D-B fixture made
opposite-wound, D-C touching runs stay split, D-D only geometry-nested-unrelated-fill.gcode updated).
Sections 2-4 describe the behaviour BEFORE this change (the "current" wording is historical);
the characterization tests were flipped to "Agreement_" where the toolpath now matches the canvas.
Steps 3-6 (SVG fill-rule, offset normalisation, trace preview, NormalizeNestedCompoundPaths) remain.

Tests pinning everything below: `Lasero.Tests/FillWindingContractCharacterizationTests.cs` (19 cases).
Related: `docs/vector-and-machine-architecture-audit.md` 5.1 / 5.2 / 10.1, HANDOFF session 13 finding D9.

## 1. Vocabulary in the data model

- `ImportedShape` (`Lasero.Core/Import/ImportedShape.cs:7`) is one flattened contour. It carries
  `IsClosed`, `LayerId`, `GeometrySetId` and nothing about fill rule, role (outer/hole) or winding.
- `GeometrySetId` (`ImportedShape.cs:12`): "contours that together form one compound path".
  `Guid.Empty` is documented as legacy = "all contours within the same SceneObject are one compound
  path". That meaning is only honoured where the object boundary is still visible (canvas,
  `GroupSelection`); it is lost once shapes are flattened across objects (section 2, ToolpathBuilder).
- A hole is not declared anywhere. It exists only as a geometric relationship (containment plus,
  by convention, opposite winding).

## 2. Current behaviour per consumer

| Consumer | Rule | Grouping key | Where |
|---|---|---|---|
| Toolpath fill | **EvenOdd** on the crossings of all closed shapes pooled together | layer only. `GeometrySetId`, object identity and winding are never read | `Lasero.Core/Import/ToolpathBuilder.cs:85-146` (closed filter :87, crossing collection :102-114, sort + pair :119-135) |
| Toolpath cut | none, emits ring point order (direction matters, existing golden `geometry-reversed-path`) | per shape | `ToolpathBuilder.cs:65-83` |
| Canvas draw (generic) | **NonZero**, one `Path` per group, groups painted independently (so unioned) | `(GeometrySetId, LayerId, LayerColor)` inside one object; Empty = whole object | `Lasero.App/Controls/SceneCanvas.xaml.cs:491-494`, `:626-644` |
| Canvas draw (node-editable objects) | **NonZero**, one `PathGeometry` for the whole `VectorPath` (set/layer ignored) | object | `SceneCanvas.xaml.cs:592-594`, `:648-688` |
| Canvas fill decision | fill if layer mode is Fill/FillAndCut and group has a closed shape | group | `SceneCanvas.xaml.cs:584-599` |
| Hit test | per shape point-in-polygon; a hole does not exist (click in a counter selects the outer via its own polygon) | shape | `Lasero.App/Controls/SceneHitTester.cs:76-77` |
| Trace preview | **EvenOdd** (`IsFilled = fill`) | whole trace result | `Lasero.App/ViewModels/BitmapTraceViewModel.cs:202-208` |
| Boolean ops | EvenOdd inside each `GeometrySetId` group (`Resolve`), then `Union(EvenOdd)` across groups and folded across objects with the chosen op under EvenOdd | set (after `NormalizeNestedCompoundPaths`) | `SceneViewModel.cs:1320-1339`, `:1058`, `:1129` |
| Hole inference | closed, opposite sign, smaller, bbox-contained, first point inside -> child adopts parent's set id. Silent, runs before every boolean | pairwise | `SceneViewModel.cs:1344-1388` |
| Offset | Clipper `InflatePaths` per `GeometrySetId` group, no normalisation. Clipper treats orientation as meaningful, so winding decides hole vs second outer | set | `Lasero.Core/Geometry/VectorOffsetPlanner.cs:49-59`, `Clipper2VectorOffsetService.cs:22-34` |
| Open paths | never fill (toolpath filter :87, canvas `IsFilled = IsClosed`) | | pinned by `Current_OpenShapesDoNotAffectFill` |
| Layers | processing layers never interact (GCodeViewModel filters shapes per layer before calling the builder) | `LayerId` | `GCodeViewModel.cs:357-368` |

How shapes reach the toolpath: `SceneDocument.ToImportedDocument` (`Lasero.Core/Scene/SceneDocument.cs:21-45`)
does `SelectMany(o => o.GetWorldShapes())` and copies shapes as they are. It does not rewrite
`Guid.Empty`, so object identity is dropped at exactly the point the fill rule needs it.

## 3. Current behaviour per producer

| Producer | GeometrySetId | Winding of holes | Fill-rule info | Where |
|---|---|---|---|---|
| SVG import | always `Guid.Empty`, even inside one `<path>` | whatever the source has (y-flip is consistent, so relative winding is preserved) | `fill-rule` attribute **ignored** (no occurrence in Lasero.Core). SVG default is nonzero | `SvgImporter.cs:147-153`, `:60`; flatteners under `Import/Svg/` |
| Bitmap trace (filled) | one fresh id per traced object (root contour + descendants) | **declared**: even depth positive, odd depth negative, independent of extractor order | none needed, consistent for both rules | `Trace/CompoundPathBuilder.cs:54-55`, `:73-77`; `BitmapTracer.cs:84`, `:208-225` |
| Hand-drawn / node edit | one id per object, reused on `Rebuild` | user drawn, undeclared | none | `VectorPathSceneFactory.cs:48`, `:89` |
| Text | one id per string, all glyph contours | comes from WPF `FormattedText` geometry (glyph outlines, holes opposite); `Weld` unions via `Geometry.Combine` | none | `VectorTextFactory.cs:256-267`, `:286` |
| Boolean result | fresh id per operation, all result rings | Clipper output: outers positive, holes negative (asserted by `Clipper2VectorBooleanServiceTests` and the new `Agreement_BooleanResolveOutputFillsHollowInToolpathAndCanvas`) | none | `SceneViewModel.cs:1436-1463` |
| Offset result | rings from all groups in one `VectorPath`, one new object/set | Clipper output, opposite winding kept for a well-formed input (`Producer_OffsetKeepsOppositeWindingOfWellFormedCompound`) | none | `VectorOffsetPlanner.cs:32-38` |
| Primitives (rect/ellipse/polygon) | own id/Empty, single ring | n/a | n/a | `ScenePrimitiveFactory.cs` |

## 4. Where they disagree (all pinned by tests, all currently pass)

All disagreements come from one fact: the toolpath uses layer-wide EvenOdd while the canvas uses
per-group NonZero, and only the tracer, boolean and offset producers emit windings that make the two
rules coincide.

1. Same-wound nested rings in one set: toolpath hollow, canvas solid.
   `Disagreement_SameWoundNestedRingsInOneSet_ToolpathHollowCanvasSolid`.
   Note the existing golden `geometry-compound-path-hole-fill` feeds exactly this (both rects are
   counter-clockwise), so the golden's "declared compound path with a hole" is only a hole under
   EvenOdd.
2. Unrelated nested shapes (different sets): toolpath hollow, canvas two solid regions (D9).
   `Disagreement_NestedRingsInDifferentSets_ToolpathHollowCanvasSolid`.
3. Partially overlapping shapes: overlap lens is not engraved, canvas paints it.
   `Disagreement_PartiallyOverlappingShapes_OverlapIsUnengraved`,
   `Disagreement_PartialOverlapInOneSetBehavesLikeDifferentSets` (set id is irrelevant to the toolpath).
4. SVG: `fill-rule` dropped, so a same-wound nested `<path>` (solid under SVG's default) engraves
   hollow. `Current_SvgImporterIgnoresFillRuleAndLeavesGeometrySetEmpty`,
   `Disagreement_SvgSameWoundNestedPath_ToolpathHollowCanvasSolid`.
5. Two separately imported objects, both `Guid.Empty`: canvas treats them as two regions,
   `ToImportedDocument` pools them, toolpath carves one out of the other.
   `Disagreement_TwoLegacyEmptySetObjects_CarveEachOtherInToolpath`.
6. Offset of a same-wound nested pair: the hole is lost (rings merge into one outer), although the
   toolpath would engrave it hollow. `Disagreement_OffsetOfSameWoundNestedPairLosesTheHoleTheToolpathWouldEngrave`.
7. Trace preview uses EvenOdd; equal to NonZero for tracer output, so no visible disagreement today.

Where they agree (pinned): opposite-wound hole in one set (`Agreement_OppositeWoundHoleInOneSet...`,
`Agreement_SvgOppositeWoundHole...`, boolean output, tracer output via
`Producer_TracerForcesOuterPositiveAndHoleNegativeWinding`). Layers stay independent
(`Current_ShapesOnDifferentLayersDoNotCarveEachOther`). Fill output does not depend on ring direction
(`Current_FillOutputIsIndependentOfRingWindingDirection`).

## 5. Proposed contract

**The region a layer fills is the union, over compound groups, of each group's NonZero region.**

- Compound group = (owning SceneObject, `GeometrySetId`, `LayerId`). `Guid.Empty` means "the whole
  object", exactly as the canvas already interprets it. Object identity must therefore survive into
  the toolpath input.
- Within a group: NonZero. A hole is a ring wound opposite to its container; a same-wound nested
  ring merges. This is what the canvas, SVG (default) and Clipper/tracer/text outputs already mean.
- Across groups: union (never parity). Unrelated shapes cannot cut each other.
- Open paths never fill (unchanged). Layers stay the outermost key (unchanged).
- Producers guarantee "hole = opposite winding". Tracer, boolean, offset (well-formed input), text
  already do. SVG import must map `fill-rule="evenodd"` to a nesting-depth winding normalisation
  (and stop leaving nested `<path>` groups at `Empty` where that matters).
- Consumers never re-decide the rule: toolpath fill, offset, boolean, canvas share this definition.
  `NormalizeNestedCompoundPaths` is kept only as a repair fallback (later increment).

Why NonZero rather than EvenOdd within a group: the canvas is what the operator sees and approves,
the tracer explicitly targets NonZero (`CompoundPathBuilder.cs:68-71`), SVG's default is NonZero, and
switching the canvas to EvenOdd would change every same-wound overlap that renders correctly today.
EvenOdd-per-group is the smaller change for the toolpath but leaves canvas and toolpath disagreeing
for cases 1, 3 and 4. This is decision D-A below.

### Effect on current goldens (why this stops here)

- `geometry-nested-unrelated-fill.gcode`: inner scanlines Y 12.5-27.5 become full-width. Byte change.
- `geometry-compound-path-hole-fill.gcode`: fixture is same-wound, so under NonZero it also becomes
  solid. Either the golden changes, or the fixture's inner rect is made clockwise (output then
  byte-identical to today's golden, and the test then honestly represents a hole). Decision D-B.
- `GCodeEmitterDifferenceTests.D9_...` must be inverted, as its own comment says.
- Abutting shapes that share an edge currently emit two runs (M4 stays per run, laser off between);
  a union would merge them into one run. Not present in any golden today, but a real byte change for
  such jobs. Decision D-C (merge vs keep runs split at the seam).
- All other goldens (no nesting/overlap) stay byte-identical, provided run construction and the
  serpentine ordering are unchanged when no intervals overlap.

## 6. Minimal migration steps

Each step independent and testable; steps 1-2 are the "first increment" and need D-A..D-C.

1. `SceneDocument.ToImportedDocument` (`SceneDocument.cs:21`): replace `Guid.Empty` by a per-object
   id. Zero output change while the toolpath is still EvenOdd (it never reads the id); the existing
   `SceneViewModel.GroupSelection` already does this locally (`SceneViewModel.cs:962-970`).
2. `ToolpathBuilder.AppendFillLayer`: per scanline, compute NonZero intervals per group (signed
   crossings), union the intervals across groups, then feed the existing serpentine emission.
   Production files: `SceneDocument.cs`, `ToolpathBuilder.cs` (2 files). Goldens: see above.
3. `SvgImporter`: honour `fill-rule` and assign per-element set ids (separate decision; changes
   imported geometry of evenodd SVGs).
4. `VectorOffsetPlanner`: normalise winding per group before `OffsetClosedGroup` so offset agrees.
5. `BitmapTraceViewModel` preview to NonZero (cosmetic consistency).
6. Demote `NormalizeNestedCompoundPaths` to a fallback and surface when it fires (audit 10.1b).

## 7. Decisions needed from the user

- D-A: NonZero-per-group (recommended) or EvenOdd-per-group as the contract.
- D-B: for `geometry-compound-path-hole-fill`, change the golden (hole disappears) or change the
  fixture to a genuinely opposite-wound hole (golden stays byte-identical).
- D-C: merge abutting runs into one, or keep them split (only matters for shapes sharing an edge).
- D-D: authorise a documented golden update for `geometry-nested-unrelated-fill` (the D9 bug fix),
  with the diff reviewed line by line.
