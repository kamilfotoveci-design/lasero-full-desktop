<!-- refreshed: 2026-08-07 -->
# Architecture

**Analysis Date:** 2026-08-07

## System Overview

Lasero Desktop is a production-quality native Windows desktop application for designing, preparing, and sending jobs to GRBL laser engraving/cutting machines. It consists of three tightly integrated subsystems:

1. **Graphics Editor** (`Lasero.Core/Scene`, `Lasero.Core/Import`, `Lasero.App/Controls`) — object placement, transform, selection, undo/redo
2. **Job / Toolpath Engine** (`Lasero.Core/GCode`, `Lasero.Core/Jobs`, `Lasero.Core/Import/ToolpathBuilder`) — G-code generation and safety validation
3. **Machine Control** (`Lasero.Core/Grbl`, `Lasero.Core/Machines`) — serial communication, status polling, realtime control

```text
┌──────────────────────────────────────────────────────────────────────────┐
│                          Presentation Layer                               │
│                        (Lasero.App)                                       │
│  MainWindow / ViewModels / Controls (SceneCanvas, WorkspaceCanvas)       │
└───────────┬──────────────────────┬──────────────────────┬────────────────┘
            │                      │                      │
            ▼                      ▼                      ▼
┌──────────────────────┬──────────────────────┬──────────────────────────────┐
│  Graphics Editor     │  Job/Toolpath Engine │  Machine Control             │
│  (Scene/Import)      │  (GCode/Jobs)        │  (Grbl/Machines)             │
│  • SceneDocument     │  • ToolpathBuilder   │  • GrblConnection            │
│  • SceneObject       │  • GCodeDocument     │  • ILaserMachine (abstraction)│
│  • Commands/Undo     │  • JobPreflight      │  • Status Polling            │
│  • SVG/Raster Import │  • FramingService    │  • G-code streaming          │
└──────────────────────┴──────────────────────┴──────────────────────────────┘
                        Domain Layer
                     (Lasero.Core)
            ▲
            │ (no UI framework imports)
            │
┌──────────────────────────────────────────────────────────────────────────┐
│               Persistence & External APIs                                 │
│  • ProjectFile (ZIP + JSON)  • Lasero Account API  • GRBL Serial Port    │
└──────────────────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| **SceneDocument** | Central document model: holds all objects + layers, flattens for toolpath generation | `Lasero.Core/Scene/SceneDocument.cs` |
| **SceneObject** | Movable/rotatable/scalable design element with local geometry + world transform | `Lasero.Core/Scene/SceneObject.cs` |
| **SceneCommandStack** | Undo/redo engine with Do()/Undo() command pattern | `Lasero.Core/Scene/Commands/SceneCommandStack.cs` |
| **ToolpathBuilder** | Converts imported shapes + layer settings → G-code (Cut/Fill layers, even-odd scanline fill) | `Lasero.Core/Import/ToolpathBuilder.cs` |
| **GrblConnection** | Single reconnectable GRBL session; queues commands, parses status, manages lifecycle | `Lasero.Core/Grbl/GrblConnection.cs` |
| **ILaserMachine** | Machine-agnostic abstraction (currently implemented by GrblConnection) | `Lasero.Core/Machines/ILaserMachine.cs` |
| **SceneViewModel** | Presentation layer for the editor: selection, dirty tracking, project creation | `Lasero.App/ViewModels/SceneViewModel.cs` |
| **GCodeViewModel** | Job state (loaded doc, run state, progress), framing, preflight validation | `Lasero.App/ViewModels/GCodeViewModel.cs` |
| **MainViewModel** | Screen navigation (Home/Designer), file I/O, autosave recovery | `Lasero.App/ViewModels/MainViewModel.cs` |
| **SceneCanvas** | WPF custom control: renders scene objects, handles mouse/keyboard input, selection/transform UI | `Lasero.App/Controls/SceneCanvas.xaml.cs` |
| **WorkspaceCanvas** | Read-only preview canvas showing the generated G-code path (uses same rendering as SceneCanvas) | `Lasero.App/Controls/WorkspaceCanvas.xaml.cs` |

## Pattern Overview

**Overall:** Three-layer application (Presentation/Domain/Persistence) with unidirectional dependency flow (`Lasero.App` → `Lasero.Core` only).

**Key Characteristics:**

- **Single-window design** — One `MainWindow` stays open for the entire app session. Screen switching (Home/Designer) uses an `AppScreen` enum + visibility-converter bindings, not navigation frames.
- **MVVM + Community Toolkit** — ViewModels inherit `ObservableObject`, use `[ObservableProperty]` source generation and `[RelayCommand]` for ICommand binding.
- **Plain WPF rendering** — `Canvas` + `Path`/`PathGeometry`, not Win2D or WinUI 3. One `Path` per scene object, built at render time from `SceneObject.GetWorldShapes()`.
- **Immutable domain models** — `SceneObject`, `ObjectTransform`, `LayerSettings`, `Position`, `ImportedShape` are all treated as immutable; mutations flow through public property setters (for `[ObservableProperty]` fields) or return new instances.
- **Undo as first-class pattern** — Every destructive edit is an `ISceneCommand` with Do()/Undo() pair. Never mutate the document outside the command stack.
- **No tool-switching architecture yet** — Interaction state (select/move/resize/rotate/pan) lives in `SceneCanvas`'s private `DragMode` enum, decided per-event. Drawing tools are not implemented.
- **Coordinate system is millimeters** — Document space is always double-precision millimeters. Machine coordinates (MPos/WPos) are a separate concern handled entirely in `Grbl/`.

## Layers

**Presentation (`Lasero.App`):**
- Purpose: Bind domain models to XAML, route user input to ViewModels, render scenes to screen
- Location: `Lasero.App/`
- Contains: XAML views, ViewModels, custom controls (`SceneCanvas`, `WorkspaceCanvas`), converters, app-level stores (settings, project recovery, recent projects)
- Depends on: `Lasero.Core`, WPF (System.Windows), CommunityToolkit.Mvvm, Serilog
- Used by: Users; depends on being launched by Windows

**Domain (`Lasero.Core`):**
- Purpose: All business logic, protocol, geometry, toolpath generation — zero UI framework
- Location: `Lasero.Core/`
- Contains: Scene (document model), Import (SVG/raster), GCode (G-code parsing/generation), Grbl (GRBL protocol), Jobs (preflight, framing, job execution), Layers, Materials, History, Machines (abstraction), LaseroApi (cloud auth)
- Depends on: System.* (collections, IO, text, net), CommunityToolkit.Mvvm (for observable models)
- Used by: `Lasero.App`, `Lasero.Tests`

**Tests (`Lasero.Tests`):**
- Purpose: Verify domain logic in isolation, without WPF or UI
- Location: `Lasero.Tests/`
- Contains: One test class per concept (SceneDocumentTests, GrblConnectionLifecycleTests, etc.), each building small sample data inline
- Depends on: xunit, `Lasero.Core`, `Lasero.App` (for stores like AppSettingsStore)
- Used by: `dotnet test` during CI/development

## Data Flow

### Primary Request Path: "New Project → Design → Export"

1. **Startup** (`App.xaml.cs` line 48-77)
   - DI container initialized with singletons for connection, ViewModels, stores
   - `GrblConnection` (implements `ILaserMachine`) registered as singleton
   - `MainViewModel` and all child ViewModels created and injected

2. **Load/Create Project** (`MainViewModel.LoadProject()` / `NewProject()`)
   - `.lasero` file deserializes → `LaseroProjectFile` (ZIP + JSON)
   - `SceneViewModel.LoadProject(project)` → populates `SceneDocument.Objects` and `SceneDocument.Layers`
   - Dirty flag reset, project path stored, screen switches to Designer

3. **Edit Scene** (`SceneCanvas` and `SceneViewModel`)
   - User clicks/drags on canvas → `SceneCanvas.OnMouseDown/Move/Up` interprets as move/resize/rotate/select
   - `SceneViewModel.SelectedObjects` updated; canvas re-renders only affected objects
   - Each undoable change (move, delete, add) is an `ISceneCommand`: `TransformObjectCommand`, `DeleteObjectsCommand`, etc.
   - `SceneCommandStack.Execute(command)` → `command.Do()` → document mutated → ViewModels notified via property-changed events
   - Scene stays in `Lasero.Core.Scene/` with no WPF knowledge; canvas code-behind applies screen-space transforms for rendering

4. **Generate G-code** (`GCodeViewModel.RegenerateFromSceneCommand`)
   - `SceneViewModel.Scene.ToImportedDocument()` flattens all visible objects' world-space shapes + layers
   - `ToolpathBuilder.BuildGCode(importedDoc)` → list of G-code strings (Cut/Fill layers, even-odd scanline fill)
   - For raster objects: `RasterImporter.BuildGCode()` is called separately, output appended
   - Result stored in `GCodeViewModel.GCodeDocument` (parsed for preview)
   - `WorkspaceCanvas` renders the same geometric path as SceneCanvas would (read-only)

5. **Job Execution** (`GCodeViewModel.RunJob()`)
   - `JobPreflight.Evaluate()` checks: machine connected/Idle, bounds valid, framing done if required
   - User confirms in dialog
   - `GCodeJobRunner.Start(gcode, machine)` streams lines to `GrblConnection` with async gate between each line
   - Status polled every 100ms; progress UI updated; Pause/Resume/Abort via `TaskCompletionSource` gate
   - On completion: job logged to `JobHistoryStore`

### Secondary Flow: Raster Import

1. **Import window** (`RasterImportWindow`) shows size/DPI/power/threshold options
2. **RasterImporter.GetPlacedSizeMm()** reads pixel dimensions only (no scanning)
3. **User confirms placement**
4. **SceneObjectFactory.FromRaster()** creates a `SceneObject` with `RasterFilePath` + `RasterOptions` stored
5. **At G-code export time**: `RasterImporter.BuildGCode(filePath, options)` scans actual pixels → `LaserJob` (raster moves), then `GrblRasterGenerator` → G-code text
6. Result appended to vector toolpath

### Secondary Flow: Machine Connection

1. **ConnectionViewModel** monitors available serial ports
2. **User selects port + baud rate**
3. **GrblConnection.Connect(portName, baudRate)**
   - Opens `GrblSerialTransport` (wraps `System.IO.Ports.SerialPort`)
   - Background reader thread started (blocking `ReadLine` loop to handle CH340/CH341 coalescing)
   - Command-queue worker thread started (serializes commands, one in-flight at a time)
   - Firmware banner read (greeting line)
   - State transitions: Connecting → Connected
   - `ConnectionViewModel` notified via `ConnectionStateChanged` event

4. **Status polling started** (`StartStatusPolling()`)
   - Realtime `?` status requests sent every 100ms
   - `GrblStatusParser` parses response → `MachineStatus` (position, mode, feed, spindle, alarms)
   - `StatusUpdated` event fires; `MachineStatusViewModel` refreshes display

5. **Command execution** (via `SendCommandAsync`, `JogAsync`, etc.)
   - Command queued in `_queue` (thread-safe `BlockingCollection`)
   - Queue worker serializes one command at a time via `GrblSerialTransport.WriteLine()`
   - Response awaited with timeout; `TaskCompletionSource` completed
   - `RawLineReceived` event fired for console display
   - If response timeout or unexpected close: connection drops, UI notified

**State Management:**
- **Scene state** — `SceneDocument.Objects` + `Layers` (observable), `SceneViewModel.SelectedObjects`, `SceneCommandStack` (undo/redo)
- **Machine state** — `GrblConnection.State` (link), `GrblConnection.LastStatus` (mode/position/alarms), `GrblConnection.ActiveAlert` (alarm/error details)
- **Job state** — `GCodeViewModel.GCodeDocument`, `GCodeJobRunner.CurrentState`, progress callbacks
- **App state** — `MainViewModel.CurrentScreen` (Home/Designer), `ProjectPath`, `IsDirty`, `ProjectRecoveryStore` (autosave)

## Key Abstractions

**ILaserMachine:**
- Purpose: Machine-agnostic interface, allowing `GrblConnection` to be swapped for a `SimulatedLaserMachine` in the future with zero ViewModel changes
- Examples: `Lasero.Core/Machines/ILaserMachine.cs`
- Pattern: ViewModels and UI code depend on this interface, never on `GrblConnection` directly; `GrblConnection` implements it

**ISceneCommand:**
- Purpose: Undo/redo pattern; every reversible edit is a `Do()`/`Undo()` pair
- Examples: `TransformObjectCommand`, `AddObjectCommand`, `DeleteObjectsCommand`, `DuplicateObjectsCommand`, `ReorderObjectCommand`, `CompositeSceneCommand`
- Pattern: Capture immutable before/after snapshots at construction; Do/Undo reassign properties wholesale, never diffing or in-place mutation

**ObjectTransform (readonly record struct):**
- Purpose: Affine transform (translate/rotate/scale) mapping local shape space to world space; replaces what a generic `Matrix2D` would do
- Examples: `Lasero.Core/Scene/ObjectTransform.cs`
- Pattern: Immutable; has `Apply(point, pivot)` method to map individual points with rotation center

**ImportedDocument / ImportedShape:**
- Purpose: Bridge between importers (SvgImporter, RasterImporter) and the toolpath engine (ToolpathBuilder)
- Examples: `Lasero.Core/Import/ImportedDocument.cs`, `ImportedShape.cs`
- Pattern: Flat list of closed/open polylines with color; created by flattening SVG curves to fixed points or reading raster pixels

**LaserJob (raster):**
- Purpose: Machine-agnostic description of raster burn pattern (moves + bounds + feed rate)
- Examples: `Lasero.Core/Raster/RasterPlanner.cs`
- Pattern: Produced by `RasterPlanner.Plan()` from processed image + options; consumed by `GrblRasterGenerator` for G-code text

**GrblMachineMode vs. GrblConnectionState:**
- Purpose: Split state model preventing single conflated "Idle/Run/Disconnected" enum
- Examples: `Lasero.Core/Grbl/GrblMachineMode.cs`, `Lasero.Core/Grbl/GrblConnection.cs`
- Pattern: `GrblConnectionState` = can-we-talk-to-it (Disconnected/Connecting/Connected); `GrblMachineMode` = what-is-it-doing (Idle/Run/Hold/Jog/Alarm/...); display UI combines both via `LaserMachineDisplayStateResolver`

## Entry Points

**App Startup:**
- Location: `Lasero.App/App.xaml.cs` → `OnStartup()`
- Triggers: User launches .exe
- Responsibilities: Configure logging (Serilog), create DI host, register services, show LoginWindow if needed, show MainWindow

**MainWindow:**
- Location: `Lasero.App/MainWindow.xaml` + `.xaml.cs`
- Triggers: User interaction throughout app session
- Responsibilities: Single-window container, screen switching (Home/Designer via AppScreen enum), command toolbar, left tool rail, right inspector panel, status bar

**New/Open/Save Project:**
- Location: `MainViewModel.NewProject()` / `OpenProject()` / `SaveProject()` / `LoadProject()`
- Triggers: Menu/keyboard shortcuts or programmatic from HomeViewModel recent-project click
- Responsibilities: File I/O, `SceneViewModel` loading, dirty-flag management, recovery-store cleanup, thumbnail rendering

**Scene Canvas Mouse/Keyboard Input:**
- Location: `SceneCanvas.xaml.cs` → `OnMouseDown()`, `OnMouseMove()`, `OnMouseUp()`, `OnKeyDown()`
- Triggers: User clicks/drags/keys while designer is active
- Responsibilities: Interpret as select/move/resize/rotate/marquee/pan based on hit-tested element; create `ISceneCommand` for undoable edits

**Job Generation and Preflight:**
- Location: `GCodeViewModel.RegenerateFromSceneCommand` → `GenerateGCode()` → `JobPreflight.Evaluate()`
- Triggers: Scene changes or user manual regenerate
- Responsibilities: Flatten scene → G-code, validate job safety (bounds, connection, machine state), update progress/messages

**Job Execution:**
- Location: `GCodeViewModel.RunJob()` → `GCodeJobRunner.Start()`
- Triggers: User clicks "Start" button after preflight passes
- Responsibilities: Stream lines to `GrblConnection`, handle pause/resume/abort, log job history

**Machine Connection:**
- Location: `ConnectionViewModel.Connect()` → `GrblConnection.Connect()`
- Triggers: User selects port in connection panel
- Responsibilities: Open serial transport, start reader/queue threads, initialize status polling

## Architectural Constraints

- **Threading:** Single UI thread (WPF dispatcher). `GrblConnection` runs its own background reader thread (blocking `ReadLine` to avoid CH340/CH341 coalescing bugs) and command-queue worker thread (one in-flight command at a time, no send-ahead buffering). Status polling via `Timer`. All public API is `Task`-based; no blocking calls on UI thread.

- **Global state:** Module-level singletons registered in DI container: `GrblConnection`, `AppSettingsStore`, `ProjectRecoveryStore`, `RecentProjectsStore`, `JobHistoryStore`, `MaterialPresetStore`, `SessionStore`, all `ViewModels`. No static fields or static singletons outside the container.

- **Circular imports:** None. Dependency graph: `Lasero.App` → `Lasero.Core`; `Lasero.Tests` → both. No back-references from Core to App.

- **Model/View separation:** `Lasero.Core` contains zero UI framework imports. Scene geometry math (`ObjectTransform`, `BoundingBox2D`) and GRBL protocol are UI-agnostic. `SceneCanvas` is the only place WPF-specific rendering lives; it reads Core models but never writes business logic that belongs in Core.

- **Document mutability:** `SceneDocument` is mutable (ObservableCollection), but mutations must go through `SceneCommandStack.Execute(command)` for undo/redo to work. Never call `Objects.Add()` or `Layers[i].Speed = ...` outside a command. Direct property assignment is only for non-undoable state (visibility, lock, selection).

- **Coordinate system:** All Core code uses millimeters with double precision. Screen-space (viewport pan/zoom) is computed in `SceneCanvas` code-behind, not in Core. Machine coordinates (MPos/WPos) are parsed/stored in `Lasero.Core.Grbl` but never conflated with document space.

- **No polymorphic object hierarchy:** `SceneObject` is a single concrete type whose `LocalShapes` can represent any imported geometry. No `VectorObject`/`RasterObject`/`TextObject` subclasses; distinguish via `IsRaster` property and `RasterFilePath` field instead.

## Anti-Patterns

### Mutating SceneDocument Outside Commands

**What happens:** Code directly calls `SceneDocument.Objects.Add()`, modifies `SceneObject.Transform`, or changes `LayerSettings` speed/power without going through `SceneCommandStack.Execute()`.

**Why it's wrong:** Undo/redo will not work for these changes. The undo stack has no record of them. User hits Ctrl+Z expecting the scene to revert and nothing happens, or worse, the visible scene reverts but some in-memory state stays changed.

**Do this instead:** Always create an `ISceneCommand` subclass (or compose via `CompositeSceneCommand`), execute it via `SceneCommandStack.Execute(command)`, and let the command pair Do()/Undo() handle the property assignments. See `TransformObjectCommand` (`Lasero.Core/Scene/Commands/TransformObjectCommand.cs`) for the canonical pattern.

### Blocking the UI Thread on GrblConnection Operations

**What happens:** Code calls a sync wrapper around `GrblConnection.SendCommandAsync()` (like `.Result` or `.Wait()`) on the UI thread, causing the UI to freeze while waiting for a serial response.

**Why it's wrong:** The GRBL connection runs on background threads. If the UI thread blocks waiting for a response, no input events are processed. If the user tries to interact (click a button, resize the window), the app appears frozen.

**Do this instead:** Always use `await` on `SendCommandAsync()`, `JogAsync()`, `HomeAsync()`, etc. in ViewModel command handlers (which are async-compatible via `async void`). For fire-and-forget cases that don't need the result, use `_ = myTask.ConfigureAwait(false)` (or just don't await, but log warnings if a task fails).

### Hardcoding Colors / Dimensions in UI

**What happens:** A XAML converter or code-behind hardcodes a color like `#E63946` or a size like `40` pixels for a resize handle, without centralizing the token.

**Why it's wrong:** Design tokens drift; if a future UI refresh wants to change all accent colors from red to orange, you'd have to search every file and change every hardcoded string. Centralization makes maintenance and A/B testing easier.

**Do this instead:** Define all design tokens in `ThemeManager` or a `Colors`/`Spacing` constant file (see `DESIGN.md`). Reference them via `DynamicResource` keys in XAML or `ThemeManager` methods in code-behind. See `Lasero.App/Theme/ThemeManager.cs` and existing style definitions.

### Assuming a Field/Property Exists Without Checking CLAUDE.md

**What happens:** Code assumes `EditablePath` (bezier node editing) or `Material` (library) exists and tries to use it, only to find the class doesn't exist or is incomplete.

**Why it's wrong:** The CLAUDE.md file explicitly documents what IS and what ISN'T built. Assuming something exists based on a previous conversation or a tentative design doc leads to mysterious runtime errors.

**Do this instead:** Check CLAUDE.md first. If CLAUDE.md says "not implemented / a real gap," don't assume there's a stub to build on. Create the abstraction from scratch or propose a design before implementing. If something is aspirational (Material library, EditablePath), CLAUDE.md will say so. See lines 77–80 and 210–211 of CLAUDE.md for examples.

## Error Handling

**Strategy:** Two-tier approach — domain exceptions are caught at the ViewModel/UI layer and presented as user-friendly messages; underlying errors are always logged.

**Patterns:**

- **Validation before action:** `JobPreflight.Evaluate()` checks bounds, connection, machine state before allowing job start. Returns a result enum + message, not an exception.
- **Try-catch at ViewModel level:** `LoadProject()`, `SaveProject()`, `GCodeViewModel.RunJob()` wrap all Core operations in try-catch, log the exception, and show a user-friendly message in a MessageBox.
- **Timeout-based connection failure:** `GrblConnection` has a `CommandTimeout` (default 8s); if no response in that window, the connection is closed and the error is logged with the pending command text.
- **GRBL errors as enumerated codes:** `GrblErrorCodes` (Slovak text) provides human-readable messages for GRBL `error:N` and `ALARM:N` codes, never showing bare numeric codes to the user.
- **No swallowing exceptions:** All catch blocks either rethrow, log and display, or log and continue with a safe fallback (e.g., thumbnail render failure doesn't block the save itself).

## Cross-Cutting Concerns

**Logging:** Serilog is configured in `App.xaml.cs` with a rolling daily file sink to `%LocalAppData%\Lasero\logs`. Use `Log.Warning()` and `Log.Error()` (static) throughout for connection issues, parsing errors, job generation failures, and file I/O problems. Include context (device name, file path, command sent) in messages. Never log bare GRBL responses without explanation.

**Validation:** `JobPreflight` is the canonical validation pattern. Check user input (coordinates, power, speed values) before they reach Core logic. Dimensions come from `$$` settings; bounds checking happens before job start. Project files are validated on load (schema version, object counts, layer references).

**Authentication:** `LaseroAuthClient` + `SessionStore` handle cloud account login. Not required for basic laser operation (jog, home, framing) — only for saving projects to cloud. `MainViewModel.Account` gate shows LoginWindow if not signed in at startup.

---

*Architecture analysis: 2026-08-07*
