using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class DeviceScannerTests
{
    private static readonly string[] GrblSettings =
    [
        "$0=10", "$1=25", "$32=1", "$110=6000", "$130=400.000", "$131=400.000", "$132=80.000",
    ];

    [Fact]
    public async Task ScanReportsThePortAndBaudRateThatAnswered()
    {
        var factory = new FakeMachineFactory
        {
            Responders = { ["COM7"] = (57600, GrblSettings) },
        };
        var scanner = new DeviceScanner(factory, () => ["COM3", "COM7"]) { IncludeSimulator = false };

        var found = await scanner.ProbeSelectedPortAsync("COM7", 57600);

        var machine = Assert.IsType<DiscoveredMachine>(found);
        Assert.Equal("COM7", machine.PortName);
        Assert.Equal(57600, machine.BaudRate);
        Assert.Equal(400, machine.Profile.MaxTravelXmm);
        Assert.True(machine.ReportsWorkArea);
        Assert.False(machine.LaserModeIsOff);
    }

    /// <summary>A board at the wrong baud rate answers with a line or two of garbage. Treating that as
    /// a laser would hand the operator a machine that cannot be driven.</summary>
    [Fact]
    public async Task ANoisyPortIsNotReportedAsAMachine()
    {
        var factory = new FakeMachineFactory
        {
            Responders = { ["COM3"] = (115200, ["garbage", "$0=10"]) },
        };
        var scanner = new DeviceScanner(factory, () => ["COM3"]) { IncludeSimulator = false };

        Assert.Null(await scanner.ProbeSelectedPortAsync("COM3", 115200));
    }

    [Fact]
    public async Task EveryPortIsClosedAgainWhetherOrNotItAnswered()
    {
        var factory = new FakeMachineFactory
        {
            Responders = { ["COM7"] = (115200, GrblSettings) },
        };
        var scanner = new DeviceScanner(factory, () => ["COM3", "COM7"]) { IncludeSimulator = false };

        await scanner.ProbeSelectedPortAsync("COM7", 115200);

        Assert.All(factory.Created, machine => Assert.False(machine.IsOpen));
        Assert.All(factory.Created, machine => Assert.True(machine.IsDisposed));
    }

    [Fact]
    public async Task ScanningOnlySendsTheSettingsQuery()
    {
        var factory = new FakeMachineFactory
        {
            Responders = { ["COM7"] = (115200, GrblSettings) },
        };
        var scanner = new DeviceScanner(factory, () => ["COM7"]) { IncludeSimulator = false };

        await scanner.ProbeSelectedPortAsync("COM7", 115200);

        Assert.All(factory.Created, machine => Assert.Empty(machine.SentCommands));
    }

    [Fact]
    public async Task DiscoveryNeverOpensPhysicalPorts()
    {
        var factory = new FakeMachineFactory();
        var scanner = new DeviceScanner(factory, () => ["COM3", "COM7"]) { IncludeSimulator = false };
        Assert.Empty(await scanner.ScanAsync());
        Assert.Empty(factory.Created);
    }
    private sealed class FakeMachineFactory : ILaserMachineFactory
    {
        public Dictionary<string, (int BaudRate, string[] Settings)> Responders { get; } = new();
        public List<FakeMachine> Created { get; } = new();

        public ILaserMachine Create()
        {
            var machine = new FakeMachine(Responders);
            Created.Add(machine);
            return machine;
        }
    }

    private sealed class FakeMachine : ILaserMachine
    {
        private readonly Dictionary<string, (int BaudRate, string[] Settings)> _responders;
        private string? _port;
        private int _baudRate;

        public FakeMachine(Dictionary<string, (int BaudRate, string[] Settings)> responders) => _responders = responders;

        public bool IsOpen { get; private set; }
        public bool IsDisposed { get; private set; }
        public List<string> SentCommands { get; } = new();

        public GrblConnectionState State => IsOpen ? GrblConnectionState.Connected : GrblConnectionState.Disconnected;
        public MachineStatus? LastStatus => null;
        public DateTime? LastStatusReceivedUtc => null;
        public string? FirmwareBanner => IsOpen && Answers ? "Grbl 1.1f ['$' for help]" : null;
        public MachineAlert? ActiveAlert => null;
        public LaserMachineDisplayState DisplayState => LaserMachineDisplayState.Idle;

        private bool Answers => _port is not null &&
            _responders.TryGetValue(_port, out var responder) && responder.BaudRate == _baudRate;

        public event Action<GrblConnectionState>? ConnectionStateChanged;
        public event Action<MachineStatus>? StatusUpdated;
        public event Action<string>? RawLineReceived;
        public event Action<int, string>? ErrorReceived;
        public event Action<int, string>? AlarmReceived;
        public event Action<string>? FeedbackMessageReceived;
        public event Action<string>? Connected;
        public event Action<Exception?>? Disconnected;
        public event Action<MachineAlert?>? AlertChanged;

        public void Connect(string portName, int baudRate = 115200)
        {
            if (IsOpen) throw new InvalidOperationException("Already connected.");
            _port = portName;
            _baudRate = baudRate;
            IsOpen = true;
            ConnectionStateChanged?.Invoke(GrblConnectionState.Connected);
            Connected?.Invoke(FirmwareBanner ?? string.Empty);
        }

        public void Disconnect()
        {
            IsOpen = false;
            Disconnected?.Invoke(null);
        }

        public Task<IReadOnlyList<string>> QuerySettingsAsync()
        {
            if (!IsOpen) throw new InvalidOperationException("Not connected.");
            if (!Answers) return Task.FromException<IReadOnlyList<string>>(new TimeoutException("No answer."));
            return Task.FromResult<IReadOnlyList<string>>(_responders[_port!].Settings);
        }

        public Task<GrblCommandResult> SendCommandAsync(string line)
        {
            SentCommands.Add(line);
            return Task.FromResult(GrblCommandResult.Ok);
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsOpen = false;
            StatusUpdated?.Invoke(default!);
            RawLineReceived?.Invoke(string.Empty);
            ErrorReceived?.Invoke(0, string.Empty);
            AlarmReceived?.Invoke(0, string.Empty);
            FeedbackMessageReceived?.Invoke(string.Empty);
            AlertChanged?.Invoke(null);
        }

        public void RequestStatus() { }
        public void FeedHold() { }
        public void CycleStartResume() { }
        public void CancelJog() { }
        public void SoftReset() { }
        public void StartStatusPolling(TimeSpan interval) { }
        public void StopStatusPolling() { }
        public void DismissAlert() { }
        public Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true) => SendCommandAsync("jog");
        public Task<GrblCommandResult> HomeAsync() => SendCommandAsync("$H");
        public Task<GrblCommandResult> UnlockAsync() => SendCommandAsync("$X");
        public Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin) => SendCommandAsync("origin");
        public Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber) => SendCommandAsync("wcs");
    }
}
