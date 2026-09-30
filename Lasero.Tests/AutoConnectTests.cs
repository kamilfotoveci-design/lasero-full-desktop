using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

/// <summary>
/// The automatic connect path: no model, port or baud rate chosen by the operator. The scanner is
/// faked; the controller behind a found port is the built-in simulator (or a scripted bare-bones GRBL
/// board), so nothing here opens a real COM port.
/// </summary>
public sealed class AutoConnectTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-auto-connect-tests", Guid.NewGuid().ToString("N"));

    public AutoConnectTests() => Directory.CreateDirectory(_directory);

    private sealed class FakeScanner(GrblPortScanResult? result = null, Exception? failure = null) : IGrblPortScanner
    {
        public int Calls { get; private set; }
        public Task<GrblPortScanResult> ScanAsync(IProgress<GrblPortScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            progress?.Report(new GrblPortScanProgress("COM3", 115200, 0, 1));
            if (failure is not null) throw failure;
            return Task.FromResult(result ?? new GrblPortScanResult([], []));
        }
    }

    private static GrblProbeResult Found(string port, string banner = "Grbl 1.1h ['$' for help]", string state = "Idle", int baud = 115200) =>
        new(new SerialPortCandidate(port), GrblProbeOutcome.Grbl, baud, banner, state);

    private static GrblProbeResult Missed(string port, GrblProbeOutcome outcome) => new(new SerialPortCandidate(port), outcome);

    private (ConnectionViewModel Connection, AppSettingsStore Settings, GrblConnection Machine) Create(IGrblPortScanner scanner, IGrblTransport? transport = null)
    {
        var machine = new GrblConnection(transport ?? new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero }) { CommandTimeout = TimeSpan.FromSeconds(3) };
        var settings = new AppSettingsStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"));
        var connection = new ConnectionViewModel(machine, settings, scanner) { ConnectSettleDelay = TimeSpan.Zero };
        return (connection, settings, machine);
    }

    private DeviceWizardViewModel CreateWizard(ConnectionViewModel connection, GrblConnection machine, AppSettingsStore settings) =>
        new(new DeviceScanner(new NullMachineFactory(), () => []), connection, machine, settings)
        {
            IdentificationTimeout = TimeSpan.FromSeconds(3),
        };

    [Fact]
    public async Task OneControllerFoundIsConnectedWithoutPickingAModelAndFillsInTheWorkAreaFromTheController()
    {
        // The simulator stands in for "some GRBL board on COM5"; its port name is the only one the
        // routing transport can open here.
        var scanner = new FakeScanner(new GrblPortScanResult([Found(VirtualGrblTransport.PortName)], []));
        var (connection, settings, machine) = Create(scanner);
        // A blocked hardware model left selected earlier must not stop an automatic connect.
        connection.SelectedCompatibility = MachineCompatibilityCatalog.Get("algolaser-pixi");
        Assert.False(connection.ConnectCommand.CanExecute(null));
        var wizard = CreateWizard(connection, machine, settings);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, scanner.Calls);
        Assert.Equal(DeviceWizardStep.Setup, wizard.Step);
        Assert.True(connection.IsConnected);
        Assert.Equal(MachineCompatibilityCatalog.ExistingGrblId, connection.SelectedCompatibility.Id);
        Assert.Equal(400, connection.WorkAreaWidthMm);
        Assert.Equal(400, connection.WorkAreaHeightMm);
        Assert.Equal(1000, connection.DetectedDevice?.MaxSpindleSpeed);
        machine.Dispose();
    }

    [Fact]
    public async Task SeveralControllersAreListedForAChoiceAndNothingIsConnected()
    {
        var scanner = new FakeScanner(new GrblPortScanResult([Found("COM3"), Found("COM7", "Grbl 1.1f ['$' for help]", "Alarm")], []));
        var (connection, settings, machine) = Create(scanner);
        var wizard = CreateWizard(connection, machine, settings);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceWizardStep.Results, wizard.Step);
        Assert.True(wizard.FoundSomething);
        Assert.Equal(2, wizard.FoundMachines.Count);
        Assert.Null(wizard.SelectedMachine);
        Assert.False(connection.IsConnected);
        Assert.Equal("COM7 · alarm, je nutné odemknout", wizard.FoundMachines[1].Summary);
        Assert.Equal("Grbl 1.1f ['$' for help]", wizard.FoundMachines[1].DisplayName);
        Assert.All(wizard.FoundMachines, machineFound => Assert.True(machineFound.IsAutoDetected));
        machine.Dispose();
    }

    [Fact]
    public async Task NothingFoundExplainsWhatWasTriedAndKeepsManualEntryAvailable()
    {
        var scanner = new FakeScanner(new GrblPortScanResult([], [Missed("COM3", GrblProbeOutcome.Busy), Missed("COM4", GrblProbeOutcome.NoResponse)]));
        var (connection, settings, machine) = Create(scanner);
        var wizard = CreateWizard(connection, machine, settings);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.True(wizard.FoundNothing);
        Assert.Contains("COM3 - port drží jiný program", wizard.ScanReport);
        Assert.Contains("COM4 - bez odpovědi", wizard.ScanReport);
        Assert.Contains("Prohledáno portů COM: 2", wizard.NothingFoundSummary);
        Assert.DoesNotContain('?', wizard.NothingFoundSummary);
        Assert.DoesNotContain('!', wizard.NothingFoundSummary);
        // Manual entry is untouched by the failed search.
        Assert.NotEmpty(connection.AvailablePorts);
        wizard.BackCommand.Execute(null);
        Assert.Equal(DeviceWizardStep.Intro, wizard.Step);
        machine.Dispose();
    }

    [Fact]
    public async Task NoComPortsAtAllPointsAtTheCableAndDriver()
    {
        var (connection, settings, machine) = Create(new FakeScanner());
        var wizard = CreateWizard(connection, machine, settings);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.True(wizard.FoundNothing);
        Assert.Contains("žádný port COM", wizard.NothingFoundSummary);
        Assert.Contains("CH340", wizard.NothingFoundSummary);
        machine.Dispose();
    }

    [Fact]
    public async Task AScannerFailureLandsOnTheNothingFoundScreenInsteadOfCrashing()
    {
        var (connection, settings, machine) = Create(new FakeScanner(failure: new InvalidOperationException("boom")));
        var wizard = CreateWizard(connection, machine, settings);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.True(wizard.FoundNothing);
        Assert.False(connection.IsConnected);
        Assert.False(connection.IsDetecting);
        machine.Dispose();
    }

    [Fact]
    public async Task ACancelledSearchNeverConnects()
    {
        var scanner = new FakeScanner(new GrblPortScanResult([Found(VirtualGrblTransport.PortName)], []));
        var (connection, settings, machine) = Create(scanner);
        var wizard = CreateWizard(connection, machine, settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.DetectAsync(null, cancellation.Token));

        Assert.False(connection.IsConnected);
        Assert.False(connection.IsDetecting);
        _ = wizard;
        machine.Dispose();
    }

    [Fact]
    public async Task StatusStripConnectScansWhenNoPortWasChosenBeforeAndConnectsToTheSingleController()
    {
        var scanner = new FakeScanner(new GrblPortScanResult([Found(VirtualGrblTransport.PortName)], []));
        var (connection, _, machine) = Create(scanner);

        await connection.SmartConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, scanner.Calls);
        Assert.True(connection.IsConnected);
        machine.Dispose();
    }

    [Fact]
    public async Task StatusStripConnectHandsOverToTheWizardWhenSeveralOrNoneAreFound()
    {
        var scanner = new FakeScanner(new GrblPortScanResult([Found("COM3"), Found("COM4")], []));
        var (connection, _, machine) = Create(scanner);
        var handovers = 0;
        GrblPortScanResult? handed = null;
        connection.AutoConnectNeedsWizard += scan => { handovers++; handed = scan; };

        await connection.SmartConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, handovers);
        Assert.Equal(2, handed?.Grbl.Count);
        Assert.False(connection.IsConnected);
        machine.Dispose();
    }

    [Fact]
    public async Task TheWizardShowsTheStripsSearchResultInsteadOfOpeningEveryPortAgain()
    {
        var scanner = new FakeScanner();
        var (connection, _, machine) = Create(scanner);
        var settings = new AppSettingsStore(Path.Combine(_directory, "wizard.json"));
        var wizard = CreateWizard(connection, machine, settings);
        wizard.AutoStartRequested = true;
        wizard.PreScanResult = new GrblPortScanResult([Found("COM3"), Found("COM4")], []);

        await wizard.AutoConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, scanner.Calls);
        Assert.Equal(2, wizard.FoundMachines.Count);
        Assert.Equal(DeviceWizardStep.Results, wizard.Step);
        Assert.Null(wizard.PreScanResult);
        machine.Dispose();
    }

    [Fact]
    public async Task StatusStripConnectReusesAPreviouslyUsedPortWithoutScanning()
    {
        var scanner = new FakeScanner();
        var (connection, settings, machine) = Create(scanner);
        settings.Current.Device.LastConnectedPort = VirtualGrblTransport.PortName;

        await connection.SmartConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, scanner.Calls);
        Assert.True(connection.IsConnected);
        machine.Dispose();
    }

    [Fact]
    public void ConnectingToTheSimulatorIsNeverRememberedAsAPreviouslyChosenPort()
    {
        var (connection, settings, machine) = Create(new FakeScanner());
        connection.SelectedPort = VirtualGrblTransport.PortName;

        connection.ConnectCommand.Execute(null);
        Assert.True(connection.IsConnected);

        Assert.Null(settings.Current.Device.LastConnectedPort);
        machine.Dispose();
    }

    [Fact]
    public async Task AnUnknownBoardThatReportsNoTravelOrSpindleSpeedStillConnectsAndTheGapsAreSurfaced()
    {
        var (connection, _, machine) = Create(new FakeScanner(), new BareBoardTransport());
        connection.SelectedPort = VirtualGrblTransport.PortName;

        connection.ConnectCommand.Execute(null);
        for (var i = 0; i < 60 && connection.DetectedDevice is null; i++) await Task.Delay(50);

        Assert.True(connection.IsConnected);
        Assert.NotNull(connection.DetectedDevice);
        Assert.Null(connection.DetectedDevice!.MaxSpindleSpeed);
        Assert.Contains("$30", connection.IdentificationMessage);
        Assert.Contains("$130", connection.IdentificationMessage);
        Assert.Null(connection.ConnectionError);
        machine.Dispose();
    }

    [Fact]
    public void TheDefaultModelEntryDescribesAutomaticDetectionAndTheOtherModelsStillBlockDirectConnect()
    {
        var generic = MachineCompatibilityCatalog.Get(MachineCompatibilityCatalog.ExistingGrblId);
        Assert.Equal("Obecný GRBL - zjištěno automaticky", generic.DisplayName);
        Assert.True(generic.AllowsDirectConnection);
        Assert.False(MachineCompatibilityCatalog.Get("xtool-s1").AllowsDirectConnection);
    }

    /// <summary>A minimal GRBL board: banner, status, and a settings list without $30, $130 or $131.</summary>
    private sealed class BareBoardTransport : IGrblTransport
    {
        public bool IsOpen { get; private set; }
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed { add { } remove { } }

        public void Open(string portName, int baudRate)
        {
            IsOpen = true;
            LineReceived?.Invoke("Grbl 1.1f ['$' for help]");
        }

        public void Close() => IsOpen = false;

        public void WriteLine(string text)
        {
            if (text == "$$")
            {
                foreach (var line in new[] { "$0=10", "$1=25", "$2=0", "$3=0", "$32=1" }) LineReceived?.Invoke(line);
            }
            LineReceived?.Invoke("ok");
        }

        public void WriteRealtimeByte(byte value)
        {
            if (value == GrblRealtimeCommand.StatusReportQuery)
                LineReceived?.Invoke("<Idle|MPos:0.000,0.000,0.000|FS:0,0>");
        }

        public void Dispose() => Close();
    }

    private sealed class NullMachineFactory : ILaserMachineFactory
    {
        public ILaserMachine Create() => throw new InvalidOperationException("The automatic path never uses the passive scanner.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
