# Coding Conventions

**Analysis Date:** 2026-08-07

## Naming Patterns

**Files:**
- PascalCase: `SceneDocument.cs`, `GrblConnection.cs`, `ProjectFileSerializer.cs`
- Type names match file names exactly (one public class/type per file)
- Enum types in PascalCase: `JobRunState.cs`, `LayerMode.cs`, `FramingMode.cs`

**Classes/Structs/Records:**
- PascalCase: `SceneObject`, `ObjectTransform`, `BoundingBox2D`, `ImportedShape`
- Sealed classes used when inheritance is not intended: `sealed class GrblConnection`, `sealed class SceneDocument`
- Record structs for immutable data: `ObjectTransform` (X, Y, RotationDeg, ScaleX, ScaleY)
- Observable classes inherit from `ObservableObject` (MVVM Toolkit): `public sealed partial class SceneObject : ObservableObject`

**Functions/Methods:**
- PascalCase: `GetWorldShapes()`, `WorldBounds()`, `BuildFrameGCode()`, `Disconnect()`
- Test methods: PascalCase with underscores for logical grouping — `IdentityTransformLeavesPointUnchanged()`, `ApplyThenInverseRoundTrips()`, `DisplayStateResolverMapsLinkAndModeWithoutAlert()`
- Private methods: PascalCase with underscore prefix: `_OnLineReceived()` (though often just lowercase `OnLineReceived()`)
- Async methods: same PascalCase, verb-driven: `SendCommandAsync()`, `IdentifyDeviceAsync()`, `RunJob()`, `RunFraming()`
- Command methods (MVVM): private or public, decorated with `[RelayCommand]`: `private async Task RunJob()` → accessible as `RunJobCommand` property

**Variables/Fields:**
- Local variables: camelCase: `var local = new Position(15, 20, 0)`, `int baudRate`, `string portName`
- Private fields: camelCase with underscore prefix: `private readonly IGrblTransport _transport`, `private GrblJobRunner? _activeRunner`
- ObservableProperty backing fields: camelCase with underscore: `[ObservableProperty] private string _name = string.Empty` → generates `public string Name` property
- Constants: UPPER_SNAKE_CASE (rare in this codebase; most magic numbers are localized)
- Static fields: camelCase with underscore: `private static readonly Position Pivot = new(10, 10, 0)`

**Types/Enums:**
- Enum names: PascalCase, singular: `enum GrblConnectionState { Disconnected, Connecting, Connected }`
- Enum values: PascalCase, no prefix: `JobRunState.Idle`, `LayerMode.Cut`
- Interface names: IPrefixed + PascalCase: `ISceneCommand`, `IGrblTransport`, `ILaserMachine`

## Code Style

**Formatting:**
- No `.editorconfig` file; formatting follows .NET conventions
- Implicit using statements enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Generally 4-space indentation (standard C#)
- Braces on same line (not Allman style)

**Language Features:**
- C# 8.0+ nullable reference types enforced throughout
- Record types for immutable data models: `public record struct ObjectTransform(double X, double Y, double RotationDeg, double ScaleX, double ScaleY)`
- Target-typed `new()`: `return new() { X = 5, Y = 10 };`
- Collection initializers: `LocalShapes = [shape1, shape2]`
- Expression-bodied members common: `public bool IsRaster => RasterFilePath is not null;`
- `required` keyword for mandatory properties: `public required IReadOnlyList<ImportedShape> LocalShapes { get; init; }`
- Init-only properties for immutability: `public Guid Id { get; init; } = Guid.NewGuid();`

**String Formatting:**
- Invariant culture for numbers: `x.ToString("0.###", CultureInfo.InvariantCulture)` (used in G-code output)
- String interpolation for display/logging: `$"Loaded: {Document!.Segments.Count} moves"`
- User-facing messages in Slovak/Czech: `"Časový limit příkazu musí být kladný."`

## Import Organization

**Order (observed from source files):**
1. System.* namespaces: `using System;`, `using System.Collections.ObjectModel;`
2. Other System.* groups: `using System.Globalization;`, `using System.IO.Ports;`
3. Third-party libraries: `using CommunityToolkit.Mvvm.ComponentModel;`, `using Serilog;`
4. Lasero namespaces: `using Lasero.Core.Scene;`, `using Lasero.Core.Grbl;`

**Path Aliases:**
- No custom path aliases observed in codebase
- Implicit using statement for `Xunit` in test project (set in `.csproj`)

**Namespace Structure:**
- `Lasero.Core` — domain logic, no WPF dependencies
  - `Lasero.Core.Scene` — document model, undo/redo
  - `Lasero.Core.GCode` — parsing, segments, bounding boxes
  - `Lasero.Core.Grbl` — machine communication, status parsing
  - `Lasero.Core.Import` — SVG/raster importing, toolpath generation
  - `Lasero.Core.Jobs` — job execution, framing, safety
  - `Lasero.Core.Layers` — layer settings, colors
  - `Lasero.Core.Machines` — machine abstractions (ILaserMachine)
  - `Lasero.Core.LaseroApi` — cloud account client
- `Lasero.App` — WPF UI layer
  - `Lasero.App.ViewModels` — MVVM presentation logic
  - `Lasero.App.Controls` — custom WPF controls (SceneCanvas, WorkspaceCanvas)
  - `Lasero.App.Converters` — XAML value converters
- `Lasero.Tests` — xunit test classes

## Error Handling

**Pattern:**
- Input validation at entry points using guard conditions:
  ```csharp
  ArgumentException.ThrowIfNullOrWhiteSpace(portName);
  if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
  if (CommandTimeout <= TimeSpan.Zero) throw new InvalidOperationException("Message");
  ```
- Specific exception types, never bare `throw new Exception()`
- User-facing error messages localized in Slovak/Czech with descriptive detail
- Technical stack traces logged via Serilog, not shown in UI dialogs

**Logging:**
- Serilog integration via `Microsoft.Extensions.Hosting`
- Log levels: `Log.Warning()` for recoverable issues, `Log.Error()` for failures
- Example: `Log.Error(ex, "Settings retrieval failed")` in `GrblConnection`
- No empty catch blocks; always handle or log-and-rethrow

**Exception Handling in Async Code:**
- `.WaitAsync(TimeSpan)` for timeout protection
- `OperationCanceledException` caught separately from other exceptions
- `TimeoutException` caught explicitly in connection code
- Example from `GrblConnection`:
  ```csharp
  catch (TimeoutException ex)
  {
      // Handle timeout
  }
  catch (OperationCanceledException)
  {
      // Handle cancellation
  }
  ```

## Logging

**Framework:** Serilog

**Patterns:**
- Configured in `App.xaml.cs` via `Microsoft.Extensions.Hosting`
- File sink: `Serilog.Sinks.File`
- Usage: `Log.Warning("message")`, `Log.Error(ex, "message with exception")`
- Current gaps (per CLAUDE.md): no per-component categorization (use nameof(typeof) as fallback)

## Comments

**When to Comment:**
- Document public API intent with XML docs: `/// <summary>Builds G-code for framing...</summary>`
- Explain non-obvious algorithmic choices (e.g., "Rotating a rectangle by 90 degrees..." in test)
- Never state obvious code: `x += 5; // Add 5 to x` is noise
- Add comments for workarounds: "We use blocking-read threads instead of `DataReceived` events due to CH340/CH341 coalescing bugs"

**XML/Doc Comments:**
- Classes and public methods documented with `///` summary tags
- Parameter documentation: `/// <param name="portName">The serial port name</param>`
- Return documentation: `/// <returns>The transformed point in world space</returns>`
- Example from `SceneObject.cs`:
  ```csharp
  /// <summary>
  /// This object's shapes with Transform applied — absolute mm, ready for ToolpathBuilder.
  /// </summary>
  public IReadOnlyList<ImportedShape> GetWorldShapes() => ...
  ```

## Function Design

**Size:**
- Small, focused functions preferred
- Example: `FramingService.Format()` is a 3-line private helper
- Largest methods in this codebase are connection state machines and UI event handlers (legitimately complex due to threading)

**Parameters:**
- Prefer few parameters; group related ones into records if more than 3
- Use named parameters for boolean flags in calls
- Required properties used for POCO mandates: `public required IReadOnlyList<ImportedShape> LocalShapes`

**Return Values:**
- `bool` for predicates: `IsRaster`, `CanRun`
- Collections as `IReadOnlyList<T>` (immutable from caller's perspective): `GetWorldShapes() => IReadOnlyList<ImportedShape>`
- Nullable reference types enforced: return `string?` or `T?` when null is valid
- Tuples for local multi-return: `var (cx, cy, inX, inY) = corners[i]`
- Records for structured returns: `LaserMachineDisplayStateResolver.Resolve()` returns an enum, but complex results use records

## Module Design

**Exports:**
- Namespaces, not internal classes
- `public` types meant for external use
- `internal` for implementation detail within assembly
- `private` for truly encapsulated detail

**Barrel Files:**
- Not observed in this codebase (no re-export index files)
- Each namespace naturally groups related types

**Patterns:**
- Service classes are static or sealed singletons: `FramingService` (static), `GrblConnection` (sealed singleton)
- ViewModels are partial classes with `[ObservableProperty]`-generated properties
- Commands decorated with `[RelayCommand]` generate a corresponding `XyzCommand` property on the ViewModel
- Data models (Scene, Import, GCode) are POCOs with minimal logic; logic lives in service classes that operate on them

## Async/Await Patterns

**Threading Model:**
- `GrblConnection` uses dedicated background threads: queue thread for serialization, status poll timer
- UI callbacks marshaled via `Application.Current.Dispatcher.Invoke()` from background threads
- No `ConfigureAwait` needed because dispatcher ensures UI thread returns (WPF-specific)
- Test code uses `Task.WhenAll()` for parallel waits, `.WaitAsync(TimeSpan)` for timeouts

**Command Async Pattern:**
```csharp
[RelayCommand]
private async Task RunJob()
{
    try
    {
        // UI interaction
        var result = await _connection.SendCommandAsync("G0 X1");
        // Update UI
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error: {ex.Message}");
    }
}
```

## Data Models

**Immutability:**
- Record structs for coordinate/transform data: `ObjectTransform`, `Position`, `BoundingBox2D`
- Init-only properties for POCO data classes: `LaseroProjectFile` (serialization DTO)
- Mutable ObservableObject subclasses only in MVVM layer (ViewModels), never in Core

**Value Semantics:**
- `Position` is a struct (value type): immutable, no allocation per-point in arrays
- `ObjectTransform` is a record struct: value equality, natural with `with` copies
- `BoundingBox2D` is a struct with methods that return new instances rather than mutate

## MVVM Patterns

**ViewModels:**
- Inherit from `CommunityToolkit.Mvvm.ObservableObject`
- Use `partial class` to enable source-gen MVVM properties
- Decorate observable properties with `[ObservableProperty]`: generates `Name` property from `_name` field
- Commands with `[RelayCommand]`: generates `RunJobCommand` property from `private async Task RunJob()`
- `[RelayCommand(CanExecute = nameof(CanRun))]` for conditional commands
- Constructor dependency injection of services (no Service Locator)

**Data Binding:**
- XAML binds directly to ViewModel properties: `<TextBlock Text="{Binding StatusText}"/>`
- Commands bound as: `<Button Command="{Binding RunJobCommand}"/>`
- Converters for type/enum → UI mappings: `InverseBooleanConverter`, `EnumEqualsVisibilityConverter`

---

*Convention analysis: 2026-08-07*
