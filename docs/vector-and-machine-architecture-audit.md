# LASERO — Vector Engine & Machine Compatibility Architecture Audit

Date: 2026-09-28
Scope: `Lasero.Core`, `Lasero.App`, `Lasero.Tests` at branch `design-system-tokens`.
Method: source inspection only. No code was modified. Where a repo document and the code
disagreed, the code was treated as authoritative and the discrepancy is flagged explicitly.

Related existing documents (read these too — this audit builds on them rather than restating them):
- `docs/reference/LIGHTBURN_VECTOR_PARITY.md` + `LIGHTBURN_VECTOR_PARITY_GAP_MATRIX.md` — per-requirement vector parity spec and gap matrix (Phases A–H)
- `docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md`
- `docs/machine-compatibility-audit.md` + `docs/machine-compatibility-matrix.md` — the 5 priority machines, evidence register
- `docs/engineering/UNDO_REDO_CONTRACT.md`

---

## 1. Executive summary

**The vector engine is in much better shape than the project's own top-level docs suggest, and the
machine layer is in worse shape than its file names suggest.**

On the vector side, LASERO already has the thing most laser applications never build: a real,
UI-independent, double-precision cubic-Bézier document model (`VectorPath`/`VectorSubpath`/`VectorNode`)
with a pure static operation set (`VectorPathEditor`), custom hit-testing, transactional drag sessions,
snapshot-based undo, and — as of commit `cc0bead` — a curve-preserving SVG path importer. Node editing
is genuinely LightBurn-class in operation coverage: insert/delete node, delete segment, break at node,
close/open, reverse, cross-object join, explicit line↔curve conversion, and segment-body dragging with
exact Bézier math are all implemented and unit-tested. This is a strong foundation. It is *not* a
prototype that needs replacing.

The vector engine's real problems are narrower and deeper than "missing features":
1. **There is no single fill/winding contract.** Canvas rendering, boolean ops, offset, toolpath fill
   and hit-testing each decide "what is inside this shape" independently, using different rules. The
   repo's own gap matrix already identifies this (Phase E, §41/§42) and correctly calls it the
   blocking item. It is the single most important thing in this document.
2. **Holes are inferred, never declared.** There is no hole/inner-contour concept in the data model.
   Hole-ness is reconstructed at boolean time by a geometric heuristic
   (`NormalizeNestedCompoundPaths`) that can silently reclassify unrelated nested shapes.
3. **Boolean/offset results are one-way trapdoors** — every result comes back as an all-`Corner`
   polygon, so a curvy imported logo stops being curve-editable the moment it's unioned.
4. **Two formats that a professional user considers table stakes do not exist: DXF import and SVG
   export.** Neither has any code at all.

On the machine side, the honest summary is: **LASERO has one machine driver, not a machine
architecture.** `ILaserMachine` exists and is genuinely load-bearing (injected into `GCodeJobRunner`,
`ConnectionViewModel`, `DeviceScanner`), and `IGrblTransport` is a clean, proven seam — a network/WiFi
transport is genuinely additive. But everything above the transport is one monolithic GRBL protocol
state machine, and everything about "what machine is this" is a compiled-in array of six strings with
a capability accessor that returns the same "Unresolved/Unverified" answer for every capability on
every machine.

The good news, and it is significant: **the codebase does not have the scalability disease you were
worried about.** There is no `if (machine.Name == "xTool S1")` anywhere in `Lasero.App` — a grep for
`xTool|AlgoLaser` across all App C# code returns zero logic hits. Machine identity is already confined
to one file. The team also made a deliberate, well-tested safety decision to *fail closed* on every
unverified machine rather than guess. So the work ahead is filling in a structure that is already
correctly placed, not untangling a mess.

**The core verdict on your question** — is this strong enough to evolve into a serious LightBurn
alternative across many machines? The vector engine: yes, after the fill-contract work, with no
rewrite. The machine layer: not yet, but the two seams that would have been expensive to retrofit
(`ILaserMachine` and `IGrblTransport`) already exist and are correct. What's missing is the middle:
a machine-independent job model, a post-processor layer, and real capability data. That is additive
work on top of correct foundations, not a rewrite.

---

## 2. Current vector architecture

### 2.1 The data model

`Lasero.Core/Scene/VectorPath.cs` defines a three-level immutable model:

| Type | Lines | Shape |
|---|---|---|
| `VectorNode` (readonly record struct) | 54–70 | `Position Anchor`, `Position? HandleIn`, `Position? HandleOut`, `VectorNodeType Type` (`Corner`/`Smooth`) |
| `VectorSubpath` | 74–131 | `IReadOnlyList<VectorNode> Nodes` + `bool IsClosed`; `Segment(i)` wraps mod N for the closing segment (85–92) |
| `VectorPath` | 136–174 | `IReadOnlyList<VectorSubpath> Subpaths` — a compound object is N subpaths in *one* object |

A null handle means "straight on that side"; a segment is straight only when both adjacent handles
are null (`VectorSubpath.IsStraightSegment`, line 96). Flattening is adaptive De Casteljau
subdivision against a **fixed** tolerance `VectorPath.DefaultFlattenToleranceMm = 0.05` (line 140),
recursion-capped at depth 20 (`CubicBezier.Flatten`, 216–227).

This satisfies everything a LightBurn-class editor needs at the model level, and the repo's own gap
matrix reaches the same conclusion ("the existing model is extended, not replaced").

### 2.2 The dual representation (important)

Every `SceneObject` carries geometry **twice**:
- `SceneObject.VectorPath` — the authoritative curve model (nullable; only path-backed objects have it)
- `SceneObject.LocalShapes` — a flattened `ImportedShape.Points` polyline cache used for rendering,
  hit-testing, boolean input, and toolpath generation

`VectorPathSceneFactory.Create`/`Rebuild` regenerates the second from the first. This is a deliberate
cached-render-of-source pattern, documented in `SceneObject.cs:44–58`, and it is the same shape as
`TextSource` → `SceneObject.Text`. It is defensible. But it is a genuine dual representation, and
every consumer must know which one it is holding. Two concrete consequences appear later in this
document: the fill-contract problem (§5.1) and the boolean curve-loss problem (§5.3).

One convention mismatch worth naming: `VectorSubpath.Flatten()` on a closed subpath **repeats the
first point as the last** (documented at `VectorPath.cs:98–104`), while Clipper2 ring output does
**not** (`Clipper2VectorBooleanService.ToRings`, 66–83). Both conventions are correct in their own
layer, but nothing in the type system distinguishes them — they are both `IReadOnlyList<Position>`.

### 2.3 Operations, interaction, and undo

- **`VectorPathEditor.cs`** (`Lasero.Core/Scene/`, ~24 KB) — pure static operations over subpaths, no
  WPF references, all returning new immutable instances. This is the right shape.
- **`VectorPathHitTester.cs`** — custom segment hit-testing: exact projection for straight segments
  (43–53), 39-sample parametric search for cubics with a control-hull bounds pre-check (82–92).
  Deliberately *not* WPF geometry hit-testing.
- **`SceneHitTester.cs`** (`Lasero.App/Controls/`) — whole-object selection, also custom math,
  explicitly independent of WPF element hit-testing (comment at line 16).
- **Undo** — `ISceneCommand` + `SceneCommandStack` (two stacks, `Changed` event). Every command is
  snapshot/wholesale-reassignment; node edits route through `ReplaceObjectsCommand` after
  `VectorPathSceneFactory.Rebuild` produces a brand-new `SceneObject`
  (`SceneViewModel.CommitVectorPathEdit`, 436–460). **No command patches geometry in place.**
- **Drag transactions** — `VectorPathDragSession` captures pre-drag `LocalShapes` so live preview
  (which mutates `obj.LocalShapes` directly every frame, `RenderVectorPathLive`, 1068–1073) can never
  contaminate the undo snapshot. `FinishNodeEditDrag`/`CancelNodeEditDrag` (1081–1162) restore-then-
  commit. A structural-equality no-op guard (`VectorPathsStructurallyEqual`, 1035–1046) prevents
  spurious undo entries — needed because records compare `IReadOnlyList<VectorNode>` by reference.

This undo/drag design directly satisfies `docs/engineering/UNDO_REDO_CONTRACT.md` §1–3 and is one of
the better-engineered parts of the codebase.

### 2.4 Import

`SvgImporter.cs` runs **two parsers** per `<path>` element and accepts the curve-preserving result only
under an all-or-nothing contract (48–59, 164–171): `ParseToVectorSubpaths` must produce the same
subpath count as `Flatten`, every subpath must have ≥2 nodes, and metadata must be uniform across all
shapes — otherwise the whole document imports as flattened polylines with `vectorPath = null`. Q/T
quadratics convert to exact equivalent cubics (`SvgPathParser.cs:330–331`); `A` arcs go through
`SvgArcMath.ArcToBezierSegments` (line 368). Every imported node is `Corner` — `Smooth` is never
inferred (rationale at 196–202). viewBox/units/nested transforms are handled
(`SvgImporter.cs:82–127`, recursive `Walk` at 117).

### 2.5 Geometry services

`Lasero.Core/Geometry/` — 5 files, Clipper2-backed (Boost-1.0). Both `IVectorBooleanService` and
`IVectorOffsetService` consume and produce plain closed-ring `IReadOnlyList<IReadOnlyList<Position>>`;
Clipper2's own `PathD`/`PathsD` types never escape the implementations. Both use `Precision = 6`
decimal places into Clipper2's Int64 space, shared deliberately so a boolean→offset chain can't
introduce a precision mismatch. `Clipper2VectorOffsetService` drops slivers below
`MinimumRingAreaSquareMm = 1e-6` post-inflate.

---

## 3. Current machine compatibility architecture

### 3.1 The layers that exist

```
ConnectionViewModel / JogViewModel / GCodeViewModel   (Lasero.App)
        ↓  ILaserMachine                              ← real seam, injected, load-bearing
GrblConnection                                        ← monolithic GRBL protocol state machine
        ↓  IGrblTransport                             ← real seam, proven by 3 implementations
GrblSerialTransport | VirtualGrblTransport | RoutingGrblTransport
        ↓
Physical machine / simulator
```

`GrblConnection` (`Lasero.Core/Grbl/GrblConnection.cs`, ~27 KB — the largest file in Core) owns:
- **Lifecycle** — `Connect()` (91–144) opens the transport, allocates a bounded
  `BlockingCollection<QueuedCommand>(256)` and a dedicated `Grbl-CommandQueue` thread (117–129).
- **Flow control** — strictly one command in flight: the queue thread writes one line then blocks on
  that command's `TaskCompletionSource` with an 8 s `CommandTimeout` (line 69, wait at 312) before
  dequeuing the next. No look-ahead into GRBL's 128-byte RX buffer.
- **Response correlation** — FIFO via `ConcurrentQueue<TaskCompletionSource<GrblCommandResult>>`
  (line 29) + `CompleteOldestPending` (534–544). Assumes in-order acks.
- **Generation fencing** — `_commandGeneration` (line 39) invalidates in-flight commands across
  reset/reconnect boundaries (captured at enqueue, line 208; rejected at 291–299). This is the
  defence against a response shifting onto the wrong command, and it is good engineering.
- **Status polling** — `System.Threading.Timer` writing the `?` realtime byte on an interval (175–186).
  Polled, not pushed.
- **Real-time channel** — `?`, `!`, `~`, `0x18` and override bytes bypass the queue entirely
  (`SafeWriteRealtime`, 441–453).

`GrblSerialTransport` uses a raw `BaseStream.Read` background thread with manual `\n` splitting rather
than `SerialPort.DataReceived`/`ReadLine`, with a documented CH340/CH341 coalescing-bug rationale
(`GrblSerialTransport.cs:6–17`). Do not "modernise" this.

### 3.2 The layers that do not exist

There is **no** machine-independent job model, **no** post-processor, and **no** controller-protocol
abstraction. G-code is generated as GRBL-syntax strings directly from the document and handed to the
transport verbatim:

```
ImportedDocument + LayerSettings
        ↓  ToolpathBuilder.BuildGCode(document, controllerMaximumS)   ← static, monolithic
List<string> of GRBL text
        ↓  GCodeJobRunner  →  ILaserMachine.SendCommandAsync(line)    ← verbatim
```

### 3.3 Machine identity today

Three disconnected representations, none of which is a profile in the sense you mean:

| Type | File | What it holds | Status |
|---|---|---|---|
| `MachineCompatibility` | `Machines/MachineCompatibility.cs:10` | `Id`, `DisplayName`, `Limitation` only | Live, UI-facing, compiled-in array of 6 |
| `KnownMachineProfile` | `Machines/KnownMachineProfiles.cs:19–33` | Work area, max feed, baud for 2 machines | **Dead** — `Match()` returns `null` unconditionally (line 39) |
| `GrblDeviceProfile` | `Grbl/GrblDeviceProfile.cs:5–14` | Live `$$` dump: travel limits, max spindle, laser mode | Live, but per-connection and discarded on every reconnect |

Nothing connects "the user picked `xtool-s1` in the dropdown" to "here is its settings", because
`RequireDirectConnection` (`MachineCompatibility.cs:33–37`) blocks connecting under any id except
`existing-grbl` in the first place — enforced in `GrblConnection.Connect` line 102, and locked in by
`MachineCompatibilitySafetyTests`.

---

## 4. What is already done well

Do not break these while refactoring.

1. **`ILaserMachine` is real and already injected everywhere.** `GCodeJobRunner(ILaserMachine)`
   (`GCodeJobRunner.cs:30`), `ConnectionViewModel(ILaserMachine, …)` (line 59),
   `ILaserMachineFactory`/`DeviceScanner` (`DeviceScanner.cs:9–11, 39`). This is the single most
   expensive seam to retrofit and it already exists.
   *(Corrects `ARCHITECTURE.md`, which states no such abstraction wraps `GrblConnection`.)*
2. **`IGrblTransport` is a proven seam.** `RoutingGrblTransport` already routes between serial and
   virtual transports by port-name sniffing (line 31). **A TCP/WiFi transport is additive work with
   zero changes to `GrblConnection`** — provided the wire protocol is still GRBL's line protocol.
3. **No machine-name conditionals in the UI.** Grep for `xTool|XTool|AlgoLaser` across all
   `Lasero.App/**/*.cs` returns zero logic hits (the only match is a design-inspiration comment in
   `MaterialsWindow.xaml:1`). Machine-name awareness is confined to
   `Machines/MachineCompatibility.cs`. This is the anti-pattern you were most worried about, and the
   codebase does not have it.
4. **Fail-closed safety posture, tested.** Unverified machines cannot open a transport *at the service
   layer*, not merely via a disabled button. Dimension-based identity inference was deliberately
   removed. `MachineCompatibilitySafetyTests` locks this in with a `[Theory]` over all 5 ids.
5. **Job completion is confirmed, not assumed.** `GCodeJobRunner.WaitForConfirmedIdleAsync` (191–256)
   polls `?` every 250 ms until a *fresh* `Idle` arrives rather than trusting the final `ok`, with an
   8 s status-silence watchdog. An unconditional `M5` epilogue is appended regardless of job content
   (118–129). Disconnect during a run forces abort + Faulted (63–70).
6. **`JobPreflight` is genuinely strong.** `Evaluate` (45–91) is a pure function over a snapshot,
   blocking on: not connected, empty/no-motion document, out-of-bounds (0.001 mm tolerance, 150–174),
   invalid per-layer power/speed/passes (200–217), unknown controller power range (62–64), `$32` laser
   mode unconfirmed (65–67), raw-G-code `$`-command injection (108–112), stale status >2 s (79–80),
   machine not Idle with state-specific messages (176–198), limit/door/probe pins (191–197).
7. **The vector undo/drag transaction design** (§2.3) — preview isolation via `VectorPathDragSession`
   plus a structural-equality no-op guard is exactly right.
8. **Custom hit-testing independent of WPF**, and **no SVG-string round-tripping anywhere** —
   rendering builds `PathGeometry`/`BezierSegment` programmatically from `Position`/`VectorNode`
   (`SceneCanvas.xaml.cs:626, 648`). The common architecture smell of serialising to a `d` string and
   reparsing is absent.
9. **Alerts are surfaced.** `AlertChanged`/`MachineAlert` (`GrblConnection.cs:86, 570–595`) is
   subscribed by `MachineStatusViewModel.cs:33`, `ConsoleViewModel.cs:40`, `JogViewModel.cs:47`,
   `RasterImportViewModel.cs:103` and bound in `MachinePanelView.xaml:52–57` / `DeviceView.xaml:73–78`.
   *(Corrects `ARCHITECTURE.md`'s "Known gap: nothing subscribes" — that is true only of the raw
   per-code `AlarmReceived`/`ErrorReceived` events, which are superseded by the alert path.)*
10. **Test discipline is real**: 74 test files, ~13.5 k lines, hardware-free testing genuinely works
    via `IGrblTransport` fakes and `VirtualGrblTransport`.

---

## 5. Current weaknesses

### 5.1 No unified fill / winding contract — *the* vector blocker

There is no single answer to "what is inside this shape", and at least four subsystems answer it
differently:

| Consumer | Rule used | Where |
|---|---|---|
| Boolean ops | EvenOdd within a `GeometrySetId` group, then Union across groups | `SceneViewModel.BuildSourceRings`, 1320–1339 |
| Offset | No winding normalisation at all | `VectorOffsetPlanner.ComputeOffset` |
| Toolpath fill | EvenOdd across **all of a layer's shapes combined** | `ToolpathBuilder.AppendFillLayer:116–119` |
| Canvas render | WPF `PathGeometry` default fill rule | `SceneCanvas.xaml.cs:626` |
| Hit-testing | Point-in-polygon per shape | `SceneHitTester.cs` |

The repo's own gap matrix already flags this (Phase E §41/§42, marked risk High) and correctly refuses
to build more boolean/offset features on top of it. **Concur, emphatically.** Every geometry feature
added before this is settled inherits and multiplies the inconsistency. Note especially that
`ToolpathBuilder`'s layer-wide EvenOdd means two *unrelated* overlapping shapes on the same layer will
have their overlap engraved as a hole — that is a physical-output correctness bug, not a cosmetic one.

### 5.2 Holes are inferred by heuristic, and the heuristic can misfire

`SceneViewModel.NormalizeNestedCompoundPaths` (1344–1388) reassigns a child ring's `GeometrySetId` to a
candidate parent's when the child is closed, opposite-signed in winding, smaller in area,
bounds-contained, and its first point tests inside the parent (`IsPointInsidePolygon`, 1359–1373).

This runs before every boolean operation, silently. Two independently drawn or imported shapes that
happen to satisfy the test — entirely plausible when importing from a tool with different winding
conventions — are silently treated as an outer+hole compound rather than two filled regions. The user
gets a surprising Union result with no indication that a reclassification occurred.

The root cause is that the data model has no hole concept to declare, so hole-ness must be
reconstructed geometrically every time.

### 5.3 Boolean and offset results are one-way trapdoors

Every Union/Subtract/Intersect/Exclude/Offset result is rebuilt as an all-`Corner` polygon
(`VectorOffsetPlanner.cs:34`), because Clipper2 is a polygon library. A curve-preserving SVG import
that is then unioned is permanently polygonised. Nothing warns the user before the operation. This may
be an accepted product limitation — but it should be an explicit decision, because "import logo →
combine two letters → adjust the curve" is a mainstream workflow.

### 5.4 The machine layer is one driver wearing an architecture's clothes

GRBL specificity is baked into `GrblConnection` itself, not the transport:

| What | Where | Why it blocks a second controller family |
|---|---|---|
| Line classification | `OnLineReceived`, 349–439 | Hardcoded `<…>` status (353), `Grbl ` banner (370), literal `"ok"` (389), `error:`/`ALARM:` (407, 417), `$…=` settings (427), `[…]` feedback (437). Marlin's `ok T:…` needs a parallel implementation. |
| Command vocabulary | 217–237 | `$J=`, `$H`, `$X`, `G10 L20 P{n}`, `G54..G59`, `$$` as bare string literals |
| Realtime bytes | `GrblRealtimeCommand.cs` | GRBL 1.1's single-byte control channel; Marlin has no equivalent |
| Error taxonomy | `GrblErrorCodes.cs` | Hardcoded GRBL 1.1 tables, called **statically** from `GrblCommandResult.cs:10–13`, bypassing even the partial `IGrblProtocolParser` seam |
| Settings parsing | `GrblDeviceProfile.cs:32–36` | `$30`/`$32`/`$130`/`$131`/`$132` by number |

`IGrblProtocolParser` abstracts only *status-line* parsing and error text lookup — not the line
classification dispatch, not the command builders. It is a partial seam, not a full one.

### 5.5 `MachineCapability` is a vocabulary with no data

`MachineCompatibility.cs:5` defines a genuinely good capability enum — `Connect, Identify, Status, Jog,
Home, Frame, Vector, Raster, Pause, Resume, Cancel, Focus, AirAssist, SourceSelection, Accessories`.
But `GetCapability` (13–14) **ignores its argument entirely** and always returns
`(Unresolved, Unverified)`:

```csharp
public CapabilityEvidence GetCapability(MachineCapability capability) => new(
    CapabilitySupport.Unresolved, CapabilityVerification.Unverified);
```

So nothing today distinguishes "this machine supports Frame" from "this machine supports AirAssist" —
and there are no `SupportsRotary`/`SupportsZAxis`/`SupportsNetworkConnection`/`SupportsMultipleHeads`
concepts anywhere in the codebase. The capability-driven UI you described cannot be written against
this, because every query returns the same answer.

### 5.6 Three independent G-code emitters, no post-processor

`ToolpathBuilder`, `FramingService`, and `GrblConnection`'s command builders each hardcode GRBL
motion/spindle words as string literals, with independently duplicated numeric formatters
(`ToolpathBuilder.Fmt`, line 151 vs `FramingService.Format`, 72–76). Supporting a PWM board that wants
`M106`/`M107` instead of `M3`/`M4`/`M5` means editing three files' string literals.

### 5.7 Missing formats

**No DXF importer and no SVG exporter exist** — verified by grep; there is not even a stub. The team
was disciplined about not advertising DXF (there is an enforcing comment at `HomeView.xaml:98`), but
for a LightBurn migrant these are both listed as 🔴 P0 in `LIGHTBURN_MIGRATION_EXPERIENCE.md` §2, and
the F2 workflow in `docs/machine-compatibility-matrix.md` explicitly depends on handing artwork to
xTool Studio — which currently cannot carry any LASERO edits, because there is no exporter.

---

## 6. Technical debt

| # | Item | File:line | Cost of leaving it |
|---|---|---|---|
| D1 | `VectorPath.cs` banner comment asserts SVG import "deliberately does NOT preserve incoming curve commands" | `Scene/VectorPath.cs:33–36` | Actively false since `cc0bead`. It is the first thing anyone opening the core geometry file reads, and it misleads about a central architectural boundary. |
| D2 | `ARCHITECTURE.md` states no `ILaserMachine` wrapper exists, no simulator exists, layers always run Fill-before-Cut, and alarms are unsurfaced | `ARCHITECTURE.md` §Machine Architecture, §Toolpath Pipeline, §Safety, §Machine Simulator | All four are now wrong (`ILaserMachine` is live; `VirtualGrblTransport` exists; `ToolpathBuilder:24` uses explicit operator order and has a `FillAndCut` mode; alerts are bound in two views). A stale architecture doc is worse than none. |
| D3 | `KnownMachineProfiles` is dead code holding misleading hardcoded dimensions (100×100, 400×410, 20000 mm/min) | `Machines/KnownMachineProfiles.cs:19–39` | Unreachable today, but a future maintainer could "fix" `Match()` and silently reinstate the dimension-based identity inference that was deliberately removed as unsafe. Mark `[Obsolete]` with the rationale inline, or delete. |
| D4 | Two parallel hardware-free test strategies: per-file hand-rolled `FakeTransport` (duplicated in ≥3 test files) and the shared `VirtualGrblTransport` | `GrblConnectionLifecycleTests.cs:249`, `GrblSessionBoundarySafetyTests.cs`, `JobCancellationSafetyTests.cs` | Divergent fakes drift from real transport semantics independently. |
| D5 | Default work area `500×400` repeated as literals in 4 places | `Lasero.App/AppSettingsStore.cs:73–74, 89–90, 176–177, 185–186` | Not machine-specific (it's the pre-identification fallback), but a 4-way duplicated constant. |
| D6 | Event marshalling burden pushed onto every subscriber | `GrblConnection.InvokeSafely:604–634` | Never marshals to the UI thread; all ~10 current subscribers correctly wrap in `RunOnUiThread`/`Dispatcher.Invoke`, but one forgotten wrap is a cross-thread WPF crash. Scales badly. |
| D7 | `DragMode` enum at 11 values with dispatch threaded through a 1 600-line partial class | `Controls/SceneCanvas.xaml.cs:86`, `SceneCanvas.VectorPathTool.cs:16–22` | Every new tool grows shared switch statements rather than being self-contained. |
| D8 | Working tree carries 170 uncommitted files on `design-system-tokens` | — | Any refactor below will be hard to review against this baseline. Land or shelve it first. |

---

## 7. Vector-editing gaps

### 7.1 Node/path operation inventory (verified against source)

| Operation | Status | Evidence |
|---|---|---|
| Move node (multi-select, Shift-constrain, Ctrl-bypass-snap) | ✅ | `SceneCanvas.VectorPathTool.cs:910–968` |
| Add handle while placing | ✅ | `VectorPathEditor.AppendNode:19–24`, `BuildNode:370–376` |
| Line → curve (explicit) | ✅ | `VectorPathEditor.ConvertSegmentToCurve:157–172`; Shift+C |
| Curve → line (explicit) | ✅ | `VectorPathEditor.ConvertSegmentToLine:177–188`; Shift+L |
| Insert node at point | ✅ | `VectorPathEditor.InsertNode:95–120` — exact De Casteljau split |
| Insert node at midpoint | ✅ | `InsertNodeAtHoveredSegmentMidpoint:1421–1432`, Shift+M |
| Delete node | ✅ | `VectorPathEditor.RemoveNode:66–78` (keeps adjacent handles; no curvature refit — documented at 60–65) |
| Delete segment | ✅ | `VectorPathEditor.DeleteSegment:288–315` |
| Break path at node | ✅ | `VectorPathEditor.BreakAtNode:247–278` |
| Close path (click, toggle, drag-to-join) | ✅ | `Close:30–46`; `TryCloseByEndpointJoin:1118–1141` |
| Open path | ✅ | `VectorPathEditor.Open:51–55` |
| Join paths across objects | ✅ | `JoinAtEndpoints:349–368`, `SceneViewModel.JoinObjectEndpoints:1217–1261` — **single-subpath objects only** (documented no-op for compound, 1204–1208) |
| Reverse direction | ✅ | `ReverseSubpath:322–330` |
| Corner ↔ Smooth | ✅ | `ConvertNodeType`/`MakeCollinear:125–129, 384–417` |
| Segment-body drag (line→curve on drag) | ✅ | Exact minimal-movement Bézier math per gap matrix Phase C |
| **Duplicate-node cleanup** | ❌ | No dedup pass anywhere. Dragging one node onto another is not prevented or merged. |
| **Self-intersection cleanup** | ❌ | Self-intersections are *detected only as snap candidates* (`SnapCandidateBuilder.AddIntersectionCandidates:71–93`) — a UI aid, not a repair operation. |
| **Tiny/degenerate segment cleanup** | ⚠️ partial | Only sliver rings post-offset (`Clipper2VectorOffsetService:20, 75–88`). No general cleanup for imported or hand-drawn paths. |
| **Trim / Extend / Align-segment** | ❌ | Gap matrix Phase D, deferred |
| **Break Apart (whole object)** | ❌ | Distinct from break-at-node; neither exists |
| **Auto-join by tolerance** | ❌ | Gap matrix §29 |
| **Convert parametric primitive → path** | ❌ | Rect/ellipse have `LocalShapes` only, no `VectorPath` — gap matrix Phase A §2 |

Read that table the right way: **the hard half is done.** What's missing is mostly cleanup/repair
operations and the two trickier interactive ones (trim/extend).

### 7.2 Snapping

`Scene/Snapping/SnapEngine.cs:12–19` implements all five kinds — `Node`, `Endpoint`, `Midpoint`,
`Intersection`, `Grid` — with priority Endpoint > Node > Intersection > Midpoint > Grid and a
`TieEpsilonSquared = 1e-9` anti-flicker deadband (54–62). Candidates are built once per drag gesture,
not per pointer-move (`SnapCandidateBuilder.cs:12–19`).

Two real gaps:
- **Intersection candidates are scoped to within one `VectorPath` object only** (comment 63–69) —
  cross-object intersection snapping would need a scene-wide spatial query this builder has no access to.
- **Snapping only exists in node-edit mode.** Whole-object transform drags (move/resize/rotate) do not
  snap to grid, object edges, centres, or other objects. `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B lists
  this as P0 and it is a genuine daily-use blocker.
- Intersection search silently skips above `MaxFlattenedPointsForIntersectionSearch = 2000` points
  (line 27) — correct degradation, but undiscoverable to the user.

### 7.3 Other vector gaps

- **DXF import**: absent entirely.
- **SVG export**: absent entirely.
- **Distribute / center-on-workspace**: absent (per migration doc §B).
- **Group resize/rotate on multi-select**: move-only per `ARCHITECTURE.md` §Selection — re-verify, as
  that doc is stale elsewhere.
- **Toolpath cut order**: raw per-layer document order, no travel optimisation
  (`ToolpathBuilder.AppendCutLayer:65–83`). Fill already has serpentine scanning (121–129); cut has no
  equivalent. Cycle-time cost only, not correctness.
- **No arc (G2/G3) emission** — everything is pre-flattened to `G1` at the fixed 0.05 mm tolerance.
  For CO2/galvo machines with fast arc interpolation this inflates file size and can limit feed rates.

---

## 8. Machine-compatibility gaps

| Gap | Current state | Impact |
|---|---|---|
| **No capability data** | `GetCapability` is a universal stub (`MachineCompatibility.cs:13–14`) | Capability-driven UI is impossible to write today |
| **No structured profile** | `MachineCompatibility` holds 3 strings; work area lives only in the live `$$` dump; `KnownMachineProfile` is dead | No origin, no power range, no speed limits, no connection types, no startup/shutdown commands, no dialect per machine |
| **Compiled-in catalog** | `MachineCompatibility.cs:20–28`, array of 6, linear `FirstOrDefault` by string id | Machine #7 requires a `Lasero.Core` code change, recompile and redeploy |
| **No inheritance** | None | "Generic GRBL → AlgoLaser GRBL → DIY MK2" cannot be expressed |
| **No network transport** | Serial only; `IGrblTransport` *could* take one | WiFi machines unreachable |
| **No controller-protocol seam** | §5.4 | FluidNC/GRBLHAL/Marlin each need a duplicated `GrblConnection` |
| **No post-processor** | §5.6 | Dialect changes touch 3+ files |
| **No job model** | `ToolpathBuilder` emits GRBL text directly | Nothing machine-independent exists to post-process |
| **Galvo unsupported architecturally** | mm/G-code motion assumptions in `ToolpathBuilder`, `GCodeParser`, `FramingService` | Galvo needs a fundamentally different output model — deeper than the controller question |
| **`GCodeParser` silently mis-previews foreign dialects** | Resolves only G0/G1/G2/G3/G90/G91/G20/G21/M3/M4/M5; explicitly ignores G4/G17-19/G54-59/G10/G28/G30 (doc comment 7–15) | A job using different codes previews wrong **with no warning** — a safety-adjacent issue |
| **`ILaserMachine` has one implementation** | Only `GrblConnection` | Its shape is unproven. Watch `GrblCommandResult`'s GRBL-numeric `ErrorCode`/`AlarmCode` (`ILaserMachine.cs:42–48`) — a second backend forced to fake GRBL codes is the signal that the result type must become protocol-neutral. |
| **No rotary / Z / autofocus / multi-head / camera / air-assist concepts** | `AirAssist` and `Focus` exist in the enum only; nothing else exists at all | Every one is greenfield |

---

## 9. Scalability risks

Ordered by how badly each bites as machine count grows.

1. **Compiled-in catalog** (`MachineCompatibility.cs:20–28`) — the single hardest blocker to "tens or
   hundreds of machines". Every addition is a release.
2. **Stub capability accessor** (`:13–14`) — as long as every capability answers "Unresolved", the UI
   has nothing to adapt to, so pressure builds to add machine-name conditionals in the UI instead.
   **This is how the anti-pattern you fear actually gets introduced.** The codebase is clean today
   precisely because nobody has needed to differentiate machines yet; the moment someone does, the
   absence of capability data makes the bad path the easy path. Fixing §5.5 is preventive.
3. **Monolithic protocol dispatch** (`GrblConnection.OnLineReceived:349–439`) — the second controller
   family will be added by copy-pasting `GrblConnection` unless a seam is extracted first. Once two
   near-duplicate 27 KB connection classes exist, every safety fix must be made twice.
4. **Triplicated G-code emission** (`ToolpathBuilder` / `FramingService` / `GrblConnection` builders) —
   N dialects × 3 call sites.
5. **`GCodeParser` scope mismatch** — preview divergence scales silently with dialect count and is
   safety-adjacent.
6. **`KnownMachineProfiles` dead code** — a trap for a future maintainer (D3).
7. **Subscriber-side event marshalling** (D6) — footgun that scales with consumer count.
8. **Fill-contract fragmentation (vector side)** — each new geometry consumer adds a fifth, sixth,
   seventh interpretation of "inside".
9. **`DragMode` enum + switch dispatch** (D7) — each new editor tool grows shared code.

---

## 10. Recommended target architecture

### 10.1 Vector

Keep the model. Fix the contract. Three changes, in order:

**(a) One topology-normalisation pass, shared by every consumer.** A single
`Lasero.Core/Geometry/PathTopology` service that takes raw subpaths and returns a normalised
`ResolvedRegion` — outer rings with explicit, declared holes, consistent winding (outers CCW, holes
CW), degenerate segments removed, self-intersections resolved. Canvas render, hit-test, boolean,
offset, and toolpath fill all consume `ResolvedRegion` and **none** of them re-decides fill rule.
This is the repo's own Phase E, and it must come before any further geometry feature.

**(b) Make holes declarable, not inferred.** Add an explicit role to the subpath level
(`VectorSubpathRole { Outer, Hole, Open }`) populated at import (SVG fill-rule + winding are known at
parse time), at trace time (`CompoundPathBuilder` already knows its nesting), and at boolean-result
time (Clipper2's `PolyTreeD` gives the hierarchy for free — the current code discards it by using the
flat `PathsD` output). Keep `NormalizeNestedCompoundPaths` only as a repair fallback for geometry that
arrives with no role information, and surface it in the UI when it changes the result.

**(c) Preserve curves through geometry operations where possible.** Offset of a pure-line path
should stay a line path; boolean results should optionally be refit with the existing `BezierFitter`
(already in `Lasero.Core/Trace/`, Schneider/Selinger) behind an explicit "smooth result" option.
At minimum, warn before an operation that will polygonise curves.

### 10.2 Machine

The pipeline you sketched is the right one. Mapped onto what exists:

```
SceneDocument / VectorPath                     ← EXISTS, good
        ↓
ToolpathGenerator → ToolpathLayer[]            ← EXISTS inside ToolpathBuilder, entangled with emission
        ↓
LaserJob (machine-independent)                 ← MISSING — the key new piece
        ↓
IPostProcessor → MachineProgram                ← MISSING
        ↓
IControllerProtocol                            ← MISSING (buried in GrblConnection)
        ↓
ILaserMachine                                  ← EXISTS, correct
        ↓
IGrblTransport (→ rename ITransport)           ← EXISTS, correct
        ↓
Physical machine
```

**Responsibility boundaries** — this is the part to be strict about:

| Layer | Owns | Must never contain |
|---|---|---|
| **Machine profile** (data) | Identity, working area, origin, speed/power limits, capability set, connection types, dialect id, startup/shutdown/homing/framing command templates | Any code. It is JSON. |
| **Toolpath generator** | Geometry → ordered motion+power intent in mm and % power. Kerf, ordering, fill strategy, passes | Any G-code word. Any controller name. Any machine name. |
| **Job model (`LaserJob`)** | Machine-independent IR: ordered operations, each with typed moves (`RapidTo`, `CutTo`, `ArcTo`), power as %, speed as mm/min, per-op metadata (air assist on/off, Z, pass index) | Controller syntax, S-values, M-codes |
| **Post processor** | `LaserJob` → concrete command text for one dialect. S-scaling, M3/M4/M5 vs M106/M107, arc emission or flattening, units preamble, profile-supplied startup/shutdown | Transport, connection state, machine identity beyond the profile it was handed |
| **Machine driver** (`ILaserMachine` impl) | Session lifecycle, queueing, flow control, generation fencing, status polling, jog/home/origin as *intent* | Dialect text (delegates to post-processor/protocol) |
| **Protocol** (`IControllerProtocol`) | Line classification, ack/error/alarm taxonomy, realtime byte set, command-string construction | Job content, geometry, UI |
| **Transport** | Bytes and lines. Open/close/write/read | Everything else |

Critically: **the capability check belongs in the profile, and the UI reads only the profile** — never
the driver, never the name.

### 10.3 Machine profile format

Recommend JSON, embedded as a resource for built-ins plus a user-writable overlay directory, with
single-parent inheritance. Inheritance **is** worth it here — "generic GRBL 1.1 laser" carries ~80 %
of every diode machine's definition, and without it each new entry re-states the same dialect,
realtime set and command templates, which is exactly how divergence bugs get in.

```jsonc
{
  "id": "algolaser-diy-kit-mk2-10w",
  "extends": "algolaser-grbl",           // → extends "generic-grbl-1.1"
  "manufacturer": "AlgoLaser",
  "model": "DIY KIT MK2 (10 W)",
  "workingArea": { "widthMm": 400, "heightMm": 435 },
  "origin": "FrontLeft",
  "limits": { "maxFeedMmPerMin": 12000, "maxPowerS": 1000 },
  "connections": [{ "type": "Serial", "baud": 115200, "dtr": false }],
  "capabilities": {
    "Frame":     { "support": "Supported",  "verification": "HardwareVerified" },
    "AirAssist": { "support": "Unsupported","verification": "OfflineTested"   }
    // anything unlisted defaults to Unresolved/Unverified — fail closed
  },
  "startupCommands": [], "shutdownCommands": ["M5"]
}
```

Two non-negotiables carried over from the existing safety work:
- **Unlisted capability ⇒ `Unresolved`/`Unverified` ⇒ treated as unsupported.** The schema default
  must preserve today's fail-closed posture so a new JSON entry can't silently become "trusted".
- **`RequireDirectConnection`'s service-layer gate stays.** A profile file must not be able to grant
  itself hardware access; verification status is a separate, code-owned assertion.

### 10.4 Post processors

`GenericGrblPostProcessor` first (behaviour-identical to today's output — that is what makes the
migration safe), then `GrblHalPostProcessor`, `FluidNCPostProcessor`, `MarlinPostProcessor` as thin
subclasses differing in a handful of overrides. `XToolPostProcessor` and `GalvoPostProcessor` are
genuinely different animals and should not be forced into the same base.

---

## 11. Suggested class / interface structure

```csharp
// ---------- Lasero.Core/Geometry ----------
public enum VectorSubpathRole { Outer, Hole, Open }

public sealed record ResolvedRegion(
    IReadOnlyList<Position> Outer,
    IReadOnlyList<IReadOnlyList<Position>> Holes);

public interface IPathTopologyService {
    // The ONE place fill/winding is decided. Every consumer calls this.
    IReadOnlyList<ResolvedRegion> Resolve(VectorPath path, double toleranceMm);
    VectorPath Cleanup(VectorPath path, PathCleanupOptions options); // dedupe, tiny-seg, self-intersect
}

// ---------- Lasero.Core/Jobs (machine-independent IR) ----------
public enum MoveKind { Rapid, Linear, ArcCw, ArcCcw }

public readonly record struct JobMove(
    MoveKind Kind, Position To, Position? ArcCentre,
    double PowerPercent, double FeedMmPerMin);

public sealed record JobOperation(
    string LayerName, LayerMode Mode, int PassIndex,
    bool AirAssist, double? ZMm,
    IReadOnlyList<JobMove> Moves);

public sealed record LaserJob(
    IReadOnlyList<JobOperation> Operations,
    BoundingBox2D Bounds,
    JobPlacement Placement);          // no controller syntax anywhere in this graph

// ---------- Lasero.Core/Machines (profile + capability) ----------
public sealed record MachineProfile(
    string Id, string? Extends, string Manufacturer, string Model,
    WorkingArea Area, MachineOrigin Origin, MachineLimits Limits,
    IReadOnlyList<ConnectionDescriptor> Connections,
    string DialectId,
    IReadOnlyDictionary<MachineCapability, CapabilityEvidence> Capabilities,
    IReadOnlyList<string> StartupCommands, IReadOnlyList<string> ShutdownCommands)
{
    // Replaces today's argument-ignoring stub. Unlisted ⇒ fail closed.
    public CapabilityEvidence GetCapability(MachineCapability c) =>
        Capabilities.TryGetValue(c, out var e) ? e
            : new(CapabilitySupport.Unresolved, CapabilityVerification.Unverified);

    public bool Supports(MachineCapability c) =>
        GetCapability(c).Support == CapabilitySupport.Supported;
}

public interface IMachineProfileRepository {          // JSON-backed, inheritance-resolving
    IReadOnlyList<MachineProfile> All { get; }
    MachineProfile Get(string id);
}

// ---------- Lasero.Core/PostProcessing ----------
public interface IPostProcessor {
    string DialectId { get; }
    MachineProgram Emit(LaserJob job, MachineProfile profile);
}
public sealed record MachineProgram(IReadOnlyList<string> Lines, MachineProgramStats Stats);

// ---------- Lasero.Core/Protocols ----------
public interface IControllerProtocol {
    ReceivedLine Classify(string line);                       // Status/Ok/Error/Alarm/Banner/Setting/Feedback
    byte[] Realtime(RealtimeCommand command);
    string BuildJog(JogRequest r);
    string BuildHome();  string BuildUnlock();
    string BuildSetWorkOrigin(int wcsIndex);
    string DescribeError(int code);  string DescribeAlarm(int code);
}

// ---------- Lasero.Core/Transport ----------
public interface ITransport { /* today's IGrblTransport, renamed */ }
public sealed class TcpTransport : ITransport { }             // new, additive
```

`GrblConnection` then becomes `ControllerMachine : ILaserMachine`, keeping its
queueing/generation-fencing/status-polling logic verbatim and delegating every string and byte to an
injected `IControllerProtocol`. **That preserved logic is the valuable part and must be moved, not
rewritten.**

---

## 12. Migration strategy

No rewrite. Eight slices, each independently shippable, each verifiable with `dotnet test`.

| # | Slice | Touches | Risk |
|---|---|---|---|
| M1 | Land or shelve the 170-file dirty tree; correct `ARCHITECTURE.md` (D2) and the `VectorPath.cs` banner (D1); `[Obsolete]` on `KnownMachineProfiles` (D3) | docs + 1 attribute | **Very low** |
| M2 | Introduce `IPathTopologyService` and route **one** consumer through it (boolean ops — it already has the most correct rule). Leave others untouched. | `Geometry/`, `SceneViewModel.BuildSourceRings` | Medium — covered by existing `Clipper2VectorBooleanServiceTests` |
| M3 | Route the remaining consumers (offset → toolpath fill → hit-test → render) one per commit, each with a before/after geometry regression test | one consumer per commit | Medium per step, low cumulative because each is isolated |
| M4 | Add `VectorSubpathRole`; populate at SVG import and from Clipper2 `PolyTreeD`; demote `NormalizeNestedCompoundPaths` to fallback | `VectorPath`, `SvgImporter`, `Clipper2*Service` | Medium |
| M5 | Extract `LaserJob` IR: `ToolpathBuilder` splits into `ToolpathGenerator` (→ `LaserJob`) + `GenericGrblPostProcessor` (→ lines). **Byte-identical output required.** | `Import/ToolpathBuilder.cs`, `Jobs/` | **Low if gated by golden-file tests written first** — see §13 |
| M6 | Fold `FramingService` onto the same post-processor so the duplicated emitter disappears | `Jobs/FramingService.cs` | Low |
| M7 | JSON `MachineProfile` + `IMachineProfileRepository` with inheritance; `MachineCompatibility` becomes a thin view over it; real capability data for the generic GRBL profile only; UI switches from names to `profile.Supports(...)` | `Machines/`, `ConnectionViewModel`, machine panel views | Medium — the fail-closed gate must be preserved and re-tested |
| M8 | Extract `IControllerProtocol` from `GrblConnection`; `GrblProtocol` as the only implementation initially. Rename `IGrblTransport` → `ITransport`, add `TcpTransport`. | `Grbl/` | **Highest** — this is the safety-critical file. Do it last, on a green suite, with the full `GrblConnectionLifecycleTests`/`GrblSessionBoundarySafetyTests` suite as the gate. |

Only after M8 does adding FluidNC/GRBLHAL/Marlin become a new class rather than a fork.

**Sequencing rule**: M5 before M7. The job model is what makes profiles meaningful; profiles without a
post-processor to consume them are just metadata.

---

## 13. Test strategy

Current state is better than average: 74 test files, real hardware-free transports, a node-edit
performance regression test (`NodeEditPerformanceTests.cs`), and curve-fidelity import tests
(`SvgVectorPathImportTests.cs`, 460 lines). Build on it.

**Vector**
- **SVG fixture corpus** — a directory of real-world `.svg` files (nested transforms, viewBox units,
  fill-rule variants, compound paths with holes, text-as-paths, stroke-only, degenerate geometry),
  each with an expected bounds/subpath-count/node-count assertion. This is `LIGHTBURN_MIGRATION_EXPERIENCE.md`
  §B item 2 and is the highest-value missing vector test asset.
- **Round-trip precision regression** — import → transform → boolean → offset → flatten, assert bounds
  drift under a stated epsilon. Pin the epsilon; a tightening or loosening should be a deliberate diff.
- **Topology contract tests** (new, gating M2–M4) — the same input through every consumer must yield
  the same inside/outside answer. This is the test that makes the fill-contract work verifiable rather
  than hopeful.
- **Hole semantics** — extend the existing nested-vs-overlapping test to cover the
  `NormalizeNestedCompoundPaths` misfire case in §5.2 explicitly.
- Strengthen `VectorPathHitTesterTests.cs` (60 lines is thin for the curve search math).
- Add an undo/redo z-order stress test: ≥3 interleaved structural commands over overlapping objects,
  assert deterministic redo. No such test exists.

**Toolpath**
- Cut-layer G-code currently has no dedicated test file (`FillToolpathTests.cs` covers fill only) —
  add one.
- Open vs closed path emission, kerf (once implemented), pass ordering, `FillAndCut` ordering
  (engrave-before-cut is a *safety* property — `ToolpathBuilder.cs:45` — and deserves an explicit test).

**Post processor**
- **Golden-file tests are the gate for M5.** Capture today's `ToolpathBuilder` output for ~6 canonical
  documents *before* refactoring; require byte-identical output after. This converts the riskiest
  refactor into a mechanical one.
- Then one golden file per dialect per canonical document.

**Machine**
- Consolidate the duplicated per-file `FakeTransport`s into one shared test double (D4).
- Connection state-transition matrix; disconnect mid-job; reconnect; timeout; generation-fencing
  across reset (partly covered by `GrblSessionBoundarySafetyTests`).
- Profile-loading tests: inheritance resolution, unknown capability defaults to Unresolved, malformed
  JSON rejected, **a profile file cannot grant itself direct-connection rights**.

**Standing rule**: every machine bug gets a regression test using the fake transport, named after the
symptom. This is already the de-facto practice — make it explicit in `docs/engineering/TESTING_STANDARDS.md`.

---

## 14. Priority roadmap

### P0 — architectural blockers / correctness

| | Item | Why P0 | Risk of change |
|---|---|---|---|
| P0.1 | **Unified fill/winding contract** (M2+M3, §5.1) | Physical-output correctness. Layer-wide EvenOdd in `ToolpathBuilder:116–119` can engrave an unintended hole where two unrelated shapes overlap. Every geometry feature added first inherits the bug. | Medium — mitigated by one-consumer-per-commit |
| P0.2 | **Golden-file G-code tests** (§13) | Prerequisite that makes every later machine refactor safe. Cheap. | Very low |
| P0.3 | **Doc corrections** (D1, D2, D3) | Four false statements in `ARCHITECTURE.md` and a false banner on the core geometry file are actively misleading contributors. | Very low |
| P0.4 | **Explicit hole roles** (M4, §5.2) | Silent geometric reclassification produces wrong boolean results with no user signal. | Medium |
| P0.5 | **`GCodeParser` dialect-scope warning** (§8) | Preview silently diverges from execution for unrecognised codes. Safety-adjacent; a warning is cheap even before a real fix. | Low |

### P1 — required for broader machine compatibility

| | Item | Notes |
|---|---|---|
| P1.1 | **`LaserJob` IR + `GenericGrblPostProcessor`** (M5) | The keystone. Gated by P0.2. |
| P1.2 | **Fold `FramingService` onto the post-processor** (M6) | Removes the second emitter |
| P1.3 | **JSON `MachineProfile` + repository + inheritance** (M7) | Machines become data, not releases |
| P1.4 | **Real capability data + capability-driven UI** (§5.5) | Replace the stub; switch UI from names to `profile.Supports(...)`. **Do this before anyone needs a machine conditional**, not after |
| P1.5 | **`IControllerProtocol` extraction** (M8) | Highest risk; last; green suite required |
| P1.6 | **`TcpTransport`** | Additive once M8 lands; unlocks WiFi machines |
| P1.7 | **DXF import** | 🔴 P0 in the migration doc; architecturally independent of everything above, so it can run in parallel |
| P1.8 | **SVG export** | Same. Also unblocks the documented xTool F2 artwork-handoff workflow, which currently cannot carry any LASERO edits |

### P2 — vector workflow

| | Item |
|---|---|
| P2.1 | **Object-level snapping** for whole-object transforms (grid/edge/centre/object) — currently node-edit only |
| P2.2 | **Path cleanup command**: duplicate-node merge, tiny-segment removal, self-intersection detection (§7.1) |
| P2.3 | **Curve preservation through boolean/offset**, or an explicit pre-operation warning (§5.3) |
| P2.4 | **Distribute + center-on-workspace** |
| P2.5 | **Convert parametric primitive → editable path** |
| P2.6 | **Cut-order travel optimisation** in `AppendCutLayer` — isolated, touches no geometry/undo/import |
| P2.7 | **Break Apart**, auto-join by tolerance, close-with-tolerance |
| P2.8 | Consolidate the duplicated `FakeTransport`s (D4); central event marshalling (D6) |

### P3 — advanced / polish

| | Item |
|---|---|
| P3.1 | Trim / Extend / Align-segment (§7.1) |
| P3.2 | Arc (G2/G3) emission in the post-processor — needs the `LaserJob` `ArcTo` move kind from P1.1 |
| P3.3 | Kerf compensation (`IVectorOffsetService` already provides the primitive) |
| P3.4 | Rotary, Z-axis, autofocus, multi-head, camera — each gated on P1.4's capability system |
| P3.5 | Galvo post-processor (a genuinely separate output model) |
| P3.6 | `IEditorTool` abstraction replacing `DragMode` (D7) — only once a new tool actually needs it |
| P3.7 | Adaptive/scale-aware flattening tolerance (currently fixed 0.05 mm) |

---

## Appendix — corrections to existing repo documents

| Document | Claim | Reality |
|---|---|---|
| `ARCHITECTURE.md` §Graphics Editor | "`SvgPathParser.Flatten` eagerly samples every curve… no control-point data surviving" | Stale. `SvgPathParser.ParseToVectorSubpaths` (210–395) preserves curves; `SvgImporter.cs:156–211` wires it under an all-or-nothing contract |
| `ARCHITECTURE.md` §Machine Architecture | "No `ILaserMachine`/`IGrblConnection` interface currently wraps `GrblConnection`" | False. `GrblConnection : ILaserMachine, IGrblDeviceProfileSource` (line 20), injected as `ILaserMachine` in ≥3 places |
| `ARCHITECTURE.md` §Machine Simulator | "Not implemented. No `SimulatedLaserMachine`/`ILaserMachine` exists" | False. `VirtualGrblTransport` provides hardware-free operation (also flagged in `docs/machine-compatibility-audit.md`) |
| `ARCHITECTURE.md` §Toolpath Pipeline | "`ToolpathBuilder` always runs all Fill layers before all Cut layers" | False. Explicit operator-controlled order (`ToolpathBuilder.cs:21–24`) plus a `LayerMode.FillAndCut` mode (44–50) |
| `ARCHITECTURE.md` §Safety | "nothing in `Lasero.App` subscribes to `AlarmReceived`/`ErrorReceived` — alarms not surfaced" | Half-stale. True of the raw events; the `AlertChanged`/`MachineAlert` path *is* subscribed in 4 view models and bound in 2 views |
| `ARCHITECTURE.md` §Layers | `LayerSettings` has "`Cut` or `Fill` only" | Stale — `LayerMode.FillAndCut` exists |
| `Lasero.Core/Scene/VectorPath.cs:33–36` | "This pass deliberately does NOT change `SvgImporter` to preserve incoming curve commands" | Contradicted by commit `cc0bead` |
| `docs/machine-compatibility-matrix.md` | 5 named machines, fail-closed, no dimension-based identity inference | **Confirmed accurate** against current code |
