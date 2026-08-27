# Testing Patterns

**Analysis Date:** 2026-08-07

## Test Framework

**Runner:**
- xunit 2.9.3
- Config: `Lasero.Tests/Lasero.Tests.csproj`
- .NET 8.0 (`net8.0-windows`), WPF enabled (`<UseWPF>true</UseWPF>`)

**Coverage:**
- coverlet.collector 10.0.1 (integrated code coverage)
- Target: not enforced in codebase (no specific % requirement)

**Run Commands:**
```bash
dotnet test                    # Run all tests
dotnet test --watch           # Watch mode (if supported)
dotnet test /p:CollectCoverage=true  # With coverage report
```

## Test File Organization

**Location:**
- All test files in `Lasero.Tests/` directory (not co-located with source)
- One test class per concept: `SceneObjectTests.cs`, `GrblConnectionLifecycleTests.cs`, `ObjectTransformTests.cs`
- No separate fixtures file; test data built inline per test

**Naming:**
- Test classes: `[Concept]Tests` (PascalCase): `SceneObjectTests`, `GCodeParserTests`, `FramingServiceTests`
- Test methods: PascalCase, verb-first, readable names: `IdentityTransformLeavesPointUnchanged()`, `DisplayStateResolverMapsLinkAndModeWithoutAlert()`
- File names match class names: `SceneObjectTests.cs` contains `public class SceneObjectTests`

**Structure:**
```
Lasero.Tests/
├── SceneObjectTests.cs
├── SceneDocumentTests.cs
├── ObjectTransformTests.cs
├── GrblConnectionLifecycleTests.cs
├── GCodeParserTests.cs
├── ProjectFileSerializerTests.cs
└── ... (22 test files total)
```

## Test Structure

**Fact/Theory Pattern:**
```csharp
public class SceneObjectTests
{
    [Fact]
    public void GetWorldShapesWithIdentityTransformReproducesLocalPoints()
    {
        // Arrange
        var obj = MakeSquareObject();
        
        // Act
        var world = obj.GetWorldShapes().Single();
        
        // Assert
        Assert.Equal(obj.LocalShapes[0].Points, world.Points);
    }
    
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(37, 1, 1)]
    [InlineData(90, 2, 0.5)]
    public void ApplyThenInverseRoundTrips(double rotationDeg, double scaleX, double scaleY)
    {
        var transform = new ObjectTransform(4, -6, rotationDeg, scaleX, scaleY);
        var local = new Position(23, 17, 0);
        
        var world = transform.Apply(local, Pivot);
        var roundTripped = transform.Inverse(world, Pivot);
        
        Assert.Equal(local.X, roundTripped.X, precision: 6);
        Assert.Equal(local.Y, roundTripped.Y, precision: 6);
    }
}
```

**Implicit Xunit Using:**
- `Lasero.Tests.csproj` includes `<Using Include="Xunit" />` (implicit global using)
- No need for `using Xunit;` in test files

**Patterns:**
- No shared `ICollectionFixture` or `IAsyncLifetime` (each test is self-contained)
- Constructors may initialize test-wide resources (`IDisposable` cleanup when needed)
- Example: `ProjectFileSerializerTests` implements `IDisposable` for temp directory cleanup:
  ```csharp
  public sealed class ProjectFileSerializerTests : IDisposable
  {
      private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-project-tests", Guid.NewGuid().ToString("N"));
      
      public ProjectFileSerializerTests() => Directory.CreateDirectory(_directory);
      public void Dispose() => Directory.Delete(_directory, recursive: true);
  }
  ```

## Test Data Setup

**Inline Test Data (Preferred):**
```csharp
private static SceneObject MakeSquareObject()
{
    var shape = new ImportedShape
    {
        Points = new[]
        {
            new Position(0, 0, 0),
            new Position(20, 0, 0),
            new Position(20, 10, 0),
            new Position(0, 10, 0),
            new Position(0, 0, 0),
        },
        IsClosed = true,
        LayerColor = RgbColor.Red,
        PreferredMode = LayerMode.Cut,
    };
    
    return new SceneObject
    {
        LocalShapes = [shape],
        LocalPivot = new Position(10, 5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 20, 10),
    };
}
```

**Test Constants:**
```csharp
private static readonly Position Pivot = new(10, 10, 0);
```

## Mocking

**Framework:** None (no Moq/NSubstitute); interface implementations used instead

**Fake Implementation Pattern:**
```csharp
private sealed class FakeTransport : IGrblTransport
{
    private readonly ConcurrentQueue<string> _writtenLines = new();
    
    public bool IsOpen { get; private set; }
    public event Action<string>? LineReceived;
    
    public void Open(string portName, int baudRate)
    {
        IsOpen = true;
    }
    
    public void WriteLine(string text)
    {
        _writtenLines.Enqueue(text);
        LineReceived?.Invoke(text);
    }
    
    public void RaiseLine(string line) => LineReceived?.Invoke(line);
}
```

**Usage:**
- Used in `GrblConnectionLifecycleTests.cs` to test `GrblConnection` without real serial port
- Allows controlled response simulation: `transport.RaiseLine("ok")`, `transport.RaiseLine("error:9")`
- Each test creates its own `FakeTransport` instance

**What to Mock:**
- External I/O (`IGrblTransport` for serial ports, file system operations)
- Time-dependent behavior (can't easily mock `System.Timers.Timer`)

**What NOT to Mock:**
- Domain models (`SceneObject`, `ObjectTransform`)
- Business logic (`FramingService`, `ToolpathBuilder`)
- Only mock at integration boundaries (transport/network/file system)

## Assertions

**Assertion Library:** Xunit's static `Assert` class

**Common Patterns:**
```csharp
Assert.Equal(expected, actual);
Assert.Equal(expected, actual, precision: 6);  // Floating-point tolerance
Assert.NotEqual(value1, value2);
Assert.True(condition);
Assert.False(condition);
Assert.Null(obj);
Assert.NotNull(obj);
Assert.Single(collection);
Assert.Contains(value, collection);
Assert.DoesNotContain(value, collection);
Assert.Empty(collection);
Assert.Throws<ExceptionType>(() => code);
Assert.All(collection, item => assertion);
```

**Floating-Point Precision:**
```csharp
Assert.Equal(0, world.X, precision: 6);  // 6 decimal places
Assert.Equal(0, topLeftWorld.X, precision: 6);
```

**Async Assertion Pattern:**
```csharp
var result = await connection.SendCommandAsync("G0 X1").WaitAsync(TimeSpan.FromSeconds(1));
Assert.False(result.IsOk);
Assert.Contains("error text", result.Message, StringComparison.OrdinalIgnoreCase);
```

## Test Types

**Unit Tests (Majority):**
- Scope: Single class/service in isolation
- Example: `SceneObjectTests` tests `SceneObject.GetWorldShapes()` and `WorldBounds()` with various transforms
- Setup: Build test data inline
- Coverage: `ObjectTransformTests`, `GCodeParserTests`, `FramingServiceTests`, `LayerSettingsTests`

**Integration Tests:**
- Scope: Multiple components working together
- Example: `GrblConnectionLifecycleTests` tests full connection lifecycle with `FakeTransport` + state transitions
- Setup: Use fakes for external boundaries, real logic for everything else
- Coverage: `ProjectFileSerializerTests` (persistence with ZIP archives), `GrblConnectionLifecycleTests` (machine state machine)

**End-to-End Tests:**
- Scope: Not automated in this codebase
- Gap: No `RasterImporter` or `ToolpathBuilder` dedicated test files (only incidental use in manual/integration testing)
- No canvas/WPF UI tests (expected — WPF UI code is difficult to unit test)

## Async Testing

**Pattern:**
```csharp
[Fact]
public async Task DisconnectCompletesPendingAndQueuedCommands()
{
    var transport = new FakeTransport();
    using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(10) };
    connection.Connect("COM1");
    var first = connection.SendCommandAsync("G0 X1");
    var second = connection.SendCommandAsync("G0 X2");
    Assert.True(transport.LineWritten.Wait(TimeSpan.FromSeconds(1)));
    
    connection.Disconnect();
    
    var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    Assert.All(results, result => Assert.False(result.IsOk));
}
```

**Key Points:**
- Test method itself is `async Task`
- Use `await` for async operations
- Timeout protection with `.WaitAsync(TimeSpan)`
- `ManualResetEventSlim` for synchronization between test and background threads (`transport.LineWritten.Wait()`)

## Error Testing

**Exception Assertion:**
```csharp
[Fact]
public void FailedOpenReturnsConnectionToDisconnectedState()
{
    var transport = new FakeTransport { OpenException = new IOException("Port je obsazený.") };
    using var connection = new GrblConnection(transport);
    
    Assert.Throws<IOException>(() => connection.Connect("COM1"));
    Assert.Equal(GrblConnectionState.Disconnected, connection.State);
}
```

**Expected Behavior Testing:**
```csharp
[Fact]
public async Task ErrorLineRaisesErrorReceivedSetsActiveAlertAndClearsOnNextOk()
{
    var transport = new FakeTransport();
    using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(2) };
    connection.Connect("COM1");
    
    (int Code, string Message)? received = null;
    connection.ErrorReceived += (code, message) => received = (code, message);
    
    var pending = connection.SendCommandAsync("G0 X1");
    Assert.True(transport.WaitForLineCount(1));
    transport.RaiseLine("error:9");
    
    var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.False(result.IsOk);
    Assert.Equal(9, received!.Value.Code);
    Assert.Contains("Odomknutie", received.Value.Message, StringComparison.OrdinalIgnoreCase);
}
```

## Current Test Coverage

**Well-Tested (Dedicated Test Files):**
- `SceneObjectTests.cs` — SceneObject geometry transformations
- `SceneDocumentTests.cs` — document model and object collections
- `ObjectTransformTests.cs` — affine transform math (identity, translation, rotation, scale, resize)
- `SceneCommandStackTests.cs` — undo/redo command execution
- `GrblConnectionLifecycleTests.cs` — connection state, command queueing, response parsing, error/alarm handling
- `GrblStatusParserTests.cs` — status report parsing (`<Idle|MPos:...>`)
- `GrblDeviceProfileParserTests.cs` — `$$` settings parsing
- `GCodeParserTests.cs` — G-code parsing (bounding box, mode changes, unit conversion)
- `FramingServiceTests.cs` — frame G-code generation (outline and corner-stub modes)
- `SvgImporterTests.cs` — SVG path parsing and shape flattening
- `ProjectFileSerializerTests.cs` — project save/load, ZIP embedding, recovery store
- `JobPreflightTests.cs` — job validation (bounds, connection, status freshness)
- `GCodeJobRunnerLifecycleTests.cs` — job execution flow (pause/resume/abort)

**Gaps (Confirmed):**
- `RasterImporter` — no dedicated test file (only incidental use)
- `ToolpathBuilder` — no dedicated test file (only incidental use); behavior tested via G-code output inspection
- Scene canvas rendering (`SceneCanvas`, `WorkspaceCanvas`, `SceneThumbnailRenderer`) — zero tests (WPF UI, hard to unit test)
- GRBL greeting-line detection — no dedicated test despite `GrblConnectionLifecycleTests.cs` covering other connection aspects
- Machine simulator/preview — not implemented, so no tests

**Priority for Adding:**
1. `ToolpathBuilder` — high-value, pure logic, no UI dependency
2. `RasterImporter` — similar to ToolpathBuilder
3. Canvas hit-testing and multi-object operations (resize/rotate together) — lower priority, more complex WPF involvement

## Running Tests

**Command Line:**
```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~SceneObjectTests"

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover

# Watch mode (if supported by test runner)
dotnet test --watch
```

**Test Project Location:** `C:\Users\Ruzovka\Videos\lasero-desktop\Lasero.Tests\`

**Required Dependencies:**
- `Lasero.Core` (always referenced)
- `Lasero.App` (for persistence/recovery tests)

## Best Practices

**DO:**
- Name test methods to describe exact behavior: `ApplyThenInverseRoundTrips` is better than `TestTransform`
- Keep tests small and focused: one assertion per test preferred, up to 3–4 for integration tests
- Use floating-point precision parameter for coordinate tests: `Assert.Equal(0, bounds.MinX, precision: 6)`
- Test both happy path and error cases
- Use `IDisposable` for test infrastructure cleanup (temp files, resources)
- Implement fakes/stubs for external boundaries (transport, file I/O)

**DON'T:**
- Use shared fixture classes (each test builds its own data)
- Mock domain logic (only mock I/O boundaries)
- Leave tests with side effects that affect other tests (each should be independent)
- Hardcode timeouts in assertions; use reasonable defaults (1–2 seconds)
- Test implementation details; focus on contract/behavior

## Test Execution

**Before Committing:**
```bash
dotnet test  # Must pass all tests
dotnet build # Build succeeds
```

**Do not claim success unless:**
- `dotnet test` passes with no failures
- No new warnings introduced
- Coverage gaps identified if adding significant new functionality

---

*Testing analysis: 2026-08-07*
