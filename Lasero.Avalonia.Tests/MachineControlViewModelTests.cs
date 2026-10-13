using System.IO;
using System.Reactive.Linq;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// Regression tests for the safety-critical parts of the Machine Control port: constructing a
/// view model must never itself move or command the machine, and the two safety confirmations
/// (SoftReset, risky console commands) must fail closed — do nothing — rather than skip the gate
/// when no confirmation UI has been wired up yet.
/// </summary>
public sealed class MachineControlViewModelTests
{
    private static AppSettingsStore NewSettingsStore() =>
        new(Path.Combine(Path.GetTempPath(), $"lasero-test-{Guid.NewGuid():N}.json"));

    [Fact]
    public void ConstructingConnectionViewModelDoesNotOpenAnyPort()
    {
        TestApp.EnsureInitialized();
        var machine = new FakeLaserMachine();

        var viewModel = new ConnectionViewModel(machine, NewSettingsStore());

        Assert.False(machine.IsOpen);
        Assert.Empty(machine.SentCommands);
        Assert.True(viewModel.AvailablePorts.Count > 0); // RefreshPorts ran (enumeration only)
        Assert.False(viewModel.IsConnected);
    }

    [Fact]
    public void JogCommandsAreDisabledUntilConnectedAndIdle()
    {
        TestApp.EnsureInitialized();
        var machine = new FakeLaserMachine();
        var jog = new JogViewModel(machine, NewSettingsStore());

        Assert.False(jog.JogXPos.CanExecute.FirstAsync().Wait());
        Assert.False(jog.Home.CanExecute.FirstAsync().Wait());

        machine.Connect("COM_TEST", 115200);
        machine.RaiseStatus(new MachineStatus { Mode = GrblMachineMode.Idle });

        Assert.True(jog.JogXPos.CanExecute.FirstAsync().Wait());
        jog.JogXPos.Execute().Wait();
        Assert.Contains(machine.SentCommands, c => c.StartsWith("jog "));
    }

    [Fact]
    public async Task SoftResetDoesNothingWhenNoConfirmationIsWiredUp()
    {
        TestApp.EnsureInitialized();
        var machine = new FakeLaserMachine();
        var settings = NewSettingsStore();
        settings.Current.Safety.ConfirmSoftReset = true;
        var jog = new JogViewModel(machine, settings);
        machine.Connect("COM_TEST", 115200);
        // ConfirmDangerousAction deliberately left null — no view has wired it up yet.

        await jog.SoftReset.Execute();

        Assert.Equal(0, machine.SoftResetCount);
    }

    [Fact]
    public async Task SoftResetProceedsOnlyWhenConfirmed()
    {
        TestApp.EnsureInitialized();
        var machine = new FakeLaserMachine();
        var settings = NewSettingsStore();
        settings.Current.Safety.ConfirmSoftReset = true;
        var jog = new JogViewModel(machine, settings) { ConfirmDangerousAction = (_, _, _, _) => Task.FromResult(false) };
        machine.Connect("COM_TEST", 115200);

        await jog.SoftReset.Execute();
        Assert.Equal(0, machine.SoftResetCount);

        jog.ConfirmDangerousAction = (_, _, _, _) => Task.FromResult(true);
        await jog.SoftReset.Execute();
        Assert.Equal(1, machine.SoftResetCount);
    }

    [Fact]
    public async Task ConsoleSendRequiresConnectedIdleMachineAndConfirmsRiskyCommands()
    {
        TestApp.EnsureInitialized();
        var machine = new FakeLaserMachine();
        var console = new ConsoleViewModel(machine);

        console.CommandInput = "$$";
        Assert.False(console.Send.CanExecute.FirstAsync().Wait()); // not connected yet

        machine.Connect("COM_TEST", 115200);
        machine.RaiseStatus(new MachineStatus { Mode = GrblMachineMode.Idle });
        Assert.True(console.Send.CanExecute.FirstAsync().Wait());

        console.CommandInput = "G0 X10";
        var confirmed = false;
        console.ConfirmDangerousAction = (_, _, _, _) => { confirmed = true; return Task.FromResult(false); };
        await console.Send.Execute();

        Assert.True(confirmed);
        Assert.DoesNotContain("G0 X10", machine.SentCommands); // declined, so never sent

        console.CommandInput = "$30=500";
        await console.Send.Execute();
        Assert.DoesNotContain("$30=500", machine.SentCommands); // a power-range change is also confirmed
    }
}
