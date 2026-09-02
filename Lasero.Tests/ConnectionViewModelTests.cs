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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
