using System.IO;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

/// <summary>
/// The automatic connect probes every serial port the operator did not choose, so what it may write is
/// part of the safety contract: only the realtime status query and the build-info request, and only ever
/// against ports that have already answered like GRBL (build info) or that have stayed silent (status).
/// The fakes here record every byte so the tests can assert on exactly that.
/// </summary>
public sealed class GrblPortScannerTests
{
    private const string Banner = "Grbl 1.1h ['$' for help]";

    private sealed class PortScript
    {
        public Func<int, bool> AnswersAtBaud { get; init; } = _ => true;
        public bool BannerOnOpen { get; init; }
        public Exception? OpenThrows { get; init; }
        public ManualResetEventSlim? OpenBlocksUntil { get; init; }
        public string[]? NonGrblReply { get; init; }
        public string Status { get; init; } = "<Idle|MPos:0.000,0.000,0.000|FS:0,0>";
        public string[] BuildInfo { get; init; } = ["[VER:1.1h.20190825:]", "[OPT:V,15,128]", "ok"];
    }

    private sealed class Recorder
    {
        public List<string> Writes { get; } = [];
        public List<string> Opens { get; } = [];
        public List<string> Closes { get; } = [];
        public int Created;
    }

    private sealed class FakeFactory(Dictionary<string, PortScript> scripts, Recorder recorder) : IGrblTransportFactory
    {
        public IGrblTransport Create()
        {
            Interlocked.Increment(ref recorder.Created);
            return new FakeTransport(scripts, recorder);
        }
    }

    private sealed class FakeTransport(Dictionary<string, PortScript> scripts, Recorder recorder) : IGrblTransport
    {
        private PortScript? _script;
        private int _baud;
        private string _port = "";

        public bool IsOpen { get; private set; }
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed { add { } remove { } }

        public void Open(string portName, int baudRate)
        {
            lock (recorder) recorder.Opens.Add($"{portName}@{baudRate}");
            _script = scripts[portName];
            _port = portName;
            _baud = baudRate;
            _script.OpenBlocksUntil?.Wait(TimeSpan.FromSeconds(5));
            if (_script.OpenThrows is not null) throw _script.OpenThrows;
            IsOpen = true;
            if (_script.BannerOnOpen && _script.AnswersAtBaud(baudRate)) LineReceived?.Invoke(Banner);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            lock (recorder) recorder.Closes.Add(_port);
        }

        public void WriteLine(string text)
        {
            lock (recorder) recorder.Writes.Add($"line:{text}@{_port}");
            if (text == "$I" && _script!.NonGrblReply is null && _script.AnswersAtBaud(_baud))
                foreach (var line in _script.BuildInfo) LineReceived?.Invoke(line);
        }

        public void WriteRealtimeByte(byte value)
        {
            lock (recorder) recorder.Writes.Add($"rt:{value:X2}@{_port}");
            if (value != GrblRealtimeCommand.StatusReportQuery) return;
            if (_script!.NonGrblReply is { } garbage)
            {
                foreach (var line in garbage) LineReceived?.Invoke(line);
                return;
            }
            if (_script.AnswersAtBaud(_baud)) LineReceived?.Invoke(_script.Status);
        }

        public void Dispose() => Close();
    }

    private sealed class FakePorts(params SerialPortCandidate[] ports) : ISerialPortEnumerator
    {
        public IReadOnlyList<SerialPortCandidate> GetPorts() => ports;
    }

    private static GrblPortScanner Scanner(Dictionary<string, PortScript> scripts, Recorder recorder, params SerialPortCandidate[] ports) =>
        new(new FakePorts(ports), new FakeFactory(scripts, recorder))
        {
            OpenTimeout = TimeSpan.FromMilliseconds(200),
            BannerWait = TimeSpan.FromMilliseconds(40),
            ResponseWait = TimeSpan.FromMilliseconds(60),
        };

    private static SerialPortCandidate Usb(string name) => new(name, "USB-SERIAL CH340 (" + name + ")", SerialPortKind.UsbSerialAdapter);
    private static SerialPortCandidate Plain(string name) => new(name);

    [Fact]
    public async Task UsbAdaptersAreProbedBeforeOtherPortsAndBluetoothIsNeverOpened()
    {
        var recorder = new Recorder();
        var bt = new SerialPortCandidate("COM3", "Standard Serial over Bluetooth link (COM3)", SerialPortKind.Bluetooth);
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM1"] = new() { AnswersAtBaud = _ => false },
            ["COM3"] = new(),
            ["COM8"] = new() { AnswersAtBaud = _ => false },
            ["COM12"] = new(),
        };
        var scanner = Scanner(scripts, recorder, Plain("COM1"), bt, Usb("COM12"), Plain("COM8"), Usb("COM12"));

        var result = await scanner.ScanAsync();

        var portsInOrder = recorder.Opens.Select(o => o.Split('@')[0]).Distinct().ToArray();
        Assert.Equal(["COM12", "COM1", "COM8"], portsInOrder);
        Assert.DoesNotContain(recorder.Opens, o => o.StartsWith("COM3@", StringComparison.Ordinal));
        Assert.Contains(result.Others, r => r.PortName == "COM3" && r.Outcome == GrblProbeOutcome.Skipped);
        Assert.Equal("COM12", Assert.Single(result.Grbl).PortName);
    }

    [Fact]
    public async Task FallsBackThroughTheBaudListUntilThePortAnswers()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript> { ["COM4"] = new() { AnswersAtBaud = baud => baud == 250000 } };

        var result = await Scanner(scripts, recorder, Usb("COM4")).ScanAsync();

        Assert.Equal(["COM4@115200", "COM4@250000"], recorder.Opens);
        var found = Assert.Single(result.Grbl);
        Assert.Equal(250000, found.BaudRate);
        Assert.Equal("Idle", found.StateLabel);
    }

    [Fact]
    public async Task ASilentPortIsReportedAsNoResponseAfterEveryBaudWasTriedAndOnlyGetsTheStatusQuery()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript> { ["COM5"] = new() { AnswersAtBaud = _ => false } };

        var result = await Scanner(scripts, recorder, Usb("COM5")).ScanAsync();

        Assert.Empty(result.Grbl);
        var other = Assert.Single(result.Others);
        Assert.Equal(GrblProbeOutcome.NoResponse, other.Outcome);
        Assert.Equal(["COM5@115200", "COM5@250000", "COM5@57600", "COM5@9600"], recorder.Opens);
        Assert.All(recorder.Writes, write => Assert.Equal("rt:3F@COM5", write));
        Assert.Equal(4, recorder.Closes.Count);
    }

    [Fact]
    public async Task ANonGrblDeviceIsIgnoredAndNeverAsksForBuildInfo()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM6"] = new() { NonGrblReply = ["ok T:21.0 /0.0 B:20.1 /0.0", "Marlin 2.1.2"] },
        };

        var result = await Scanner(scripts, recorder, Usb("COM6")).ScanAsync();

        Assert.Empty(result.Grbl);
        Assert.Equal(GrblProbeOutcome.NotGrbl, Assert.Single(result.Others).Outcome);
        Assert.DoesNotContain(recorder.Writes, w => w.StartsWith("line:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABusyPortIsSkippedWithoutTryingOtherBaudRates()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM7"] = new() { OpenThrows = new UnauthorizedAccessException("Access to the port 'COM7' is denied.") },
            ["COM9"] = new(),
        };

        var result = await Scanner(scripts, recorder, Usb("COM7"), Usb("COM9")).ScanAsync();

        Assert.Single(recorder.Opens, o => o.StartsWith("COM7@", StringComparison.Ordinal));
        Assert.Contains(result.Others, r => r.PortName == "COM7" && r.Outcome == GrblProbeOutcome.Busy);
        Assert.Equal("COM9", Assert.Single(result.Grbl).PortName);
    }

    [Fact]
    public async Task APortWhoseOpenHangsIsAbandonedAndReportedAsOpenFailed()
    {
        var recorder = new Recorder();
        using var release = new ManualResetEventSlim(false);
        var scripts = new Dictionary<string, PortScript> { ["COM10"] = new() { OpenBlocksUntil = release } };

        var result = await Scanner(scripts, recorder, Plain("COM10")).ScanAsync();
        release.Set();

        Assert.Equal(GrblProbeOutcome.OpenFailed, Assert.Single(result.Others).Outcome);
        Assert.Single(recorder.Opens);
    }

    [Fact]
    public async Task SeveralControllersAreAllReportedWithPortFirmwareAndState()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM3"] = new() { BannerOnOpen = true },
            ["COM4"] = new() { Status = "<Alarm|MPos:0.000,0.000,0.000|FS:0,0>" },
        };

        var result = await Scanner(scripts, recorder, Usb("COM4"), Usb("COM3")).ScanAsync();

        Assert.Equal(["COM3", "COM4"], result.Grbl.Select(r => r.PortName).ToArray());
        Assert.Equal(Banner, result.Grbl[0].FirmwareBanner);
        Assert.Equal("Grbl 1.1h.20190825", result.Grbl[1].FirmwareBanner);
        Assert.Equal("Alarm", result.Grbl[1].StateLabel);
    }

    [Fact]
    public async Task NothingFoundYieldsAnEmptyGrblListAndAReasonForEveryPort()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM1"] = new() { AnswersAtBaud = _ => false },
            ["COM2"] = new() { OpenThrows = new IOException("The device is not functioning") },
        };

        var result = await Scanner(scripts, recorder, Plain("COM1"), Plain("COM2")).ScanAsync();

        Assert.Empty(result.Grbl);
        Assert.Equal(2, result.PortsExamined);
        Assert.Equal([GrblProbeOutcome.NoResponse, GrblProbeOutcome.OpenFailed], result.Others.Select(r => r.Outcome).ToArray());
    }

    [Fact]
    public async Task NoPortsAtAllIsNotAnError()
    {
        var result = await Scanner([], new Recorder()).ScanAsync();

        Assert.Empty(result.Grbl);
        Assert.Empty(result.Others);
    }

    [Fact]
    public async Task TheProbeOnlyEverWritesTheStatusQueryAndBuildInfo()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM1"] = new() { BannerOnOpen = true },
            ["COM2"] = new(),
            ["COM3"] = new() { AnswersAtBaud = _ => false },
            ["COM4"] = new() { NonGrblReply = ["hello"] },
            ["COM5"] = new() { Status = "<Run|MPos:1.000,0.000,0.000|FS:500,0>" },
            ["COM6"] = new() { AnswersAtBaud = baud => baud == 9600 },
        };

        await Scanner(scripts, recorder, Usb("COM1"), Usb("COM2"), Plain("COM3"), Plain("COM4"), Plain("COM5"), Plain("COM6")).ScanAsync();

        Assert.NotEmpty(recorder.Writes);
        foreach (var write in recorder.Writes)
        {
            var kind = write.Split('@')[0];
            Assert.True(kind is "rt:3F" or "line:$I", $"Unexpected probe write: {write}");
        }
        // Never a soft reset, feed hold, cycle start, jog cancel, homing, unlock or settings write.
        Assert.DoesNotContain(recorder.Writes, w => w.StartsWith("rt:18", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunningMachineIsNeverAskedForBuildInfo()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript> { ["COM5"] = new() { Status = "<Run|MPos:1.000,0.000,0.000|FS:500,0>" } };

        var result = await Scanner(scripts, recorder, Usb("COM5")).ScanAsync();

        Assert.Equal("Run", Assert.Single(result.Grbl).StateLabel);
        Assert.DoesNotContain(recorder.Writes, w => w.StartsWith("line:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryOpenedPortIsClosedAgain()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM1"] = new(),
            ["COM2"] = new() { AnswersAtBaud = _ => false },
        };

        await Scanner(scripts, recorder, Usb("COM1"), Usb("COM2")).ScanAsync();

        Assert.Equal(recorder.Opens.Count, recorder.Closes.Count);
    }

    [Fact]
    public async Task CancellingStopsTheScanBetweenPorts()
    {
        var recorder = new Recorder();
        var scripts = new Dictionary<string, PortScript>
        {
            ["COM1"] = new() { AnswersAtBaud = _ => false },
            ["COM2"] = new(),
        };
        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scanner(scripts, recorder, Usb("COM1"), Usb("COM2")).ScanAsync(progress, cancellation.Token));

        Assert.DoesNotContain(recorder.Opens, o => o.StartsWith("COM2@", StringComparison.Ordinal));
    }

    private sealed class SyncProgress(Action<GrblPortScanProgress> report) : IProgress<GrblPortScanProgress>
    {
        public void Report(GrblPortScanProgress value) => report(value);
    }

    [Theory]
    [InlineData("USB-SERIAL CH340 (COM3)", SerialPortKind.UsbSerialAdapter)]
    [InlineData("Silicon Labs CP210x USB to UART Bridge (COM4)", SerialPortKind.UsbSerialAdapter)]
    [InlineData("USB Serial Port (COM5)", SerialPortKind.UsbSerialAdapter)]
    [InlineData("Standard Serial over Bluetooth link (COM6)", SerialPortKind.Bluetooth)]
    [InlineData("Communications Port (COM1)", SerialPortKind.Unknown)]
    [InlineData(null, SerialPortKind.Unknown)]
    public void ClassifiesWindowsDeviceDescriptions(string? description, SerialPortKind expected) =>
        Assert.Equal(expected, SerialPortCandidate.Classify(description));

    [Fact]
    public async Task FindsTheBuiltInSimulatorTransportAsAGrblControllerWithoutAnyHardware()
    {
        var factory = new SimulatorFactory();
        var scanner = new GrblPortScanner(new FakePorts(new SerialPortCandidate(VirtualGrblTransport.PortName)), factory)
        {
            BannerWait = TimeSpan.FromMilliseconds(500),
            ResponseWait = TimeSpan.FromMilliseconds(500),
        };

        var result = await scanner.ScanAsync();

        var found = Assert.Single(result.Grbl);
        Assert.Contains("Grbl 1.1h", found.FirmwareBanner);
    }

    private sealed class SimulatorFactory : IGrblTransportFactory
    {
        public IGrblTransport Create() => new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
    }
}
