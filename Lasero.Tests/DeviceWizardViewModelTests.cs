using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

/// <summary>
/// Covers the one piece of real logic the new DeviceWizardOverlay view added to
/// <see cref="DeviceWizardViewModel"/>: a connect attempt that never actually reaches
/// <c>IsConnected</c> must not be mistaken for "connected but slow to identify" and must not
/// advance the wizard to the Setup step.
/// </summary>
public sealed class DeviceWizardViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-device-wizard-vm-tests", Guid.NewGuid().ToString("N"));

    public DeviceWizardViewModelTests() => Directory.CreateDirectory(_directory);

    private DeviceWizardViewModel CreateWizard()
    {
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"));
        var connection = new ConnectionViewModel(machine, settings);
        var scanner = new DeviceScanner(new NullMachineFactory(), () => Array.Empty<string>());
        return new DeviceWizardViewModel(scanner, connection, machine, settings)
        {
            // The real 8-second GRBL identification timeout would make this test slow for no
            // reason — the virtual transport's Open() throws synchronously for a port name it
            // does not own, so the failure is already known long before any timeout matters.
            IdentificationTimeout = TimeSpan.FromMilliseconds(50),
        };
    }

    [Fact]
    public async Task AFailedConnectionAttemptStaysOnResultsRatherThanAdvancingToSetup()
    {
        var wizard = CreateWizard();
        var badMachine = new DiscoveredMachine("COM_DOES_NOT_EXIST", 115200, null, new GrblDeviceProfile());
        wizard.Step = DeviceWizardStep.Results;
        wizard.SelectedMachine = badMachine;

        await wizard.UseSelectedMachineCommand.ExecuteAsync(null);

        Assert.Equal(DeviceWizardStep.Results, wizard.Step);
        Assert.True(wizard.ConnectFailed);
    }

    [Fact]
    public async Task SelectingADifferentMachineClearsAPreviousConnectFailure()
    {
        var wizard = CreateWizard();
        var first = new DiscoveredMachine("COM_DOES_NOT_EXIST", 115200, null, new GrblDeviceProfile());
        var second = new DiscoveredMachine("COM_ALSO_MISSING", 115200, null, new GrblDeviceProfile());
        wizard.Step = DeviceWizardStep.Results;
        wizard.SelectedMachine = first;
        await wizard.UseSelectedMachineCommand.ExecuteAsync(null);
        Assert.True(wizard.ConnectFailed);

        wizard.SelectedMachine = second;

        Assert.False(wizard.ConnectFailed);
    }

    [Fact]
    public async Task ExplicitSimulatorSelectionClearsBlockedHardwareCompatibility()
    {
        var wizard = CreateWizard();
        wizard.Connection.SelectedCompatibility = MachineCompatibilityCatalog.Get("xtool-s1");
        wizard.Step = DeviceWizardStep.Results;
        wizard.SelectedMachine = new DiscoveredMachine(VirtualGrblTransport.PortName, 115200, null, new GrblDeviceProfile());
        try
        {
            await wizard.UseSelectedMachineCommand.ExecuteAsync(null);
            Assert.Equal(MachineCompatibilityCatalog.ExistingGrblId, wizard.Connection.SelectedCompatibility.Id);
            Assert.True(wizard.Connection.IsConnected);
            Assert.False(wizard.ConnectFailed);
        }
        finally { wizard.Connection.DisconnectCommand.Execute(null); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class NullMachineFactory : ILaserMachineFactory
    {
        public ILaserMachine Create() => new GrblConnection(new VirtualGrblTransport());
    }
}
