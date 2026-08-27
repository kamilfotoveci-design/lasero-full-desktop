# Codebase Concerns

**Analysis Date:** 2026-08-07

## Tech Debt

**Project format lacks versioning infrastructure:**
- Issue: `LaseroProjectFile.Version` field exists but no migration logic is implemented anywhere. Schema has evolved (added RasterOptions, RasterAssetEntry) but there is no version-branch handling in `ProjectFileSerializer.Deserialize()`.
- Files: `Lasero.App/ProjectFile.cs`, `Lasero.App/ProjectFileSerializer.cs`
- Impact: Future schema changes will be backward-incompatible. Old projects will either fail to load or load with incorrect/missing data. A real migration system is needed before any more breaking changes.
- Fix approach: Implement `IProjectMigration` interface + a migration pipeline in `ProjectFileSerializer.Load()` that transforms old versions to current on load.

**AppSettings schema also lacks versioning:**
- Issue: `AppSettings.Version = 1` exists but nothing branches on it in `AppSettingsStore.Load()`. Only a generic try/catch discards invalid settings.
- Files: `Lasero.App/AppSettingsStore.cs`
- Impact: If settings schema changes (e.g., new property added, old one renamed), old saved settings files will deserialize with nulls or fail silently, losing user customization.
- Fix approach: Implement `AppSettings` migration logic similar to project format migration.

**GCodeParser assumes machine starts at (0,0,0):**
- Issue: `GCodeParser` initializes position to `Position.Zero` and includes this in bounding box calculation. The comment acknowledges this ("assumes the machine starts at (0,0,0) before the first line"), but it means computed bounds may be a *superset* of the actual job bounds if the first move doesn't revisit the origin.
- Files: `Lasero.Core/GCode/GCodeParser.cs` (lines 31, 137)
- Impact: Framing and workspace preview may show a larger area than necessary. Unlikely to cause real harm (extra margin is conservative), but fragile if assumptions change.
- Fix approach: Document clearly in `GCodeDocument` that bounds include the implicit (0,0,0) start. If stricter bounds are needed, add a separate "actual moves bounds" field or compute it separately.

**No test coverage for RasterImporter:**
- Issue: `RasterImporter` class (`Lasero.Core/Import/RasterImporter.cs`) is only integration-tested via `GCodeViewModel` — there is no dedicated `RasterImporterTests.cs`.
- Files: `Lasero.Core/Import/RasterImporter.cs`
- Impact: Bugs in the facade's parameter mapping (e.g., `TargetHeightMm` logic, `MinPower`/`MaxPower` clamping) are harder to catch and may only surface in interactive use.
- Fix approach: Add `RasterImporterTests.cs` with cases for: size scaling, aspect ratio preservation, parameter bounds, missing file handling.

**No test coverage for ToolpathBuilder:**
- Issue: `ToolpathBuilder` (`Lasero.Core/Import/ToolpathBuilder.cs`) has zero dedicated tests. It's exercised indirectly through end-to-end job generation, but not isolated.
- Files: `Lasero.Core/Import/ToolpathBuilder.cs`
- Impact: Regressions in G-code generation (e.g., scanline fill crossing logic, power/speed formatting) may not be caught until a real job is attempted.
- Fix approach: Add `ToolpathBuilderTests.cs` covering: cut layers (single/multiple passes), fill layers (even-odd rule, holes in shapes), disabled layers, empty/invalid shapes.

**No arc emission in toolpath generation:**
- Issue: `ToolpathBuilder` only emits `G0` (rapid) and `G1` (linear) moves. The codebase has full `ArcMath` support for *reading* arcs from existing G-code (`GCodeParser`), but never *generates* `G2`/`G3` arcs from Bézier curves or circular paths.
- Files: `Lasero.Core/Import/ToolpathBuilder.cs`, `Lasero.Core/GCode/ArcMath.cs`
- Impact: Imported SVG curves are eagerly flattened to polylines, losing all curve information. Jobs are longer in G-code, potentially slower to stream, and less efficient for curved geometry.
- Fix approach: After `EditablePath` is complete (track control points through import), implement arc-fitting logic in `ToolpathBuilder.AppendCutLayer()` to emit `G2`/`G3` for smooth curves.

**No travel-order/path optimization:**
- Issue: `ToolpathBuilder` iterates shapes in raw document order, with no heuristic to minimize rapid moves between shapes or optimize scanline fill order.
- Files: `Lasero.Core/Import/ToolpathBuilder.cs` (lines 47, 75)
- Impact: Job execution is slower than it could be. A large design with scattered objects may require many long rapids, wasting time. No impact on correctness, only efficiency.
- Fix approach: Implement a simple nearest-neighbor heuristic or Traveling Salesman variant to reorder shapes before generation, or defer until profiling shows it's a real bottleneck.

**SceneCanvas is a large, complex file:**
- Issue: `SceneCanvas.xaml.cs` is 945 lines, combining selection logic, transformation math, hit-testing, rendering, and pan/zoom all in one code-behind file. No separate service/helper classes for these concerns.
- Files: `Lasero.App/Controls/SceneCanvas.xaml.cs`
- Impact: Difficult to unit test; high chance of regressions when modifying hit-testing or transformation logic; unclear which methods are "public API" vs. internal helpers.
- Fix approach: Extract a `CanvasTransformService` (pan/zoom/coordinate conversion), a `SelectionService` (hit-test, marquee, multi-select), and a `RenderingService` (path building, visual element management). Keep `SceneCanvas` as a thin composition layer. This is a larger refactoring; defer unless it's actively blocking a feature.

**EditablePath not yet implemented:**
- Issue: `Lasero.Core/Import/EditablePath.cs` is mentioned in CLAUDE.md as "planned/in-progress," but the file does not exist. SVG path parsing still uses `SvgPathParser.Flatten()` which eagerly samples curves into fixed-point polylines with no control-point preservation.
- Files: `Lasero.Core/Import/Svg/SvgPathParser.cs` (uses a fixed 16-step curve sampling)
- Impact: Bezier curves and arcs imported from SVG are immediately lost; users cannot edit curve control points or smooth geometry. Limits the editor's usability for vector designs.
- Fix approach: This is a planned feature. Do not assume it exists until `Lasero.Core/Import/EditablePath.cs` is committed. Unblock by implementing it as a separate, tested module before enabling in the UI.

## Known Bugs

**Alarms and errors are not displayed to the operator:**
- Symptoms: `GrblConnection.ErrorReceived` and `AlarmReceived` events fire with correct human-readable text (`GrblErrorCodes`), but no `Lasero.App` ViewModel subscribes to them. Operators see nothing on screen.
- Files: `Lasero.Core/Grbl/GrblConnection.cs` (events defined but not subscribed to in `Lasero.App`), `Lasero.App/ViewModels/ConnectionViewModel.cs`
- Trigger: Connect to GRBL, trigger an error condition (e.g., soft-limit exceeded, invalid command), watch console for error text — but the operator sees no visual indicator or banner.
- Workaround: Check the machine console log (`ConsoleViewModel`) for error text. This is fragile and requires the operator to know to look there.
- Fix approach: In `MachineStatusViewModel` or a new `MachineAlertViewModel`, subscribe to `ILaserMachine.AlertChanged` and bind a dismissible error/warning banner to the UI. The infrastructure is already in place (machine display state, alert tracking) — this is purely a UI binding gap.

**GetPlacedSizeMm in RasterImporter loads a Bitmap without disposal in one code path:**
- Symptoms: `RasterImporter.GetPlacedSizeMm()` line 21 creates a `Bitmap` in a `using` statement (correct), but if an exception is thrown by `new Bitmap(filePath)` before the `using` closes, or if the method is called with an invalid path, the file handle may remain open until GC collects it.
- Files: `Lasero.Core/Import/RasterImporter.cs` (line 21)
- Trigger: Call `GetPlacedSizeMm()` with a file that triggers an exception (e.g., locked/missing file), then immediately delete the source file — the file may still be locked by the process.
- Workaround: Don't delete source images while the import dialog is open.
- Fix approach: Ensure the exception is raised before the `using` statement is entered, or wrap in a try/catch that explicitly disposes. Current code is defensive but could be clearer.

## Security Considerations

**SVG import does not validate XML structure:**
- Risk: `SvgImporter.Import()` calls `XDocument.Parse()` on user-supplied SVG text, which could contain malicious XML (e.g., billion laughs attack, external entity expansion). The parser uses the default `XDocument` settings, which disable external entities by default in .NET, but the risk is not explicitly mitigated.
- Files: `Lasero.Core/Import/SvgImporter.cs` (line 23)
- Current mitigation: .NET's default `XDocument` disables entity expansion. No explicit XXE (XML External Entity) handling is needed.
- Recommendations: Add a unit test that attempts to load an SVG with a malicious entity expansion payload and confirm it is rejected. Document the assumption explicitly.

**Project files are ZIP archives with arbitrary entries:**
- Risk: `ProjectFileSerializer.Load()` extracts raster assets from `assets/raster-NNNN.ext` entries inside the ZIP. No path-traversal check exists; a malicious ZIP could include entries like `assets/../../../evil.exe`.
- Files: `Lasero.App/ProjectFile.cs` (lines 168-180, `ExtractRasterAssets`)
- Current mitigation: The code uses a hardcoded `GetDefaultAssetCacheDirectory()` per-project path and does not construct file paths from ZIP entry names directly. Entries are expected to be in a fixed `assets/` subdirectory. Path traversal is unlikely but not explicitly validated.
- Recommendations: Add explicit validation in `ExtractRasterAssets()` that ensures every extracted entry's target path is within the cache directory (e.g., `Path.GetFullPath().StartsWith(cacheDir)`). Add a test case for a ZIP with a `../../` traversal attempt.

**Serial port communication is not encrypted:**
- Risk: `GrblSerialTransport` sends all commands and receives all status/responses in plain text over the serial port. If the port is USB or network-accessible, this is a low risk (physically local by definition), but firmware/settings queries expose the machine's configuration.
- Files: `Lasero.Core/Grbl/GrblSerialTransport.cs`
- Current mitigation: GRBL itself does not support encrypted serial communication. The risk is inherent to the protocol, not the implementation.
- Recommendations: No change needed for local USB; if TCP transport is added in the future, TLS wrapping would be essential.

## Performance Bottlenecks

**RasterImporter regenerates the full pixel scan on every preview/edit in the import dialog:**
- Problem: `RasterImportViewModel.RecomputeAsync()` calls `RasterImporter.LoadProcessedPreview()` which re-reads the image file from disk and re-processes every pixel every time a slider (brightness, DPI, power) changes.
- Files: `Lasero.App/ViewModels/RasterImportViewModel.cs` (line 116, `RecomputeAsync`), `Lasero.Core/Import/RasterImporter.cs`
- Cause: No caching of the loaded bitmap or intermediate processing stages. The `DebounceMs` constant (150ms) prevents rapid re-computation, but still rebuilds from the source file on every edit.
- Improvement path: Cache the loaded `GrayscaleImage` in `RasterImportViewModel` and only re-process the tone/brightness/contrast stages on slider changes. Deferring DPI changes until placement would also avoid re-processing.

**SceneCanvas rebuilds all visual elements on object property changes:**
- Problem: When a `SceneObject.Transform` or visibility changes, `RebuildVisuals()` is called, which rebuilds all `Path` and selection-handle visuals from scratch for all objects on screen.
- Files: `Lasero.App/Controls/SceneCanvas.xaml.cs` (search for `RebuildVisuals`)
- Cause: No fine-grained visual update; any change triggers a full rebuild.
- Improvement path: Implement incremental updates where only the changed object's visuals are rebuilt, while keeping others intact. This is a micro-optimization and likely not a real bottleneck unless projects exceed 10,000+ objects (not expected in this use case).

**SVG parser uses a fixed 16-step curve sampling:**
- Problem: Every Bézier curve and elliptical arc in an imported SVG is sampled to 16 fixed line segments, regardless of curve complexity. Complex curves could be sampled more densely, simple curves wasted effort.
- Files: `Lasero.Core/Import/Svg/SvgPathParser.cs`
- Cause: Curve simplification/arc-fitting not implemented. Flattening is deliberate but inflexible.
- Improvement path: Implement adaptive sampling based on curve curvature, or implement proper arc-fitting. Defer until `EditablePath` is complete, which will preserve curves end-to-end instead of sampling at all.

## Fragile Areas

**SceneCanvas coordinate transformation math:**
- Files: `Lasero.App/Controls/SceneCanvas.xaml.cs` (methods like `ToWorldX`, `ToWorldY`, `ToCanvasX`, `ToCanvasY`)
- Why fragile: Pan/zoom state (`_scale`, `_offsetXMm`, `_offsetYMm`), coordinate conversion, and hit-testing are all interdependent. A single off-by-one or inverted-axis mistake breaks selection, movement, and rendering all at once.
- Safe modification: Add comprehensive unit tests for coordinate conversion (screen → world → screen round-trip, edge cases like negative coordinates, very large zoom). Test hit-testing independently. Add comments explaining the Y-axis flip (WPF screen Y down vs. document Y up).
- Test coverage: No dedicated tests exist for this math. Gaps: 1) coordinate round-trip verification, 2) zoom/pan boundary conditions, 3) hit-testing edge cases.

**RasterImportViewModel live preview re-computation:**
- Files: `Lasero.App/ViewModels/RasterImportViewModel.cs` (async Task.Run and cancellation token logic)
- Why fragile: Debouncing + async cancellation means the preview could show stale results if cancellation arrives at the wrong time, or if multiple recomputes fire in quick succession.
- Safe modification: Add explicit tests for cancellation races (e.g., start recompute, cancel immediately, verify no UI update). Verify that `IsComputing` flag transitions correctly (false during delay, true during processing, false at end).
- Test coverage: No tests exist for the async lifecycle. Gaps: 1) cancellation race conditions, 2) multiple rapid changes, 3) exception handling during recompute.

**GrblConnection event subscriptions and thread safety:**
- Files: `Lasero.Core/Grbl/GrblConnection.cs` (event handlers in `OnLineReceived`, `OnUnexpectedlyClosed`)
- Why fragile: Events are raised from the background `_queueThread` while the UI thread may be subscribing/unsubscribing. No explicit synchronization protects the event delegate list.
- Safe modification: Ensure all event raises are marshaled to the correct thread (UI thread for ViewModel subscribers). Test concurrent subscription/unsubscription during connection state changes.
- Test coverage: `GrblConnectionLifecycleTests.cs` tests connection flow, but not concurrent event subscription/unsubscription scenarios.

**ProjectFileSerializer atomic-write pattern:**
- Files: `Lasero.App/ProjectFile.cs` (lines 84-105, Save method)
- Why fragile: The atomic-write logic (write to temp, then `File.Move(overwrite: true)`) is sound, but if the temp file cleanup in the `finally` block fails silently, orphaned `.*.tmp` files accumulate. A disk-full condition during the temp write would leave a partial file.
- Safe modification: Add explicit error logging for temp file cleanup failures. Consider a startup cleanup of stale temp files on app launch.
- Test coverage: Happy-path tests exist. Gaps: 1) disk-full simulation, 2) permission-denied on temp file deletion, 3) temp file cleanup on restart.

## Scaling Limits

**No batch import:**
- Current capacity: Single file import only (one SVG, one raster, or one G-code file at a time).
- Limit: Users cannot import a folder of related designs or compose a large project from multiple files in a single operation.
- Scaling path: Implement multi-file import dialog + batch SceneObject creation. This is a UX enhancement, not a technical limit.

**No hardware-free machine simulator:**
- Current capacity: `ILaserMachine` abstraction exists, but no `SimulatedLaserMachine` is implemented. All operations require a real, connected machine.
- Limit: Cannot test workflows or frame jobs without hardware. Cannot develop offline. Safety gates (e.g., `JobPreflight`) cannot be tested without real hardware.
- Scaling path: Implement a `SimulatedLaserMachine : ILaserMachine` that accepts commands, simulates GRBL state, and returns synthetic status responses. This unblocks offline testing.

**No material library:**
- Current capacity: No `Material` or `OperationPreset` types exist. Layer parameters are manual entry only.
- Limit: Users cannot save/reuse parameter presets across projects. No offline material database (plywood speed/power, acrylic, fabric, etc.).
- Scaling path: Implement `Lasero.Core/Materials/MaterialLibrary.cs` + UI for browsing/applying presets. The PRODUCT.md mentions this as a future priority.

**Canvas rendering not tested for large object counts:**
- Current capacity: No known limit, but 945-line `SceneCanvas` with path-building logic per object could degrade if projects exceed thousands of objects.
- Limit: Unknown (no load tests exist). Likely O(N) per render, acceptable up to ~1000 objects, needs profiling beyond that.
- Scaling path: Profile with a large test project (1000+ simple objects) and identify bottlenecks. Optimize rendering batching/deferred updates if needed.

## Dependencies at Risk

**System.Drawing (GDI+) for bitmap loading:**
- Risk: `System.Drawing` is Windows-only and relies on GDI+, a legacy Windows subsystem. It is maintained but deprecated in favor of newer APIs (e.g., `Windows.Graphics.Imaging`).
- Impact: If GDI+ is removed from future Windows versions, raster import will break. Current risk is low (GDI+ has been stable for 20+ years), but long-term, a migration path would be prudent.
- Migration plan: Consider `ImageSharp` (cross-platform, but adds dependency) or `Windows.Graphics.Imaging` (Windows-only but modern) if GDI+ ever becomes unavailable.

**CommunityToolkit.Mvvm for MVVM boilerplate:**
- Risk: A third-party package maintained by Microsoft, but not part of the standard framework. Dependency on source-code generation (`[ObservableProperty]`, `[RelayCommand]`). If the package is abandoned, code generation breaks.
- Impact: Medium. The generated code is straightforward (`PropertyChanged` event raising, command wiring). If needed, the boilerplate could be hand-written instead, but it would increase code verbosity.
- Migration plan: If the package becomes unmaintained, manually implement `INotifyPropertyChanged` and `ICommand` in base classes. Not urgent; CommunityToolkit is well-maintained by Microsoft.

**System.IO.Ports for serial communication:**
- Risk: `System.IO.Ports.SerialPort` is stable but basic. No support for USB serial emulation layer details, flow control, or advanced protocols. If GRBL support expands to other transports (Ethernet, Bluetooth), this becomes a bottleneck.
- Impact: Low for current GRBL-only use case. Would be a problem if multi-controller/multi-transport is needed.
- Migration plan: The `IGrblTransport` interface already abstracts serial communication. Adding `TcpGrblTransport` or `BluetoothGrblTransport` would not require changes to higher layers. Current risk is acceptable.

## Missing Critical Features

**Visual feedback for machine alarms and errors:**
- Problem: Alarms and errors are parsed and fired as events, but the UI has no banner, status indicator, or dialog to show them. Operators may not know a critical problem occurred.
- Blocks: Safety-critical workflows cannot rely on error visibility. An operator may attempt to run a job unaware the machine is in an alarm state.
- Priority: High. This is a real safety gap noted in CLAUDE.md.

**Versioned project format with migration:**
- Problem: `.lasero` project schema has evolved (raster assets, raster options) but no versioning infrastructure prevents old projects from loading with missing data or crashing.
- Blocks: Safe schema evolution and long-term backward compatibility.
- Priority: Medium. Low risk now (few projects exist), but essential before production release.

**Text tool and polygon drawing:**
- Problem: The left tool rail has placeholder buttons for text and polygon drawing, but they are not implemented.
- Blocks: Text/label placement, complex polygon editing.
- Priority: Low. Nice-to-have, not core to the laser engraving workflow.

## Test Coverage Gaps

**Untested areas:**
- `RasterImporter` has zero dedicated tests. Gaps: file I/O errors, invalid image formats, extreme dimensions, parameter bounds.
- `ToolpathBuilder` has zero dedicated tests. Gaps: multi-pass layer handling, fill scanline edge cases, disabled layers.
- `SceneCanvas` coordinate transformation and hit-testing have no unit tests. Gaps: round-trip accuracy, zoom/pan edge cases, multi-select drag math.
- `RasterImportViewModel` async cancellation and preview recomputation have no tests. Gaps: cancellation race conditions, rapid consecutive edits, exception handling.
- `ProjectFileSerializer` has tests for happy-path save/load, but not for: disk-full, temp file cleanup failures, corrupted ZIP entries, path-traversal in asset extraction.
- GRBL machine-control integration has unit tests for connection lifecycle and command flow, but not for: real hardware error recovery, concurrent command/status-report interleaving, timeout + reconnect edge cases.

**Files at risk:**
- `Lasero.Core/Import/RasterImporter.cs` - No tests
- `Lasero.Core/Import/ToolpathBuilder.cs` - No tests
- `Lasero.App/Controls/SceneCanvas.xaml.cs` - No tests (WPF code, expected)
- `Lasero.App/ViewModels/RasterImportViewModel.cs` - No async lifecycle tests

---

*Concerns audit: 2026-08-07*
