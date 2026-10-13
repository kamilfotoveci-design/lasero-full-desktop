using System.IO;
using System.Reactive.Linq;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// Mirrors Lasero.Tests/DeviceWizardViewModelTests.cs — the one piece of real logic this view model
/// owns beyond plain state transitions: a connect attempt that never actually reaches IsConnected
/// must not be mistaken for "connected but slow to identify" and must not advance to Setup.
/// </summary>
public sealed class DeviceWizardViewModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "lasero-device-wizard-vm-tests", Guid.NewGuid().ToString("N"));

    public DeviceWizardViewModelTests() => Directory.CreateDirectory(directory);

    private DeviceWizardViewModel CreateWizard()
    {
        TestApp.EnsureInitialized();
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"));
        var connection = new ConnectionViewModel(machine, settings);
        var scanner = new DeviceScanner(new NullMachineFactory(), () => Array.Empty<string>());
        return new DeviceWizardViewModel(scanner, connection, machine, settings)
        {
            // The real 8-second GRBL identification timeout would make this test slow for no
            // reason — the virtual transport's Connect() fails synchronously for a port name it
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

        await wizard.UseSelectedMachine.Execute();

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
        await wizard.UseSelectedMachine.Execute();
        Assert.True(wizard.ConnectFailed);

        wizard.SelectedMachine = second;

        Assert.False(wizard.ConnectFailed);
    }

    [Fact]
    public void ConstructingTheWizardScansNothingAndConnectsNothing()
    {
        // Building the view model (as happens the instant the overlay opens) must never itself
        // touch a port — only the operator's own Scan/Připojit clicks may do that.
        var wizard = CreateWizard();

        Assert.Equal(DeviceWizardStep.Intro, wizard.Step);
        Assert.Empty(wizard.FoundMachines);
        Assert.False(wizard.Connection.IsConnected);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class NullMachineFactory : ILaserMachineFactory
    {
        public ILaserMachine Create() => new GrblConnection(new VirtualGrblTransport());
    }
}
