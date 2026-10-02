using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

/// <summary>
/// Beam-safety audit (docs/beam-safety-audit.md). Every test drives the production connection,
/// job runner or JogViewModel against the hardware-free simulator and asserts on the simulated
/// controller's spindle/laser state, which is what decides whether a real beam stays lit.
/// </summary>
public sealed class BeamSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-beam-safety-tests", Guid.NewGuid().ToString("N"));

    public BeamSafetyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // ---- Connection lifecycle -------------------------------------------------------------------

    [Theory]
    [InlineData("M3 S100")]
    [InlineData("M4 S500")]
    public async Task UserDisconnectWithBeamOnTurnsTheBeamOff(string spindleOn)
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        Assert.True((await machine.SendCommandAsync(spindleOn)).IsOk);
        Assert.True(transport.Sim.IsBeamOn);

        machine.Disconnect();

        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task DisposingTheMachineAtProcessExitTurnsTheBeamOff()
    {
        var (machine, transport) = ConnectedMachine();
        Assert.True((await machine.SendCommandAsync("M3 S100")).IsOk);

        machine.Dispose();

        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task DisconnectAfterAcknowledgedM5DoesNotNeedAReset()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        await machine.SendCommandAsync("M3 S100");
        await machine.SendCommandAsync("M5");
        transport.Realtime.Clear();

        machine.Disconnect();

        Assert.DoesNotContain(GrblRealtimeCommand.SoftReset, transport.Realtime);
    }

    [Fact]
    public async Task DisconnectAfterMidJobDrainStillResetsWhenM3WasRewrittenAfterM5()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        await machine.SendCommandAsync("M3 S100");
        await machine.SendCommandAsync("M5");
        await machine.SendCommandAsync("M4 S50");

        machine.Disconnect();

        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task CommandTimeoutDisconnectStillTriesToStopABeamOnAnAliveButSilentPort()
    {
        var silent = new SilentTransport();
        using var machine = new GrblConnection(silent) { CommandTimeout = TimeSpan.FromMilliseconds(200) };
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.Disconnected += reason => disconnected.TrySetResult(reason);
        machine.Connect("COM-TEST");

        var result = await machine.SendCommandAsync("M3 S100");
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.IsOk);
        Assert.Contains(GrblRealtimeCommand.SoftReset, silent.RealtimeBeforeClose);
    }

    [Fact]
    public async Task UnexpectedCableLossFailsCleanlyWithoutThrowing()
    {
        // A dead port cannot be written to: no software can turn the beam off after this point.
        // The requirement is only that the app notices, reports it and does not hang or crash.
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        await machine.SendCommandAsync("M3 S100");
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.Disconnected += reason => disconnected.TrySetResult(reason);

        transport.Sim.InjectDisconnect();

        var reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(reason);
        Assert.Equal(GrblConnectionState.Disconnected, machine.State);
    }

    // ---- Job runner -----------------------------------------------------------------------------

    [Fact]
    public async Task JobLineErrorStopsMachineAndLeavesBeamOff()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        transport.ErrorFor = line => line == "BAD" ? 3 : null;
        var runner = new GCodeJobRunner(machine);

        await runner.RunAsync(["M4 S500", "G1 X5 S500 F1000", "BAD", "G1 X10"]);

        Assert.Equal(JobRunState.Faulted, runner.State);
        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task JobCancellationLeavesBeamOff()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        transport.Sim.ResponseDelay = TimeSpan.FromMilliseconds(20);
        var runner = new GCodeJobRunner(machine);
        using var cancellation = new CancellationTokenSource();
        var lines = new List<string> { "M4 S500" };
        lines.AddRange(Enumerable.Range(1, 200).Select(i => $"G1 X{i} S500 F1000"));

        var run = runner.RunAsync(lines, cancellationToken: cancellation.Token);
        await Task.Delay(150);
        cancellation.Cancel();
        await run;

        Assert.Equal(JobRunState.Aborted, runner.State);
        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task OperatorAbortLeavesBeamOff()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        transport.Sim.ResponseDelay = TimeSpan.FromMilliseconds(20);
        var runner = new GCodeJobRunner(machine);
        var lines = new List<string> { "M4 S500" };
        lines.AddRange(Enumerable.Range(1, 200).Select(i => $"G1 X{i} S500 F1000"));

        var run = runner.RunAsync(lines);
        await Task.Delay(150);
        runner.Abort();
        await run;

        Assert.Equal(JobRunState.Aborted, runner.State);
        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task ConnectionLostDuringJobStopsTheRunnerAndTurnsTheBeamOffWhileThePortIsStillWritable()
    {
        var (machine, transport) = ConnectedMachine();
        using var _ = machine;
        transport.Sim.ResponseDelay = TimeSpan.FromMilliseconds(20);
        var runner = new GCodeJobRunner(machine);
        var lines = new List<string> { "M4 S500" };
        lines.AddRange(Enumerable.Range(1, 200).Select(i => $"G1 X{i} S500 F1000"));

        var run = runner.RunAsync(lines);
        await Task.Delay(150);
        machine.Disconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobRunState.Faulted, runner.State);
        Assert.False(transport.Sim.IsBeamOn);
    }

    // ---- Positioning laser (hold-to-fire) -------------------------------------------------------

    [Fact]
    public async Task ReleaseTurnsThePositioningLaserOff()
    {
        var (machine, transport, jog) = ReadyJog("release.json");
        using var _ = machine;
        await jog.StartPositioningLaserAsync();
        Assert.True(transport.Sim.IsBeamOn);

        await jog.StopPositioningLaserAsync();

        Assert.False(transport.Sim.IsBeamOn);
        Assert.False(jog.IsPositioningLaserOn);
    }

    [Fact]
    public async Task DisconnectWhileThePositioningLaserIsLitTurnsItOff()
    {
        var (machine, transport, jog) = ReadyJog("disconnect-lit.json");
        using var _ = machine;
        await jog.StartPositioningLaserAsync();
        Assert.True(jog.IsPositioningLaserOn);

        machine.Disconnect();

        Assert.False(transport.Sim.IsBeamOn);
        // ConnectionStateChanged is marshalled to the WPF dispatcher; the simulator beam is
        // synchronously off before Disconnect returns, while the bound view-model flag settles next.
        await WaitUntilAsync(() => !jog.IsPositioningLaserOn, TimeSpan.FromSeconds(1));
        Assert.False(jog.IsPositioningLaserOn);
    }

    [Fact]
    public async Task ReleaseWhoseM5IsRejectedEscalatesToASoftReset()
    {
        var (machine, transport, jog) = ReadyJog("m5-rejected.json");
        using var _ = machine;
        await jog.StartPositioningLaserAsync();
        transport.ErrorFor = line => line == "M5" ? 20 : null;

        await jog.StopPositioningLaserAsync();

        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task ReleaseWhoseM5IsNeverAnsweredEscalatesToASoftReset()
    {
        var (machine, transport, jog) = ReadyJog("m5-silent.json");
        using var _ = machine;
        machine.CommandTimeout = TimeSpan.FromMilliseconds(300);
        await jog.StartPositioningLaserAsync();
        transport.SwallowLine = line => line == "M5";

        await jog.StopPositioningLaserAsync();

        Assert.False(transport.Sim.IsBeamOn);
    }

    [Fact]
    public async Task LitPositioningLaserIsReleasedWhenStatusReportsGoStale()
    {
        var (machine, transport, jog) = ReadyJog("stale-status.json");
        using var _ = machine;
        jog.PositioningLaserWatchdogInterval = TimeSpan.FromMilliseconds(100);
        await jog.StartPositioningLaserAsync();
        Assert.True(transport.Sim.IsBeamOn);

        // No further RequestStatus: the last status ages past the two-second freshness window.
        await WaitUntilAsync(() => !transport.Sim.IsBeamOn, TimeSpan.FromSeconds(5));

        Assert.False(transport.Sim.IsBeamOn);
        Assert.False(jog.IsPositioningLaserOn);
    }

    [Fact]
    public async Task LitPositioningLaserStaysOnWhileStatusIsFresh()
    {
        var (machine, transport, jog) = ReadyJog("fresh-status.json");
        using var _ = machine;
        jog.PositioningLaserWatchdogInterval = TimeSpan.FromMilliseconds(50);
        await jog.StartPositioningLaserAsync();

        for (var i = 0; i < 6; i++)
        {
            machine.RequestStatus();
            await Task.Delay(100);
        }

        Assert.True(transport.Sim.IsBeamOn);
        await jog.StopPositioningLaserAsync();
        Assert.False(transport.Sim.IsBeamOn);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static (GrblConnection Machine, ScriptedTransport Transport) ConnectedMachine()
    {
        var transport = new ScriptedTransport();
        var machine = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(3) };
        machine.Connect(VirtualGrblTransport.PortName);
        machine.RequestStatus();
        return (machine, transport);
    }

    private (GrblConnection Machine, ScriptedTransport Transport, JogViewModel Jog) ReadyJog(string settingsFile)
    {
        var (machine, transport) = ConnectedMachine();
        var jog = new JogViewModel(machine, new AppSettingsStore(Path.Combine(_directory, settingsFile)));
        jog.ConfigurePositioningLaser(maximumSValue: 1000, laserModeEnabled: true);
        machine.RequestStatus();
        Assert.True(jog.CanUsePositioningLaser);
        return (machine, transport, jog);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    /// <summary>Simulator wrapper that can reject or swallow chosen lines and records realtime bytes.</summary>
    private sealed class ScriptedTransport : IGrblTransport
    {
        public VirtualGrblTransport Sim { get; } = new() { ResponseDelay = TimeSpan.Zero };
        public Func<string, int?>? ErrorFor { get; set; }
        public Func<string, bool>? SwallowLine { get; set; }
        public List<byte> Realtime { get; } = [];

        public bool IsOpen => Sim.IsOpen;
        public ScriptedTransport() => Sim.LineReceived += line => LineReceived?.Invoke(line);
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed { add => Sim.UnexpectedlyClosed += value; remove => Sim.UnexpectedlyClosed -= value; }
        public void Open(string portName, int baudRate) => Sim.Open(portName, baudRate);
        public void Close() => Sim.Close();
        public void Dispose() => Sim.Dispose();

        public void WriteLine(string text)
        {
            if (SwallowLine?.Invoke(text.Trim()) == true) return;
            if (ErrorFor?.Invoke(text.Trim()) is { } code)
            {
                LineReceived?.Invoke($"error:{code}");
                return;
            }
            Sim.WriteLine(text);
        }

        public void WriteRealtimeByte(byte value)
        {
            lock (Realtime) Realtime.Add(value);
            Sim.WriteRealtimeByte(value);
        }
    }

    /// <summary>Open port that never answers: models a controller that hangs mid-command.</summary>
    private sealed class SilentTransport : IGrblTransport
    {
        public List<byte> RealtimeBeforeClose { get; } = [];
        public bool IsOpen { get; private set; }
        public event Action<string>? LineReceived { add { } remove { } }
        public event Action<Exception>? UnexpectedlyClosed { add { } remove { } }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;
        public void WriteLine(string text) { }
        public void WriteRealtimeByte(byte value)
        {
            if (IsOpen) lock (RealtimeBeforeClose) RealtimeBeforeClose.Add(value);
        }
        public void Dispose() => IsOpen = false;
    }
}
