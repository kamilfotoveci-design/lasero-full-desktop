using System.Collections.Concurrent;
using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

/// <summary>Regression coverage for the static-audit fixes: WCO memory, feed-hold timeout, jog units,
/// work-coordinate-aware bounds, G20 refusal, import extensions, recovery origin and single-instance parsing.</summary>
public sealed class AuditFixSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-audit-" + Guid.NewGuid().ToString("N"));

    public AuditFixSafetyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ---- WCO memory ----------------------------------------------------------------------------

    [Fact]
    public void MPosOnlyReportsAfterAWcoReportDeriveWorkPositionFromRememberedOffset()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");

        transport.RaiseLine("<Idle|MPos:110.000,60.000,0.000|FS:0,0|WCO:100.000,50.000,0.000>");
        transport.RaiseLine("<Idle|MPos:120.000,70.000,0.000|FS:0,0>");

        var status = connection.LastStatus!;
        Assert.True(status.WorkCoordinateOffsetKnown);
        Assert.Equal(new Position(100, 50, 0), status.WorkCoordinateOffset);
        Assert.Equal(new Position(20, 20, 0), status.WorkPosition);
        Assert.Equal(new Position(120, 70, 0), status.MachinePosition);
    }

    [Fact]
    public void WPosOnlyReportsAfterAWcoReportDeriveMachinePositionFromRememberedOffset()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");

        transport.RaiseLine("<Idle|WPos:10.000,10.000,0.000|FS:0,0|WCO:-200.000,-100.000,0.000>");
        transport.RaiseLine("<Idle|WPos:15.000,12.000,0.000|FS:0,0>");

        var status = connection.LastStatus!;
        Assert.Equal(new Position(15, 12, 0), status.WorkPosition);
        Assert.Equal(new Position(-185, -88, 0), status.MachinePosition);
    }

    [Fact]
    public void WcoIsForgottenWhenTheControllerRestarts()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        transport.RaiseLine("<Idle|MPos:110,60,0|WCO:100,50,0>");

        transport.RaiseLine("Grbl 1.1h ['$' for help]");
        transport.RaiseLine("<Idle|MPos:120,70,0|FS:0,0>");

        var status = connection.LastStatus!;
        Assert.False(status.WorkCoordinateOffsetKnown);
        Assert.Equal(Position.Zero, status.WorkPosition);
    }

    // ---- Feed hold must not look like a dead link ------------------------------------------------

    [Fact]
    public async Task CommandStaysPendingWhileMachineKeepsReportingHold()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromMilliseconds(300) };
        connection.Connect("COM1");

        var pending = connection.SendCommandAsync("G1 X10 F100");
        Assert.True(transport.WaitForLineCount(1));
        using var cts = new CancellationTokenSource();
        var reporter = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                transport.RaiseLine("<Hold:0|MPos:1,0,0|FS:0,0|WCO:0,0,0>");
                await Task.Delay(50);
            }
        });

        await Task.Delay(1000); // well past three timeout windows
        Assert.False(pending.IsCompleted);
        Assert.Equal(GrblConnectionState.Connected, connection.State);

        transport.RaiseLine("ok");
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await reporter;
        Assert.True(result.IsOk);
        Assert.Equal(GrblConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task SilentLinkStillTimesOutAndDisconnects()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromMilliseconds(200) };
        connection.Connect("COM1");

        var pending = connection.SendCommandAsync("G1 X10 F100");
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.IsOk);
        // The failure result is published before the session finishes closing.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (connection.State != GrblConnectionState.Disconnected && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(GrblConnectionState.Disconnected, connection.State);
    }

    // ---- Jog units ----------------------------------------------------------------------------

    [Fact]
    public async Task JogForcesMillimetreUnits()
    {
        var transport = new ScriptedTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(2) };
        connection.Connect("COM1");

        var jog = connection.JogAsync(1, 0, 0, 500);
        Assert.True(transport.WaitForLineCount(1));
        transport.RaiseLine("ok");
        await jog.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("$J=G21G91 X1 Y0 Z0F500", transport.WrittenLines.First());
    }

    // ---- Preflight ----------------------------------------------------------------------------

    [Fact]
    public void NegativeWorkCoordinatesAreValidWhenTheOffsetPutsThemInsideMachineTravel()
    {
        var result = JobPreflight.Evaluate(Context(
            ["G0 X-50 Y0", "G1 X-10 Y10"], wco: new Position(-300, -100, 0), wcoKnown: true));

        Assert.DoesNotContain(result.Issues, i => i.Code is "job.outside-work-area" or "job.exceeds-reported-machine-travel");
    }

    [Fact]
    public void JobPastMachineTravelBlocksEvenInsideTheSavedWorkArea()
    {
        var result = JobPreflight.Evaluate(Context(
            ["G0 X0 Y0", "G1 X450 Y10"], wco: Position.Zero, wcoKnown: true));

        Assert.Contains(result.Issues, i => i.Code == "job.exceeds-reported-machine-travel");
        Assert.False(result.CanStart);
    }

    [Fact]
    public void UnknownWorkOffsetBlocksStartWhenMachineTravelIsReported()
    {
        var result = JobPreflight.Evaluate(Context(
            ["G0 X0 Y0", "G1 X100 Y10"], wco: Position.Zero, wcoKnown: false));

        Assert.Contains(result.Issues, i => i.Code == "machine.wco-unknown");
        Assert.False(result.CanStart);
    }

    [Fact]
    public void RawGCodeSwitchingToInchesIsRefused()
    {
        var context = Context(["G20", "G0 X1 Y0"], wco: Position.Zero, wcoKnown: true) with { IsRawGCode = true };

        var result = JobPreflight.Evaluate(context);

        Assert.Contains(result.Issues, i => i.Code == "job.imperial-units-not-allowed");
        Assert.False(result.CanStart);
    }

    // ---- Import filter ----------------------------------------------------------------------------

    [Theory]
    [InlineData(".gcode", true)]
    [InlineData(".NC", true)]
    [InlineData(".tap", true)]
    [InlineData(".txt", true)]
    [InlineData(".ngc", true)]
    [InlineData(".pdf", false)]
    [InlineData(".zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyGCodeExtensionsReachTheGCodeParser(string? extension, bool expected) =>
        Assert.Equal(expected, GCodeViewModel.IsGCodeExtension(extension));

    // ---- Recovery origin ----------------------------------------------------------------------------

    [Fact]
    public void RecoverySnapshotRemembersTheProjectItCameFrom()
    {
        var original = Path.Combine(_directory, "moje.lasero");
        File.WriteAllText(original, "x");
        var store = new ProjectRecoveryStore(Path.Combine(_directory, "recovery"));
        store.SwitchAccount("account-a");

        store.Save(new LaseroProjectFile { Name = "Moje" }, original);
        Assert.Equal(original, store.TryLoadOriginalPath());

        store.Save(new LaseroProjectFile { Name = "Moje" });
        Assert.Null(store.TryLoadOriginalPath());

        store.Save(new LaseroProjectFile { Name = "Moje" }, original);
        store.Discard();
        Assert.Null(store.TryLoadOriginalPath());
    }

    [Fact]
    public void RecoveryOriginIsIgnoredWhenTheProjectFileIsGone()
    {
        var store = new ProjectRecoveryStore(Path.Combine(_directory, "recovery"));
        store.SwitchAccount("account-a");

        store.Save(new LaseroProjectFile { Name = "Moje" }, Path.Combine(_directory, "smazano.lasero"));

        Assert.Null(store.TryLoadOriginalPath());
    }

    // ---- Single instance ----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.lasero")]
    [InlineData("C:\\Users\\a\\evil.exe")]
    public void ForwardedLaunchRejectsAnythingButAnAbsoluteProjectPath(string? line) =>
        Assert.Null(SingleInstanceGuard.ParseForwardedProjectPath(line));

    [Fact]
    public void ForwardedLaunchAcceptsAnAbsoluteProjectPath() =>
        Assert.Equal("C:\\Projects\\a.LASERO", SingleInstanceGuard.ParseForwardedProjectPath("C:\\Projects\\a.LASERO\r\n"));

    // ---- helpers ----------------------------------------------------------------------------

    private static JobPreflightContext Context(string[] lines, Position wco, bool wcoKnown) => new()
    {
        IsConnected = true,
        Document = GCodeParser.Parse(lines, "test"),
        MachineStatus = new MachineStatus
        {
            Mode = GrblMachineMode.Idle,
            WorkCoordinateOffset = wco,
            WorkCoordinateOffsetKnown = wcoKnown,
        },
        MachineStatusAge = TimeSpan.Zero,
        RequireFraming = false,
        HasFramedCurrentDocument = true,
        WorkAreaWidthMm = 500,
        WorkAreaHeightMm = 400,
        MachineMaxTravelXmm = 400,
        MachineMaxTravelYmm = 400,
    };

    private sealed class ScriptedTransport : IGrblTransport
    {
        private readonly ConcurrentQueue<string> _written = new();
        private readonly ManualResetEventSlim _signal = new();
        public bool IsOpen { get; private set; }
        public IReadOnlyCollection<string> WrittenLines => _written.ToArray();
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed;

        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;

        public void WriteLine(string text)
        {
            if (!IsOpen) throw new IOException("Port není otevřený.");
            _written.Enqueue(text);
            _signal.Set();
        }

        public void WriteRealtimeByte(byte value)
        {
            if (!IsOpen) throw new IOException("Port není otevřený.");
        }

        public void RaiseLine(string line) => LineReceived?.Invoke(line);
        public void RaiseDisconnect(Exception exception) => UnexpectedlyClosed?.Invoke(exception);

        public bool WaitForLineCount(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_written.Count < expected && DateTime.UtcNow < deadline)
                _signal.Wait(TimeSpan.FromMilliseconds(20));
            return _written.Count >= expected;
        }

        public void Dispose() => Close();
    }
}
