# Lasero WPF → Avalonia migration

The WPF application remains the production baseline while `Lasero.Avalonia` is migrated feature by
feature. This avoids a high-risk rewrite and keeps the current, heavily edited UI recoverable.

## Visual fidelity contract

The existing WPF application is the visual source of truth. Avalonia must preserve the same layout,
tokens, Inter typography, icon geometry, control density, Czech copy, interaction states, and window
structure. This is a framework migration, not a redesign. A screen is not complete until a WinApp
screenshot and accessibility-tree pass have been compared with its WPF counterpart at the same
window size.

## Architecture rules

- View models are pure .NET and never reference Avalonia or WPF types.
- Platform operations (dialogs, clipboard, storage, notifications, dispatcher) live behind services.
- Domain failures use `Result`/`Maybe`; exceptions are reserved for unrecoverable failures.
- ReactiveUI owns state and commands. DynamicData is introduced when editor collections become live.
- Prefer Zafiro components and behaviors before adding local infrastructure.
- New members follow the migration skill conventions: no underscore-prefixed fields and no `Async`
  suffix on task-returning methods.

## Migration order

1. **Shell and composition root — visual parity started.** Avalonia desktop startup, DI, WPF design
   tokens, the 60 px title bar, 164 px standard navigation, 56 px editor tool rail, 320 px editor
   inspector, 48 px status strip, Home layout, and Core-backed inventory are present.
2. **Extract application contracts — started.** The project picker is platform-neutral and its
   Avalonia implementation uses `StorageProvider`. Clipboard, notification, dispatcher, and
   thumbnail-renderer contracts remain.
3. **Materials workflow — functionally complete, visual parity pending.** The offline Core catalog is
   filterable by technology, power, operation, and search through a DynamicData pipeline. Its final
   UI must still be ported as the WPF `MaterialsWindow` dialog rather than an embedded replacement.
4. **Project compatibility workflow — complete.** Avalonia can select and safely inspect packaged
   and legacy JSON `.lasero` files without referencing the WPF project.
5. **Document/editor model — vertical slice started.** The Avalonia editor renders the Core
   `SceneDocument` with WPF-matched ruler/grid geometry, zoom/pan, selection, rectangle/line drawing,
   and Core command-stack undo/redo. Text creation, import, all remaining primitives, transforms,
   node editing, snapping, layer management, raster tools, serialization, and the complete inspector
   behavior remain pending. The current editor is not yet feature-parity complete.
6. **Device workflow.** Reuse GRBL and serial logic from Core; expose connection state as observables
   and port device setup, jogging, framing, pause/resume, and emergency stop.
7. **Files and export.** Port import/export dialogs through Avalonia `StorageProvider`; isolate SVG,
   raster, G-code, and preview rendering from UI types.
8. **Assistant and secondary views.** Port Kamil, settings, onboarding, diagnostics, and update UI after
   their underlying services are platform-neutral.
9. **Parity and cutover.** Add UI Automation journeys, compare generated output against WPF, package
   per target OS, then retire the WPF project only after parity is demonstrated.

## Known cross-platform blockers

`Lasero.Core` currently references DirectML ONNX Runtime, `System.Drawing.Common`, serial ports, and
Windows protected-data APIs. They should be split behind capability interfaces before Linux/macOS
publishing. DirectML remains a Windows implementation; image and secret-storage implementations need
portable alternatives. The first Avalonia milestone can still run on Windows while this separation is
completed.

## Verification commands

```powershell
dotnet restore .\Lasero.Avalonia\Lasero.Avalonia.csproj
dotnet build .\Lasero.Avalonia\Lasero.Avalonia.csproj -c Debug
dotnet run --project .\Lasero.Avalonia\Lasero.Avalonia.csproj
```
