# Fill / winding contract (Phase 2)

Status: migration steps 1-6 done (see section 6 for what each turned out to need). Sections 2-4
describe behaviour AFTER the change. The original pre-change analysis is in the git history of this
file (commit 3eca4f8).

Tests pinning everything below: `Lasero.Tests/FillWindingContractCharacterizationTests.cs` (all Agreement_/Contract_/Producer_/Repair_ tests).
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

## 2. Behaviour per consumer (after migration steps 1-6)

| Consumer | Rule | Grouping key | Where |
|---|---|---|---|
| Toolpath fill | **NonZero per group** from signed scanline crossings, runs of different groups **unioned**; runs that merely touch stay separate | `GeometrySetId` (legacy `Guid.Empty` resolved to the owning object's id by `SceneDocument.ToImportedDocument`), inside one layer | `Lasero.Core/Import/ToolpathBuilder.cs` `AppendFillLayer`; `Lasero.Core/Scene/SceneDocument.cs` `ToImportedDocument` |
| Toolpath cut | none, emits ring point order (direction matters, golden `geometry-reversed-path`) | per shape | `ToolpathBuilder.AppendCutLayer` |
| Canvas draw (generic) | NonZero, one `Path` per group, groups painted independently (so unioned) | `(GeometrySetId, LayerId, LayerColor)` inside one object; Empty = whole object | `Lasero.App/Controls/SceneCanvas.xaml.cs` `CompoundGroups`, `BuildCompoundGeometry` |
| Canvas draw (node-editable) | NonZero, one `PathGeometry` for the whole `VectorPath` | object | `SceneCanvas.xaml.cs` `BuildVectorPathGeometry` |
| Hit test | per shape point-in-polygon (holes are not modelled) | shape | `Lasero.App/Controls/SceneHitTester.cs` |
| Trace preview (filled) | NonZero (was EvenOdd), one geometry per traced object = per group | traced object | `Lasero.App/ViewModels/BitmapTraceViewModel.cs` `BuildPreviewGeometry` (see note below) |
| Boolean ops | `Resolve(EvenOdd)` inside each set, then Union across sets and folding across objects. For well-formed input (holes wound opposite) EvenOdd and NonZero coincide; only same-wound nested rings in ONE set differ (boolean: hole, fill: merge) | set | `SceneViewModel.BuildSourceRings`, `UniteSelection`, `CombineSelection` |
| Hole repair | `NormalizeNestedCompoundPaths`: repair fallback for legacy geometry whose contours carry separate set ids (old text). Links opposite-wound, contained contours of DIFFERENT sets as hole+parent, i.e. the contract's own hole definition. Never touches shapes already in one set; now logs every reclassification | pairwise | `SceneViewModel.NormalizeNestedCompoundPaths` |
| Offset | Clipper `InflatePaths` per set. Behaves per the contract without normalisation: same-wound nested rings merge, opposite-wound holes are kept whatever the outer's absolute direction | set | `VectorOffsetPlanner.ComputeOffsetRings`, `Clipper2VectorOffsetService` |
| Open paths | never fill | | `ToolpathBuilder` closed filter; canvas `IsFilled = IsClosed` |
| Layers | never interact | `LayerId` | `GCodeViewModel.BuildSceneGCode` |

Note on the trace preview: the one-line change (`FillRule.Nonzero`) lives in `BitmapTraceViewModel.cs`,
whose surrounding preview function is uncommitted work by another agent and was therefore left
in the working tree for that owner to commit with the rest of the file.

## 3. Producers (after migration steps 1-6)

| Producer | GeometrySetId | Hole convention | Where |
|---|---|---|---|
| SVG import | `Guid.Empty` for every contour = one compound group per imported object | source winding kept for nonzero/default. `fill-rule="evenodd"` (attribute, `style`, or inherited from a group) re-winds rings by nesting depth (even positive, odd negative), on flattened shapes and `VectorPath` together | `SvgImporter.cs` `ReadEvenOdd`, `EvenOddReverseFlags` |
| Bitmap trace (filled) | one id per traced object | declared: even depth positive, odd negative | `Trace/CompoundPathBuilder.cs` |
| Hand-drawn / node edit | one id per object | user drawn (opposite winding = hole) | `VectorPathSceneFactory.cs` |
| Text | one id per string | WPF glyph outlines, holes opposite | `VectorTextFactory.cs` |
| Boolean result | fresh id per operation | Clipper output: outers positive, holes negative | `SceneViewModel.ToImportedShapes` |
| Offset result | one new object | Clipper output, opposite winding preserved | `VectorOffsetPlanner.ComputeOffset` |
| Primitives | own id/Empty, single ring | n/a | `ScenePrimitiveFactory.cs` |

## 4. Agreement matrix and remaining gaps

Toolpath fill, canvas, SVG import, tracer, boolean (well-formed input) and offset now answer the
same inside/outside question, pinned by the `Agreement_*`, `Contract_*`, `Producer_*` and
`Repair_*` tests in `Lasero.Tests/FillWindingContractCharacterizationTests.cs`
(same-wound nested rings merge; opposite-wound rings are holes; unrelated shapes never carve each
other; overlap is engraved; legacy `Guid.Empty` objects stay independent; evenodd SVGs keep
their holes; offset keeps or merges consistently).

Remaining gaps, deliberately not changed here:
1. Boolean `Resolve(EvenOdd)` inside one set still turns same-wound nested rings into a hole while
   fill/canvas merge them. Switching it to NonZero would change user-visible boolean results and the
   fixture of `SceneViewModelTests.UniteSelectionPreservesAnExistingHoleInOneOfTheSources`
   (same-wound). Needs a user decision.
2. `NormalizeNestedCompoundPaths` cannot be removed: `UniteRepairsCounterInLegacyTextGroupWithSeparateGeometryIds`
   fails without it. It is now documented and logged, not surfaced in the UI (UI change out of scope).
3. Hit test does not model holes.
4. Cut layers and `VectorPath` canvas rendering ignore `GeometrySetId` (whole object = one path).

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

## 6. Migration steps and outcome

1. Done (f80bac7): `SceneDocument.ToImportedDocument` resolves `Guid.Empty` to the object's id.
2. Done (f80bac7): NonZero-per-group fill with union; one golden updated as authorised.
3. Done: SVG `fill-rule="evenodd"` re-winding; nonzero/default untouched.
4. No code needed: offset already follows the contract; tests added.
5. One-line NonZero change in the trace preview, left in the working tree (see note in section 2).
6. Kept as a documented, logged repair fallback rather than removed (a legacy-text test depends on it).

## 7. Decisions

D-A to D-D were approved and implemented. Open: D-E, whether boolean `Resolve` inside one set should
move to NonZero (remaining gap 1 in section 4).

Original list:

- D-A: NonZero-per-group (recommended) or EvenOdd-per-group as the contract.
- D-B: for `geometry-compound-path-hole-fill`, change the golden (hole disappears) or change the
  fixture to a genuinely opposite-wound hole (golden stays byte-identical).
- D-C: merge abutting runs into one, or keep them split (only matters for shapes sharing an edge).
- D-D: authorise a documented golden update for `geometry-nested-unrelated-fill` (the D9 bug fix),
  with the diff reviewed line by line.
