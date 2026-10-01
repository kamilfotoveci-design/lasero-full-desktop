using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;

namespace Lasero.Tests;

public sealed class ConnectionViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-connection-vm-tests", Guid.NewGuid().ToString("N"));

    public ConnectionViewModelTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DevicePickerAlwaysOffersSafeVirtualLaserAlongsidePhysicalPorts()
    {
        using var machine = new GrblConnection(new VirtualGrblTransport());
        var settings = new AppSettingsStore(Path.Combine(_directory, "settings.json"));

        var viewModel = new ConnectionViewModel(machine, settings);

        Assert.Contains(VirtualGrblTransport.PortName, viewModel.AvailablePorts);
    }

    [Fact]
    public void ConnectedStatusDoesNotRemainInDeviceIdentificationProgress()
    {
        using var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var settings = new AppSettingsStore(Path.Combine(_directory, "status-settings.json"));
        var viewModel = new ConnectionViewModel(machine, settings);

        machine.Connect(VirtualGrblTransport.PortName);

        Assert.StartsWith("Připojeno", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("zjišťuje se typ", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualMotionRequiresFreshIdleStateButSafetyResetRemainsAvailable()
    {
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        using var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, "jog-settings.json"));
        var viewModel = new JogViewModel(machine, settings);

        machine.Connect(VirtualGrblTransport.PortName);
        Assert.False(viewModel.JogXPosCommand.CanExecute(null));
        Assert.True(viewModel.SoftResetCommand.CanExecute(null));

        machine.RequestStatus();
        Assert.True(viewModel.JogXPosCommand.CanExecute(null));
        Assert.True(viewModel.SetOriginHereCommand.CanExecute(null));

        transport.InjectAlarm();
        Assert.False(viewModel.JogXPosCommand.CanExecute(null));
        Assert.False(viewModel.SetOriginHereCommand.CanExecute(null));
        Assert.True(viewModel.UnlockCommand.CanExecute(null));
        Assert.True(viewModel.SoftResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task PositioningLaserUsesPercentOfControllerMaximumAndAlwaysStopsWithM5()
    {
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        using var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, "laser-fire-settings.json"));
        var viewModel = new JogViewModel(machine, settings)
        {
            PositioningLaserPowerPercent = 1,
        };

        machine.Connect(VirtualGrblTransport.PortName);
        machine.RequestStatus();
        viewModel.ConfigurePositioningLaser(maximumSValue: null, laserModeEnabled: true);
        Assert.False(viewModel.CanUsePositioningLaser);
        await viewModel.StartPositioningLaserAsync();
        Assert.False(viewModel.IsPositioningLaserOn);

        viewModel.ConfigurePositioningLaser(maximumSValue: 1000, laserModeEnabled: true);

        Assert.True(viewModel.CanUsePositioningLaser);
        await viewModel.StartPositioningLaserAsync();
        machine.RequestStatus();
        Assert.True(viewModel.IsPositioningLaserOn);
        Assert.Equal(10, machine.LastStatus?.SpindleSpeed);

        await viewModel.StopPositioningLaserAsync();
        machine.RequestStatus();
        Assert.False(viewModel.IsPositioningLaserOn);
        Assert.Equal(0, machine.LastStatus?.SpindleSpeed);
    }

    [Fact]
    public async Task MotionCommandsAreBlockedWhileThePositioningLaserIsLit()
    {
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        using var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, "laser-interlock-settings.json"));
        var viewModel = new JogViewModel(machine, settings)
        {
            PositioningLaserPowerPercent = 1,
        };

        machine.Connect(VirtualGrblTransport.PortName);
        machine.RequestStatus();
        viewModel.ConfigurePositioningLaser(maximumSValue: 1000, laserModeEnabled: true);

        // Reported machine mode stays Idle while the positioning laser is lit — CanManualMotion alone
        // cannot see the beam, so every motion command must independently refuse to run.
        Assert.True(viewModel.JogXPosCommand.CanExecute(null));
        Assert.True(viewModel.HomeCommand.CanExecute(null));
        Assert.True(viewModel.SetOriginHereCommand.CanExecute(null));
        Assert.True(viewModel.GoToWorkZeroCommand.CanExecute(null));

        await viewModel.StartPositioningLaserAsync();
        machine.RequestStatus();
        Assert.True(viewModel.IsPositioningLaserOn);

        Assert.False(viewModel.JogXPosCommand.CanExecute(null));
        Assert.False(viewModel.JogZPosCommand.CanExecute(null));
        Assert.False(viewModel.HomeCommand.CanExecute(null));
        Assert.False(viewModel.SetOriginHereCommand.CanExecute(null));
        Assert.False(viewModel.GoToWorkZeroCommand.CanExecute(null));

        await viewModel.StopPositioningLaserAsync();
        machine.RequestStatus();
        Assert.False(viewModel.IsPositioningLaserOn);

        Assert.True(viewModel.JogXPosCommand.CanExecute(null));
        Assert.True(viewModel.HomeCommand.CanExecute(null));
        Assert.True(viewModel.SetOriginHereCommand.CanExecute(null));
        Assert.True(viewModel.GoToWorkZeroCommand.CanExecute(null));
    }

    [Fact]
    public async Task OriginAndRapidCommandsAskForConfirmationBeforeAnything()
    {
        var (machine, transport, viewModel) = CreateReadyJog("confirm-asks.json");
        using var _ = machine;
        var prompts = new List<LaseroDialogOptions>();
        viewModel.ConfirmAction = o => { prompts.Add(o); return false; };

        await viewModel.SetOriginHereCommand.ExecuteAsync(null);
        await viewModel.GoToWorkZeroCommand.ExecuteAsync(null);

        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, o => Assert.DoesNotContain('?', o.Message + o.Title));
        Assert.All(prompts, o => Assert.DoesNotContain('!', o.Message + o.Title));
    }

    [Fact]
    public async Task DecliningConfirmationSendsNothingToTheMachine()
    {
        var (machine, transport, viewModel) = CreateReadyJog("confirm-declines.json");
        using var _ = machine;
        viewModel.ConfirmAction = _ => false;
        transport.Sent.Clear();

        await viewModel.SetOriginHereCommand.ExecuteAsync(null);
        await viewModel.GoToWorkZeroCommand.ExecuteAsync(null);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task AcceptingConfirmationSendsTheCommands()
    {
        var (machine, transport, viewModel) = CreateReadyJog("confirm-accepts.json");
        using var _ = machine;
        viewModel.ConfirmAction = _ => true;
        transport.Sent.Clear();

        await viewModel.SetOriginHereCommand.ExecuteAsync(null);
        Assert.Contains(transport.Sent, l => l.StartsWith("G10", StringComparison.OrdinalIgnoreCase));

        await viewModel.GoToWorkZeroCommand.ExecuteAsync(null);
        Assert.Contains(transport.Sent, l => l.Contains("G0", StringComparison.OrdinalIgnoreCase) && l.Contains("X0 Y0"));
    }

    [Fact]
    public async Task ConfirmationDoesNotBypassTheLaserInterlock()
    {
        var (machine, transport, viewModel) = CreateReadyJog("confirm-interlock.json");
        using var _ = machine;
        viewModel.ConfigurePositioningLaser(maximumSValue: 1000, laserModeEnabled: true);
        await viewModel.StartPositioningLaserAsync();
        machine.RequestStatus();
        var asked = false;
        viewModel.ConfirmAction = _ => { asked = true; return true; };
        transport.Sent.Clear();

        Assert.False(viewModel.GoToWorkZeroCommand.CanExecute(null));
        Assert.False(viewModel.SetOriginHereCommand.CanExecute(null));
        Assert.False(asked);
        Assert.Empty(transport.Sent);
    }

    private (GrblConnection Machine, RecordingTransport Transport, JogViewModel ViewModel) CreateReadyJog(string settingsFile)
    {
        var transport = new RecordingTransport(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, settingsFile));
        var viewModel = new JogViewModel(machine, settings);
        machine.Connect(VirtualGrblTransport.PortName);
        machine.RequestStatus();
        return (machine, transport, viewModel);
    }

    private sealed class RecordingTransport(IGrblTransport inner) : IGrblTransport
    {
        public List<string> Sent { get; } = [];
        public bool IsOpen => inner.IsOpen;
        public event Action<string>? LineReceived { add => inner.LineReceived += value; remove => inner.LineReceived -= value; }
        public event Action<Exception>? UnexpectedlyClosed { add => inner.UnexpectedlyClosed += value; remove => inner.UnexpectedlyClosed -= value; }
        public void Open(string portName, int baudRate) => inner.Open(portName, baudRate);
        public void Close() => inner.Close();
        public void WriteLine(string text) { lock (Sent) Sent.Add(text); inner.WriteLine(text); }
        public void WriteRealtimeByte(byte value) => inner.WriteRealtimeByte(value);
        public void Dispose() => inner.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
