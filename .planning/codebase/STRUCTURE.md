# Codebase Structure

**Analysis Date:** 2026-08-07

## Directory Layout

```
lasero-desktop/
├── Lasero.Core/                          # Domain layer (no WPF, zero UI framework)
│   ├── GCode/                           # G-code parsing and geometry
│   │   ├── ArcMath.cs                   # Arc interpolation math (SVG spec appendix F)
│   │   ├── BoundingBox2D.cs             # Axis-aligned bounding box (replaces generic Rect)
│   │   ├── GCodeDocument.cs             # Parsed G-code document for preview/execution
│   │   └── GCodeParser.cs               # G-code text → GCodeDocument
│   │
│   ├── Grbl/                            # GRBL protocol and machine control
│   │   ├── GrblConnection.cs            # Main machine session (threading, queuing, lifecycle)
│   │   ├── GrblSerialTransport.cs       # Serial port implementation (background reader thread)
│   │   ├── GrblStatusParser.cs          # Status report parsing (position/mode/alarms)
│   │   ├── GrblDeviceProfileParser.cs   # $$ settings → device profile
│   │   ├── GrblErrorCodes.cs            # Error/alarm code → Slovak text lookup
│   │   ├── GrblMachineMode.cs           # Controller state enum (Idle/Run/Hold/...)
│   │   ├── MachineStatus.cs             # Current position, mode, feed, spindle state
│   │   ├── MachineAlert.cs              # Alarm/error detail (kind + message)
│   │   ├── Position.cs                  # X/Y/Z point type (shared with Scene/GCode)
│   │   ├── GrblCommandResult.cs         # Response status (OK/Error/etc.)
│   │   ├── GrblRealtimeCommand.cs       # Single-byte realtime commands (!, ~, ?, etc.)
│   │   ├── IGrblTransport.cs            # Transport abstraction (serial/TCP/etc.)
│   │   └── IGrblProtocolParser.cs       # Protocol parser abstraction
│   │
│   ├── Machines/                        # Machine abstraction (generic, not GRBL-specific)
│   │   ├── ILaserMachine.cs             # Generic machine interface (for future simulators)
│   │   └── LaserMachineDisplayState.cs  # Display-layer state enum + resolver
│   │
│   ├── Scene/                           # Document model and undo/redo
│   │   ├── SceneDocument.cs             # Root document: objects + layers
│   │   ├── SceneObject.cs               # One movable/rotatable/scalable item
│   │   ├── ObjectTransform.cs           # Affine transform (translate/rotate/scale)
│   │   ├── SceneObjectFactory.cs        # Creates objects from imported geometry
│   │   └── Commands/                    # Undo/redo command pattern
│   │       ├── ISceneCommand.cs         # Do()/Undo() interface
│   │       ├── SceneCommandStack.cs     # Undo/redo stacks + Changed event
│   │       ├── TransformObjectCommand.cs# Move/rotate/scale (main editing command)
│   │       ├── AddObjectCommand.cs      # Add object + auto-ensure layers
│   │       ├── DeleteObjectsCommand.cs  # Delete objects
│   │       ├── DuplicateObjectsCommand.cs # Clone with new IDs
│   │       ├── ReorderObjectCommand.cs  # Z-order (move up/down in list)
│   │       └── CompositeSceneCommand.cs # Batch multiple commands as one undo step
│   │
│   ├── Import/                          # SVG and raster importers
│   │   ├── ImportedDocument.cs          # Flat shape list + layers (bridge to toolpath)
│   │   ├── ImportedShape.cs             # Closed/open polyline with color
│   │   ├── ToolpathBuilder.cs           # Shapes + layers → G-code (Cut/Fill, scanline fill)
│   │   ├── SvgImporter.cs               # SVG → ImportedDocument
│   │   ├── RasterImporter.cs            # PNG/JPG/BMP → G-code (separate from vector pipeline)
│   │   ├── RasterImportOptions.cs       # DPI, threshold, power settings
│   │   └── Svg/                         # SVG path parsing (hand-rolled, not external lib)
│   │       ├── SvgPathParser.cs         # SVG path data (d=) parsing
│   │       ├── SvgPathTokenizer.cs      # Tokenization of path commands
│   │       ├── SvgShapeFlattener.cs     # Bezier → polyline flattening (16 samples/segment)
│   │       ├── SvgTransform.cs          # Transform matrix parsing
│   │       ├── SvgArcMath.cs            # Elliptical arc endpoint→center conversion
│   │       └── SvgSubpath.cs            # Subpath state during parsing
│   │
│   ├── Raster/                          # New raster processing pipeline
│   │   ├── ImageProcessor.cs            # Load PNG/JPG/BMP → ProcessedImage (grayscale)
│   │   ├── BitmapLoader.cs              # GDI+ bitmap → raw pixels
│   │   ├── GrayscaleImage.cs            # 2D grayscale pixel array (0–1 normalized)
│   │   ├── RasterPlanner.cs             # Grayscale → raster moves (Travel/Burn)
│   │   ├── GrblRasterGenerator.cs       # Raster moves → G-code text (M4 S power)
│   │   └── ProcessedImage.cs            # Grayscale result of ImageProcessor
│   │
│   ├── Jobs/                            # Job execution and validation
│   │   ├── GCodeJobRunner.cs            # Stream G-code to machine, pause/resume/abort
│   │   ├── JobPreflight.cs              # Validation gate before job start
│   │   ├── FramingService.cs            # Generate bounds-outline G-code (safety check)
│   │   ├── JobRunState.cs               # Enum: Idle/Running/Paused/Completed/Aborted
│   │   ├── FramingMode.cs               # Enum: Off/Outline/Grid
│   │   ├── FramingOptions.cs            # Framing settings
│   │   └── JobCompletedEventArgs.cs     # Event payload (name, duration, time)
│   │
│   ├── Layers/                          # Layer settings (Cut vs. Fill)
│   │   ├── LayerSettings.cs             # Speed/Power/Passes/FillInterval + Color/Mode
│   │   ├── LayerMode.cs                 # Enum: Cut or Fill
│   │   └── RgbColor.cs                  # RGB color + IsApproximately() for matching
│   │
│   ├── History/                         # Job history and recovery
│   │   ├── JobHistoryEntry.cs           # One completed job (name, material, duration, time)
│   │   ├── JobHistorySummary.cs         # Aggregated stats (count, total time, etc.)
│   │   └── RecentProjectEntry.cs        # Recently-opened project (path, name, thumbnail)
│   │
│   ├── Materials/                       # Material library (aspirational, not fully implemented)
│   │   ├── MaterialPreset.cs            # Material profile (speed/power/passes)
│   │   └── MaterialPresetLibrary.cs     # Collection + serialization (WIP)
│   │
│   ├── LaseroApi/                       # Cloud account and auth (optional for local-only use)
│   │   ├── LaseroAuthClient.cs          # Login/logout/refresh token
│   │   ├── LaseroAccountClient.cs       # Fetch user profile, settings sync
│   │   ├── SessionStore.cs              # Cache auth state to disk
│   │   ├── FirebaseSession.cs           # Credentials container
│   │   └── PremiumStatus.cs             # User tier + features
│   │
│   └── Lasero.Core.csproj               # .NET 8, no UI framework imports
│
├── Lasero.App/                           # Presentation layer (WPF + MVVM)
│   ├── App.xaml & .xaml.cs              # Application entry point, DI setup, logging
│   ├── MainWindow.xaml & .xaml.cs       # Single-window container, screen switch (Home/Designer)
│   ├── AppScreen.cs                     # Enum: Home / Designer (drives MainWindow panel visibility)
│   │
│   ├── ViewModels/                      # MVVM presentation state
│   │   ├── MainViewModel.cs             # Screen navigation, file I/O, project lifecycle
│   │   ├── SceneViewModel.cs            # Editor state: objects, selection, dirty, undo/redo
│   │   ├── GCodeViewModel.cs            # Job state: loaded doc, run state, progress, framing
│   │   ├── ConnectionViewModel.cs       # Available ports, baudrate, connection state
│   │   ├── MachineStatusViewModel.cs    # Display machine position, mode, alarms
│   │   ├── JogViewModel.cs              # Jog distance/speed, buttons
│   │   ├── ConsoleViewModel.cs          # Raw send/receive lines display
│   │   ├── HomeViewModel.cs             # Dashboard: recent projects, new project button
│   │   ├── MaterialsViewModel.cs        # Material preset editor (WIP)
│   │   └── AccountViewModel.cs          # Login/cloud account UI
│   │
│   ├── Controls/                        # Custom WPF controls
│   │   ├── SceneCanvas.xaml & .xaml.cs # Interactive editor canvas (select/move/resize/rotate/pan/zoom)
│   │   │                               # – DragMode enum (None/Select/Move/Resize/Rotate/Pan)
│   │   │                               # – Hit-testing via transparent Path elements
│   │   │                               # – Keyboard: F (fit), arrow keys (nudge), Delete (remove)
│   │   ├── WorkspaceCanvas.xaml & .xaml.cs # Read-only G-code preview canvas
│   │   ├── RulerMath.cs                # Canvas coordinate/measurement utilities
│   │   └── Ruler/                      # (if present) Horizontal/vertical rulers
│   │
│   ├── Components/                      # Reusable UI components
│   │   ├── DeviceCard.xaml & .xaml.cs  # Machine status display card
│   │   ├── ProjectCard.xaml & .xaml.cs # Recent project thumbnail + name/date
│   │   ├── EmptyState.xaml & .xaml.cs  # Dashboard empty-state message
│   │   └── StatusBadge.xaml & .xaml.cs # Connection/job status indicator
│   │
│   ├── Converters/                      # XAML value converters
│   │   ├── EnumEqualsVisibilityConverter.cs # Enum == value → Visibility
│   │   ├── EnumToBooleanConverter.cs    # Enum == value → bool (checkbox state)
│   │   ├── InverseBooleanConverter.cs   # !value
│   │   ├── InverseBooleanToVisibilityConverter.cs # !value → Visibility
│   │   ├── NotNullConverter.cs          # null → false
│   │   ├── NotNullToVisibilityConverter.cs # null → Hidden
│   │   ├── RgbColorToBrushConverter.cs  # RgbColor → SolidColorBrush
│   │   ├── ThumbnailPathToImageSourceConverter.cs # Path → BitmapImage
│   │   ├── UtcToLocalConverter.cs       # DateTime.Utc → local timezone string
│   │   ├── WidthToCornerRadiusConverter.cs # Width → CornerRadius (reactive sizing)
│   │   └── MachineModeConverters.cs     # GrblMachineMode → display string/color
│   │
│   ├── Theme/                           # Design tokens and theming
│   │   ├── ThemeManager.cs              # Apply dark/light theme, motion preferences
│   │   └── (XAML style resources)       # Color/font/spacing tokens
│   │
│   ├── Thumbnails/                      # Project card rendering
│   │   └── SceneThumbnailRenderer.cs    # Render scene → PNG file (off-screen canvas)
│   │
│   ├── Views/                           # Additional XAML view files
│   │   ├── HomeView.xaml & .xaml.cs     # Dashboard with recent projects
│   │   ├── DesignerView.xaml & .xaml.cs # (if present) Main editor layout
│   │   └── (other full-screen layouts)
│   │
│   ├── Windows/                         # Modal dialog windows
│   │   ├── LoginWindow.xaml & .xaml.cs  # Cloud account login (shown before MainWindow)
│   │   ├── OnboardingWindow.xaml & .xaml.cs # First-run tutorial (if not shown before)
│   │   ├── SettingsWindow.xaml & .xaml.cs # App preferences (device, safety, appearance)
│   │   ├── MaterialsWindow.xaml & .xaml.cs # Material preset editor
│   │   └── RasterImportWindow.xaml & .xaml.cs # Raster import options (DPI, power, etc.)
│   │
│   ├── Stores/                          # App-level persistence
│   │   ├── AppSettingsStore.cs          # Global app settings (device, safety, appearance)
│   │   ├── ProjectFile.cs               # Serialization of SceneDocument → ZIP + JSON
│   │   ├── ProjectFileSerializer.cs     # Load/save .lasero files (ProjectFile ↔ disk)
│   │   ├── ProjectRecoveryStore.cs      # Autosave to recovery path (safe, separate file)
│   │   ├── RecentProjectsStore.cs       # List of recent projects + thumbnails
│   │   ├── JobHistoryStore.cs           # Completed jobs log (name, material, duration)
│   │   ├── MaterialPresetStore.cs       # Saved material profiles (WIP)
│   │   └── SessionStore.cs              # Cloud auth token caching (from Lasero.Core)
│   │
│   ├── ProcessedImagePreviewRenderer.cs # Preview raster processing results
│   ├── Lasero.App.csproj                # net8.0-windows WPF application
│   └── (XAML resources, images, icons)
│
├── Lasero.Tests/                         # Unit test suite (xunit)
│   ├── SceneObjectTests.cs              # SceneObject geometry/transform
│   ├── SceneDocumentTests.cs            # Document model (object/layer management)
│   ├── ObjectTransformTests.cs          # Transform math (rotate/scale/translate)
│   ├── SceneObjectFactoryTests.cs       # Object creation from imported geometry
│   ├── SceneCommandStackTests.cs        # Undo/redo functionality
│   ├── DocumentLifecycleTests.cs        # Load/save/recovery cycle
│   │
│   ├── GCodeParserTests.cs              # G-code parsing
│   ├── GrblStatusParserTests.cs         # Status report parsing
│   ├── GrblDeviceProfileParserTests.cs  # $$ settings parsing
│   │
│   ├── GrblConnectionLifecycleTests.cs  # Connection lifecycle (mocked transport)
│   ├── GrblRasterGeneratorTests.cs      # Raster → G-code conversion
│   ├── ImageProcessorTests.cs           # Image loading and grayscale conversion
│   ├── RasterPlannerTests.cs            # Raster job planning
│   │
│   ├── SvgImporterTests.cs              # SVG import
│   ├── JobPreflightTests.cs             # Preflight validation rules
│   ├── GCodeJobRunnerLifecycleTests.cs  # Job streaming and state transitions
│   ├── FramingServiceTests.cs           # Bounds-outline G-code generation
│   │
│   ├── ProjectFileSerializerTests.cs    # .lasero file I/O
│   ├── AppSettingsStoreTests.cs         # Settings persistence
│   ├── MaterialPresetStoreTests.cs      # Material library I/O
│   ├── JobHistorySummaryTests.cs        # Job stats aggregation
│   ├── SessionStoreTests.cs             # Auth token caching
│   │
│   └── Lasero.Tests.csproj              # net8.0-windows (uses WPF for MainWindow, etc.)
│
├── LaseroDesktop.sln                    # Visual Studio solution file
├── CLAUDE.md                            # Architecture decisions and constraints
├── DESIGN.md                            # Design tokens and visual guidelines
├── PRODUCT.md                           # Product vision and terminology
├── README.md                            # Current status ("Aktuální stav" / "Zbývá pro další fázi")
├── .planning/
│   └── codebase/                        # This codebase map (ARCHITECTURE.md, STRUCTURE.md, etc.)
└── dist/                                # Build outputs (Release builds)
```

## Directory Purposes

**`Lasero.Core/`:**
- Purpose: All domain logic, zero UI framework dependencies
- Contains: Scene model, import/export (SVG/raster), G-code generation, GRBL protocol, job validation
- Key files: `SceneDocument.cs`, `ToolpathBuilder.cs`, `GrblConnection.cs`, `ILaserMachine.cs`
- No WPF, no Forms, no Avalonia — only System.* and MVVM Community Toolkit for observable types

**`Lasero.App/`:**
- Purpose: WPF presentation layer and app entry point
- Contains: MainWindow, ViewModels, custom controls, converters, themes, stores, app lifecycle
- Key files: `App.xaml.cs`, `MainWindow.xaml`, ViewModels, `SceneCanvas`, `ProjectFileSerializer`
- Depends on: `Lasero.Core`, WPF, Community Toolkit MVVM, Serilog

**`Lasero.Tests/`:**
- Purpose: Unit and integration test suite
- Contains: One test class per concept, no shared fixtures (each builds sample data inline)
- Key files: `SceneDocumentTests.cs`, `GrblConnectionLifecycleTests.cs`, `SvgImporterTests.cs`
- Depends on: xunit, `Lasero.Core`, `Lasero.App`

## Key File Locations

**Entry Points:**
- `Lasero.App/App.xaml.cs` — Application startup, DI container, logging setup
- `Lasero.App/MainWindow.xaml` — Single-window container (Home/Designer screen switching)
- `Lasero.Core/Scene/SceneDocument.cs` — Document model (root)
- `Lasero.Core/Grbl/GrblConnection.cs` — Machine session (root for machine control)

**Configuration:**
- `Lasero.App/AppSettingsStore.cs` — App settings (device port, baudrate, safety, appearance)
- `Lasero.App/ProjectFileSerializer.cs` — .lasero file format (ZIP + JSON)
- `.env` (if present) — Development environment variables (not committed)

**Core Logic:**
- `Lasero.Core/Scene/` — Document model, undo/redo, object transforms
- `Lasero.Core/Import/ToolpathBuilder.cs` — Vector toolpath generation (Cut/Fill layers)
- `Lasero.Core/Raster/` — Raster image import and G-code generation
- `Lasero.Core/Grbl/GrblConnection.cs` — GRBL protocol and machine communication
- `Lasero.Core/Jobs/JobPreflight.cs` — Job safety validation

**Testing:**
- `Lasero.Tests/*.cs` — Test files; no fixture directory (each test class is self-contained)
- Run all: `dotnet test` from repo root

## Naming Conventions

**Files:**
- `ClassName.cs` — One public class per file; file name matches class name
- `SomeView.xaml` + `SomeView.xaml.cs` — XAML view + code-behind pair
- `SomeViewModel.cs` — ViewModel class; no separate interface
- `SomeCommand.cs` — `ISceneCommand` implementation (never `SomeEditCommand`, always explicit: `TransformObjectCommand`)
- `*Tests.cs` — Test class; one per concept (no `TestFixtures/` or `TestData/` directories)

**Directories:**
- PascalCase: `ViewModels/`, `Controls/`, `Converters/`, `Commands/`, `Grbl/`, `Import/`
- Plural when collection-like: `Views/`, `Windows/`, `Converters/`, `Commands/`
- Singular when namespaced concept: `Raster/`, `Jobs/`, `Layers/`, `Scene/`, `Machines/`

**Classes:**
- ViewModels: `[Feature]ViewModel` (SceneViewModel, GCodeViewModel, ConnectionViewModel)
- Views/Windows: `[Feature]View.xaml` or `[Feature]Window.xaml` (HomeView, LoginWindow)
- Commands: `[What][Verb]Command` (TransformObjectCommand, DeleteObjectsCommand, AddObjectCommand)
- Converters: `[Type]To[Target]Converter` or `[Purpose]Converter` (RgbColorToBrushConverter, NotNullToVisibilityConverter)
- Stores: `[What]Store` (AppSettingsStore, ProjectRecoveryStore, RecentProjectsStore)
- Interfaces: `I[Concept]` (ILaserMachine, IGrblTransport, ISceneCommand)

**Enums:**
- Values are descriptive: `Idle`, `Run`, `Paused`, not `I`, `R`, `P`
- Mode enums: `[Concept]Mode` (GrblMachineMode, LayerMode)
- State enums: `[Concept]State` (GrblConnectionState, JobRunState)

## Where to Add New Code

**New Feature in the Scene Editor (e.g., text tool, constraint snapping):**
- Model: Add properties to `SceneObject` in `Lasero.Core/Scene/SceneObject.cs` or create a new type in `Lasero.Core/Scene/`
- Command: Create `Lasero.Core/Scene/Commands/[NewFeature]Command.cs` implementing `ISceneCommand` with Do()/Undo()
- ViewModel: Add properties/commands to `Lasero.App/ViewModels/SceneViewModel.cs`
- UI: Extend `Lasero.App/Controls/SceneCanvas.xaml.cs` for input handling, or add a new control
- Tests: Create `Lasero.Tests/[NewFeature]Tests.cs`

**New Machine Operation (e.g., probe, taper jog):**
- Add method to `ILaserMachine` interface in `Lasero.Core/Machines/ILaserMachine.cs`
- Implement in `GrblConnection` in `Lasero.Core/Grbl/GrblConnection.cs`
- Add ViewModel command in `Lasero.App/ViewModels/JogViewModel.cs` or `MachineStatusViewModel.cs`
- Add UI button/input in MainWindow or a new modal window
- Test with `Lasero.Tests/GrblConnectionLifecycleTests.cs` pattern (using FakeTransport)

**New Import Format (e.g., DXF, PDF):**
- Create importer class in `Lasero.Core/Import/[Format]Importer.cs`
- Implement to return `ImportedDocument` (list of shapes + layers)
- Create associated `ImportedDocument.FromFile()` or similar entry point
- Add UI in `Lasero.App/MainWindow.xaml` (File → Import menu) or `RasterImportWindow`
- Add tests in `Lasero.Tests/[Format]ImporterTests.cs`

**New Layer Setting (e.g., AirAssist, MinPower):**
- Add property to `LayerSettings` in `Lasero.Core/Layers/LayerSettings.cs`
- Update serialization in `ProjectFile` (`Lasero.App/ProjectFile.cs`)
- Update UI in `Lasero.App/Views/` (layers panel or materials window)
- Update `ToolpathBuilder` in `Lasero.Core/Import/ToolpathBuilder.cs` to use the new property
- Test round-trip: save project, load project, verify value persists

**New Tool/Modal Window:**
- Create XAML: `Lasero.App/Windows/[ToolName]Window.xaml` + code-behind
- Create ViewModel: `Lasero.App/ViewModels/[ToolName]ViewModel.cs`
- Register in DI: `App.xaml.cs` → `services.AddSingleton<[ToolName]ViewModel>()`
- Show from MainWindow or another window: `new [ToolName]Window(viewModel).ShowDialog()`
- Bind XAML controls to ViewModel properties and commands

**New Converter/Component:**
- Converter: `Lasero.App/Converters/[Purpose]Converter.cs` implementing `IValueConverter`
- Component: `Lasero.App/Components/[ComponentName].xaml` + code-behind, reference in MainWindow or other views
- Register in XAML namespace if not already imported: `xmlns:local="clr-namespace:Lasero.App.Converters"`

## Special Directories

**`.planning/codebase/`:**
- Purpose: Codebase maps (ARCHITECTURE.md, STRUCTURE.md, etc.) generated by `/gsd-map-codebase`
- Generated: Yes
- Committed: Yes (checked into git)
- Usage: Reference during code review, planning, and implementation

**`Lasero.App/bin/` and `Lasero.Core/bin/`:**
- Purpose: Build outputs (compiled DLLs, PDBs, runtimes)
- Generated: Yes (by `dotnet build`)
- Committed: No (in .gitignore)
- Note: Do not edit; rebuild with `dotnet clean && dotnet build`

**`Lasero.App/obj/` and `Lasero.Core/obj/`:**
- Purpose: Intermediate build artifacts and generated files (XAML codegen, resource links)
- Generated: Yes (by `dotnet build`)
- Committed: No (in .gitignore)
- Note: XAML codegen files (`*.g.cs`) are here; don't edit directly

**`dist/`:**
- Purpose: Published/release builds
- Generated: Yes (by build scripts or `dotnet publish`)
- Committed: No (typically)
- Contents: Standalone executable, runtime, dependencies (if self-contained)

## Top-Level Files

- **`CLAUDE.md`** — Architecture decisions, constraints, what IS and ISN'T implemented. Read this before proposing changes.
- **`DESIGN.md`** — Design tokens (colors, fonts, spacing), visual guidelines, motion preferences.
- **`PRODUCT.md`** — Product vision, terminology, use cases. Not a roadmap; updated when the product focus shifts.
- **`README.md`** — Current status ("Aktuální stav" / "Zbývá pro další fázi"), build instructions, quick start.
- **`LaseroDesktop.sln`** — Visual Studio solution file; lists three projects.

---

*Structure analysis: 2026-08-07*
