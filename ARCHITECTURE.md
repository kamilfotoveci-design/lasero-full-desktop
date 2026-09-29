# Lasero Desktop — Architecture Reference

Detailed, verified description of how this repository is actually built. Read the section you need;
do not read the whole file for a small task. `CLAUDE.md` holds the always-on project rules and points
here. Every claim below was verified against the code; where something is aspirational rather than
built, it says so explicitly.
Live product framing lives in `PRODUCT.md`. Live visual/design tokens live in `DESIGN.md`. Live "what's done / what's next" lives in `README.md`'s "Aktuální stav" / "Zbývá pro další fázi" sections. **This file does not restate a status snapshot or a phase roadmap** — the previous version's biggest failure was a hardcoded phase list that silently went stale. Don't reintroduce one here; point at README.md instead.

---

## Product

A production-quality native Windows desktop application for designing, preparing, previewing, and sending jobs to GRBL laser engraving/cutting machines. Inspired by the workflow of professional laser software such as LightBurn, but with our own UI, architecture, terminology, and implementation.

Do not copy proprietary source code, assets, icons, branding, or exact UI layouts from other commercial applications.

Three subsystems, kept cleanly separated:
1. Graphics Editor (`Lasero.Core/Scene`, `Lasero.Core/Import`, `Lasero.App/Controls`)
2. Job / Toolpath Engine (`Lasero.Core/GCode`, `Lasero.Core/Import/ToolpathBuilder.cs`, `Lasero.Core/Jobs`)
3. Machine Control (`Lasero.Core/Grbl`)

---

## Technology (actual)

- C# / .NET 8
- **WPF** (`net8.0-windows`, `<UseWPF>true</UseWPF>`) — not WinUI 3, not Win2D. The canvas is rendered with plain `System.Windows.Shapes.Path` / `PathGeometry` on a `Canvas`.
- CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) throughout
- Microsoft.Extensions.Hosting + Microsoft.Extensions.DependencyInjection for the app host/container
- Serilog (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`) for logging
- `System.IO.Ports` for GRBL serial communication
- `System.Text.Json` + `System.IO.Compression.ZipArchive` for project persistence
- xunit for tests

Do not introduce Win2D, WinUI 3, or the Windows App SDK — the previous CLAUDE.md's technology section was wrong and should not be resurrected.

---

## Solution Architecture (actual)

Three projects, not the eight `LaserApp.*` modules the previous version proposed:

- **`Lasero.Core`** (`net8.0`, no UI framework, no ProjectReferences) — the entire domain layer lives here as namespaces, not separate assemblies:
  - `Scene/` — document/editor model (`SceneDocument`, `SceneObject`, `SceneObjectFactory`, `ObjectTransform`) + `Scene/Commands/` (undo/redo: `ISceneCommand`, `SceneCommandStack`, `AddObjectCommand`, `DeleteObjectsCommand`, `DuplicateObjectsCommand`, `ReorderObjectCommand`, `TransformObjectCommand`, `CompositeSceneCommand`)
  - `Import/` — `ImportedDocument`, `ImportedShape`, `SvgImporter` (+ `Import/Svg/` hand-rolled path parser), `RasterImporter`, `ToolpathBuilder`
  - `GCode/` — `GCodeDocument`, `GCodeParser`, `GCodeSegment`, `ArcMath`, `BoundingBox2D`
  - `Grbl/` — the machine-communication layer (see below)
  - `Jobs/` — `GCodeJobRunner`, `JobPreflight`, `FramingService`
  - `Layers/` — `LayerSettings`, `LayerMode`, `RgbColor`
  - `History/` — `JobHistoryEntry`, `JobHistorySummary`, `RecentProjectEntry`
  - `LaseroApi/` — cloud account/auth client (`LaseroAuthClient`, `LaseroAccountClient`, `SessionStore`)
- **`Lasero.App`** (`net8.0-windows`, WinExe, WPF, → references `Lasero.Core`) — `ViewModels/`, `Controls/` (`SceneCanvas`, `WorkspaceCanvas`), `Components/`, `Converters/`, `Theme/`, `Thumbnails/`, plus root-level windows (`MainWindow`, `LoginWindow`, `OnboardingWindow`, `SettingsWindow`) and app-level stores (`AppSettingsStore`, `ProjectFile`, `ProjectRecoveryStore`, `RecentProjectsStore`)
- **`Lasero.Tests`** (`net8.0-windows`, xunit → references both) — one test class per concept, no shared fixture file; see Testing section.

Dependencies flow one way: `Lasero.App → Lasero.Core`. UI code does not touch serial ports or generate G-code directly — confirmed no `SerialPort`/`System.IO.Ports` usage anywhere in `Lasero.App`.

If you're tempted to split `Lasero.Core` into the previous version's `Geometry`/`Toolpaths`/`Machines`/`Protocols`/`Persistence` projects: don't, unless the user explicitly asks for that restructuring. The namespace separation inside one project is intentional and working; splitting assemblies is a real, disruptive migration, not a documentation fix.

---

## Main Application Layout

Single `MainWindow`, not multi-page navigation. An `AppScreen` enum (`Home` / `Designer`) plus visibility-converter bindings picks which top-level panel shows — not separate `Frame`/page navigation. Modal `Window`s exist for `LoginWindow` (shown before `MainWindow` if signed out), `OnboardingWindow` (first run), and `SettingsWindow`.

Within the Designer screen, a `RadioButton`-driven toggle switches between `SceneCanvas` (design) and `WorkspaceCanvas` (read-only toolpath preview).

Layout matches the original intent: top menu bar + command toolbar, left tool rail, center canvas (majority of space), right inspector/layers/machine panels, bottom status/coordinates/zoom strip.

For color tokens, typography, and spacing: see `DESIGN.md` — do not hardcode new colors here or duplicate that doc.

---

## Graphics Editor

Model/rendering separation exists but is partial: geometry math (`ObjectTransform`, `BoundingBox2D`, shape flattening) lives in `Lasero.Core` and never references WPF types — keep it that way. `SceneCanvas.xaml.cs` (WPF-specific) builds `PathGeometry` from `SceneObject.GetWorldShapes()` for rendering and does WPF-native hit-testing (transparent-fill `Path` elements tagged with the owning `SceneObject`) — there is no separate hit-testing/selection service class.

**No tool-switching architecture exists.** There is no `IEditorTool`/`ToolManager`/`EditorController`, and no drawing tools (line/rect/ellipse/polygon/bezier/text) are implemented — only select/move/resize/rotate/marquee-select/pan/zoom. Interaction state lives in `SceneCanvas`'s own code-behind as a private `DragMode` enum (`None/Select/Move/Resize/Rotate/Pan`), decided per-event by which visual element raised it (object path → move, resize handle → resize, rotate handle → rotate, empty canvas → marquee-select, middle-mouse → pan). This is a real, confirmed gap versus a LightBurn-class editor, and the left tool rail has multiple `IsEnabled="False"` placeholder buttons for it. If/when drawing tools get built, introduce the tool-manager layer properly rather than keep growing the `DragMode` switch — but don't build it speculatively before there's a first real tool to route.

Editable-path (bezier node) work: an `EditablePath`/`PathNode` model preserving real curve control points end-to-end from SVG import (currently `SvgPathParser.Flatten` eagerly samples every curve into fixed points with no control-point data surviving) is planned/in-progress — check for `Lasero.Core/Import/EditablePath.cs` before assuming it exists.

---

## Coordinate System

`Position` (X/Y/Z, `Lasero.Core.Grbl` namespace) is the shared point type across Scene/Import/GCode/Grbl. Document units are millimeters, double precision. `ObjectTransform` (readonly record struct: X/Y/RotationDeg/ScaleX/ScaleY) maps local shape space to world (document/bed) space around each object's own pivot. Screen-space conversion (viewport pan/zoom) lives in `SceneCanvas`'s code-behind, not in Core. Machine coordinates (MPos/WPos) are a third space, handled entirely in `Grbl/` (`MachineStatus`, `GrblStatusParser`) — never conflated with document mm coordinates.

---

## Document Model

`SceneDocument` holds `ObservableCollection<SceneObject> Objects` and `ObservableCollection<LayerSettings> Layers` (global, per-color — not per-object). Every `SceneObject` has: `Id`, `LocalShapes` (flattened `ImportedShape` list), `LocalPivot`, `LocalBounds`, `Transform`, `Name`, `IsVisible`, `IsLocked`, plus optional `RasterFilePath`/`RasterOptions` for raster imports. `SceneDocument.ToImportedDocument()` flattens every visible object's transformed geometry into one `ImportedDocument` — the bridge to `ToolpathBuilder`.

**Invariant: `LocalPivot` is the centre of `LocalBounds`** (`SceneObject.IsPivotAtBoundsCenter`). Selection grips (`ObjectTransform.HandleLocalPoint`), resize (`ComputeResize`), rotate, flip and the inspector size all act about the pivot and assume it is the middle of the box; off-centre, the edge grips and rotate grip land on the pivot's own X/Y lines and rotate/flip orbit that point. Anything that builds or rebuilds an object must keep it centred. `VectorPathSceneFactory` does (it moves only the pivot and compensates `Transform` with `ObjectTransform.WithPivotMoved`, so nothing shifts on the canvas), and `SceneViewModel.LoadProject` heals older files via `SceneObject.WithPivotAtBoundsCenter()`.

There is no polymorphic `GraphicObject`/`VectorObject`/`LineObject`/... hierarchy as the previous version proposed — `SceneObject` is a single concrete type whose `LocalShapes` can represent any imported/flattened geometry. Don't introduce that hierarchy without a concrete need; it doesn't match how the importers or the canvas currently work.

---

## Geometry

No dedicated `Geometry` project/namespace with `PointD`/`SizeD`/`RectD`/`Matrix2D`/`BezierSegment` primitives exists. What exists instead: `Position` (Grbl namespace), `BoundingBox2D` (GCode namespace), `ObjectTransform` (Scene namespace, does the affine-transform job `Matrix2D` would). This is scattered across namespaces more than ideal, but working and tested (`ObjectTransformTests.cs`) — not a priority to consolidate unless it's actively blocking something.

Path flattening: `SvgPathParser` (internal to `Import/Svg`) samples cubic/quadratic beziers at a fixed 16 steps per segment; elliptical arcs via `SvgArcMath` (SVG spec appendix F.6.5 endpoint-to-center conversion). Offsets, boolean operations, and general path simplification are not implemented.

---

## Selection System

Lives in `SceneViewModel`'s `SelectedObjects` collection plus `SceneCanvas`'s `DragMode` state (see Graphics Editor). Click, marquee-select, and multi-select-move are implemented; **group resize/rotate for multi-select is not** (move-only for multiple objects — confirmed by an explicit code comment in `SceneCanvas.xaml.cs`). Selected objects render a bounding box, 8 resize handles, and a rotate handle, sized to stay usable on screen regardless of zoom.

---

## Commands and Undo

This is the one area that already matches the original vision closely — keep this pattern for any new undoable edit. `ISceneCommand` (`Do()`/`Undo()`) + `SceneCommandStack` (undo/redo stacks, `Changed` event). Existing commands: `TransformObjectCommand`, `AddObjectCommand`, `DeleteObjectsCommand`, `DuplicateObjectsCommand`, `ReorderObjectCommand`, `CompositeSceneCommand` (for multi-object operations). The pattern is always: capture immutable before/after snapshots of one `[ObservableProperty]`-backed property at construction time, and `Do`/`Undo` just reassign that property wholesale — no diffing, no in-place mutation. Follow this exact shape for any future command (e.g. a node-edit command).

---

## Layers / Operations

`LayerSettings` (`Lasero.Core/Layers/LayerSettings.cs`) currently has exactly 8 properties: `Color` (identity/match key), `Name`, `Mode` (`Cut` or `Fill` only — no `Line`/`Image`/`OffsetFill`), `Speed`, `Power`, `Passes`, `FillLineIntervalMm`, `IsEnabled`. There is **no `AirAssist`, `MinPower`, `Priority`, or separate `OutputEnabled`** yet — if a task needs one of these, add it to `LayerSettings` rather than inventing a parallel settings object. Layer execution order is not a stored property; `ToolpathBuilder` always runs all Fill layers before all Cut layers.

---

## Toolpath Pipeline

Entry point: `ToolpathBuilder.BuildGCode(ImportedDocument)`. Actual pipeline: filter enabled layers → reorder Fill-before-Cut → match shapes to layers by color (`IsApproximately`) → per layer, per pass: Cut layers emit rapids + `M4 S<power>` + straight `G1` per point (no `G2`/`G3` arc emission — shapes are pre-flattened to polylines before this stage); Fill layers do even-odd scanline fill across all of a layer's edges combined.

Known gaps, confirmed by code inspection — don't assume these exist: **no travel-move/path-order optimization** (shapes cut in raw document order), **no arc output** (arc math exists only on the G-code *reading*/preview side, `GCodeParser`). Raster engraving is a fully separate pipeline (`RasterImporter.BuildGCode`, direct pixel-darkness → power scan), invoked and concatenated after the vector toolpath, not integrated into `ToolpathBuilder`.

---

## Job Model / Preview

`GCodeViewModel` owns the loaded/generated `GCodeDocument`, run state, and progress. Preview (`WorkspaceCanvas`) reads the same job data that gets sent to the machine. Framing (`FramingService.BuildFrameGCode`, laser off by default) lets the operator verify placement before a real job, and can be made mandatory via `AppSettingsStore.Current.Safety.RequireFramingBeforeStart`.

---

## Raster Processing

`RasterImporter` is two separate entry points: `GetPlacedSizeMm()` (placement-only, reads pixel dimensions, no scanning — used at import time for the canvas outline) and `BuildGCode(filePath, options)` (full pixel scan, grayscale/threshold → power, run-length row scan — called at export/regenerate time, not import time, directly from the source file on disk). The original image is never modified; processing parameters (`RasterOptions`) are stored, output is regenerated on demand.

---

## Machine Architecture

`GrblConnection` (`Lasero.Core/Grbl/GrblConnection.cs`) owns one reconnectable session, built on an `IGrblTransport` seam (production impl: `GrblSerialTransport`, wrapping `System.IO.Ports.SerialPort`). No `ILaserMachine`/`IGrblConnection` interface currently wraps `GrblConnection` itself — it's injected as a concrete singleton. If a task calls for supporting a second controller family or for interface-based testability at the connection level (not just the transport level), introduce that abstraction deliberately as its own slice — don't assume it already exists.

State model is intentionally **split in two**, not one unified enum:
- `GrblConnectionState` (`Disconnected/Connecting/Connected`) — the link state, owned by `GrblConnection`.
- `GrblMachineMode` (`Unknown/Idle/Run/Hold/Jog/Alarm/Door/Check/Home/Sleep`) — the controller's reported state, parsed from live status reports.

There is no single combined "Disconnected/Idle/Run/.../Error" state anywhere yet — if the UI needs one display-level state, derive it from the two rather than merging the underlying enums (they model genuinely different things: can-we-talk-to-it vs. what-is-it-doing).

Async model is deliberately **not** `SerialPort.DataReceived`/naive `async`: `GrblSerialTransport` runs its own blocking-read background thread (comment explains CH340/CH341 chipset coalescing bugs with the built-in event), and `GrblConnection` uses a dedicated command-queue worker thread that blocks on one in-flight command's completion before sending the next — correct GRBL flow control (no send-ahead buffering). The *public* API is fully `Task`-based (`SendCommandAsync`, `JogAsync`, `HomeAsync`, etc.) and never blocks the calling/UI thread. Don't "fix" this into `SerialPort.DataReceived` — that's the bug it was built to avoid.

`$$`-settings reading (`QuerySettingsAsync`, `GrblDeviceProfileParser`) is fully wired to the UI already — work-area width/height from `$130`/`$131` flow into `AppSettingsStore` and gate `JobPreflight.ValidateBounds`. Not a stub.

---

## Machine Connection

GRBL only (no other controller family), via serial port (`System.IO.Ports`) — no TCP transport exists. Connection/jog/home/unlock/reset operations are asynchronous per above.

---

## Safety

Real, working gates — not decorative:
- `JobPreflight.Evaluate` blocks job start on: not connected, empty job, job exceeding work-area bounds, stale (>2s) or missing machine status, machine not `Idle`, triggered limit/door pins, and (if configured) framing-not-yet-run.
- An unconditional confirmation dialog (`GCodeViewModel.RunJob`) requires explicit user Yes before streaming starts, after preflight passes.
- `App.xaml.cs`'s global unhandled-exception handlers attempt a `FeedHold()` before showing an error and shutting down.
- `GrblErrorCodes` gives full human-readable (Slovak) text for GRBL error/alarm codes — never show a bare `error:9` to the operator.
- Pause/Resume/Abort (`GCodeJobRunner`) use an async `TaskCompletionSource` gate checked between streamed lines — no polling, no UI-thread blocking.

**Known gap**: `GrblConnection.AlarmReceived`/`ErrorReceived` events fire correctly with human-readable text, but nothing in `Lasero.App` currently subscribes to either — alarms/errors are not suppressed at the connection layer, but they are not surfaced anywhere the operator would see them (no banner, no status indicator, no dialog). Treat this as a real, current safety-UX gap, not something already handled by the console.

---

## Protocols

`Grbl/` doubles as the "protocol" layer — `GrblStatusParser` (stateless status-report parsing), `GrblErrorCodes` (error/alarm text lookup), `GrblDeviceProfileParser` (`$$`-settings → typed profile), `GrblRealtimeCommand` (single-byte realtime command values). No separate `Protocols/Grbl/` project; it's namespace-level separation inside `Lasero.Core`, which is sufficient — don't extract a new project for this alone.

---

## Machine Console

`ConsoleViewModel` shows raw send/receive lines (`> `/`< ` prefixed) and accepts manual command input, gated on connection state. **Known gap**: lines are not timestamped despite the product intent that console traffic should be — `Append()` currently just prepends the direction marker, nothing else.

---

## Job Transmission

`GCodeJobRunner`: explicit `Pause()`/`Resume()`/`Abort()`, real-time `FeedHold`/`CycleStartResume` under the hood, async gate between streamed lines (not blind send-ahead). `GCodeViewModel` wires this to UI commands, marshaling state/progress back via `Application.Current.Dispatcher.Invoke`.

---

## Persistence

`.lasero` = a ZIP archive containing `project.json` (System.Text.Json) plus embedded raster-asset entries (`assets/raster-NNNN.ext`), extracted to a per-project cache dir on load. `ProjectFileSerializer.Load()` also tolerantly accepts a plain (non-zip) JSON file for backward compatibility. Saves are atomic: write to a same-directory temp file with `WriteThrough`, flush, then `File.Move(overwrite: true)`.

Schema (`LaseroProjectFile`): `Version` (currently always 2), `Name`, `Objects` (shapes/pivot/bounds/transform/visibility/lock/raster refs), `Layers`. **`Version` exists but nothing branches on it — there is no migration logic implemented.** The schema also has **no document-dimension or machine-profile field** — only a partial `SceneDocument` snapshot (objects + layers) is persisted. Both are real, current gaps versus a "fully versioned project format with migration," not yet-unwritten placeholders to assume are handled.

---

## Autosave

`ProjectRecoveryStore` writes to a fixed, separate `autosave.lasero` path (`%LocalAppData%\Lasero\recovery`) — never the same path as the user's own project file, so it structurally cannot corrupt the main save. Reuses the same atomic-write serializer. A `DispatcherTimer` (30s interval, `MainWindow.xaml.cs`) triggers it only when the document is dirty; failures are logged, never thrown.

---

## Material Library

Not implemented. Purely aspirational still — no `Material`/`OperationPreset` types exist anywhere in the repo.

---

## Architecture Boundaries

Views (XAML): render UI only. ViewModels (`Lasero.App/ViewModels`): presentation state + commands, `CommunityToolkit.Mvvm` source-generated. Core (`Lasero.Core`): document/domain models, GRBL protocol, toolpath generation, persistence DTOs — no WPF references. `SceneCanvas`/`WorkspaceCanvas` (`Lasero.App/Controls`) are the one place WPF-specific rendering code lives; they read Core models but never write business logic that belongs in Core (e.g. transform math is `ObjectTransform`, not canvas code).

No circular project dependencies (`Lasero.App → Lasero.Core`, `Lasero.Tests → both`).

---

## Rendering

Plain WPF `Canvas` + `Path`/`PathGeometry`, not Win2D. `SceneCanvas` builds one `Path` per object (`PolyLineSegment`s — straight lines only, no `PolyBezierSegment` yet, consistent with the pre-flattened `ImportedShape` model). `SceneThumbnailRenderer` renders the same model off-screen to produce project-card PNGs, sharing `GetWorldShapes()`/`WorldBounds()` with the interactive canvas. Rendering must not mutate the document (already true — confirmed no document writes from rendering code).

---

## Performance

No specific optimization work has been done or is currently required at real-world object counts for this app's use case; the general principles (avoid rebuilding all geometry every pointer move, avoid one XAML element per primitive at high object counts, cache expensive resources) remain good guidance for future work, not a description of work already completed. Correctness before premature optimization.

---

## Input

Confirmed as implemented in `SceneCanvas`: mouse wheel zooms around the cursor, middle-mouse pans, left-click drives select/move/resize/rotate depending on what was clicked, arrow keys nudge the selection, `F` fits the view. Delete/Ctrl+A/Ctrl+C/Ctrl+V/Ctrl+Z/Ctrl+Y/Ctrl+S/Ctrl+O/Ctrl+N should follow standard Windows convention wherever wired — verify against `MainWindow.xaml`'s `InputBindings` before assuming a shortcut exists.

---

## Properties Inspector

Right-side panel should reflect current selection (no selection → document properties, one object → object properties, multiple → common/alignment). Verify current binding state in `MainWindow.xaml` before assuming full parity with this description — this section states intent/convention to follow, not a verified-complete inventory like the sections above.

---

## Validation

Never assume user-entered values are valid — validate machine dimensions, coordinates, speed, power, pass counts, imported files, and device profiles before they can reach a machine command. `JobPreflight` is the existing precedent for this pattern.

---

## Logging

Serilog is already wired (`App.xaml.cs`, `Microsoft.Extensions.Hosting`). Use `Log.Warning`/`Log.Error` (see `GrblConnection`/`ConnectionViewModel` for the existing pattern) for connection, protocol, parsing, project load/save, and job generation/transmission failures. No empty catch blocks.

---

## Error Handling

User-facing errors must explain what happened and what action is possible (see `GrblErrorCodes` for the existing precedent — never show a bare GRBL numeric code to the operator). Technical detail belongs in Serilog logs, not the dialog text.

---

## Testing

xunit, one test class per concept, no shared fixture file (each test class builds its own small sample data inline). Current major coverage: Scene/undo-redo (`SceneObjectTests`, `SceneDocumentTests`, `SceneObjectFactoryTests`, `SceneCommandStackTests`, `ObjectTransformTests`, `DocumentLifecycleTests`), GRBL/GCode parsing (`GCodeParserTests`, `GrblStatusParserTests`, `GrblDeviceProfileParserTests`, `GrblConnectionLifecycleTests`), project persistence (`ProjectFileSerializerTests`), job safety (`JobPreflightTests`, `GCodeJobRunnerLifecycleTests`, `FramingServiceTests`), SVG import (`SvgImporterTests`).

Confirmed gaps at time of writing (check again before trusting this list — it will drift): `RasterImporter` and `ToolpathBuilder` have no dedicated test files (only incidental use as test helpers elsewhere); rendering/canvas code (`SceneCanvas`, `WorkspaceCanvas`, `SceneThumbnailRenderer`) has zero tests (expected — WPF UI code, hard to unit test meaningfully); GRBL greeting-line detection and `error:`/`ALARM:` line classification have no dedicated test coverage despite `GrblConnectionLifecycleTests.cs` existing.

Run `dotnet test` from the repo root before claiming any change is complete. Do not hardcode the current test count in this file — it was already found stale once (README said 54, actual was 86) — check with a fresh `dotnet test` run or a grep for `[Fact]`/`[Theory]` instead.

Never require a physical laser for automated tests — use `IGrblTransport`'s `FakeTransport` pattern (see `GrblConnectionLifecycleTests.cs`) for anything touching `GrblConnection`.

---

## Machine Simulator

Not implemented. No `SimulatedLaserMachine`/`ILaserMachine` exists anywhere in the repo — this is a real, currently-missing capability, not a stub to build on top of. Framing (`FramingService`) still requires a real, connected, Idle machine; there is no hardware-free preview execution path today.

---

## Development Process

Never attempt a giant feature implementation in one step. For every significant task:
1. Inspect relevant existing code.
2. Explain the affected architecture.
3. Propose a small implementation plan.
4. Implement the smallest complete vertical slice.
5. Build.
6. Run tests.
7. Fix errors.
8. Inspect the diff.
9. Report what was verified.

Do not claim success unless the solution builds and `dotnet test` passes.

---

## Current Priorities

Do not hardcode a phase list here — that's exactly what went stale last time. For "what's done" and "what's next," check:
- `README.md` — "Aktuální stav" (current state) and "Zbývá pro další fázi" (remaining for next phase), kept accurate as changes land.
- `PRODUCT.md` — capabilities/constraints and product principles.
- Whatever priority order was most recently confirmed directly with the user for the task at hand — priorities shift session to session; don't assume an old plan still holds without asking.
