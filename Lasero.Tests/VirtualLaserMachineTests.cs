using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

public sealed class VirtualLaserMachineTests
{
    [Fact]
    public async Task SimulatorPortIsRoutedWithoutOpeningPhysicalSerialHardware()
    {
        using var routing = new RoutingGrblTransport(new GrblSerialTransport(), new VirtualGrblTransport());
        using var machine = new GrblConnection(routing);

        machine.Connect(VirtualGrblTransport.PortName);
        var settings = await machine.QuerySettingsAsync();

        Assert.True(routing.IsVirtualActive);
        Assert.Contains("$32=1", settings);
    }

    [Fact]
    public async Task BeginnerWorkflowRunsThroughRealConnectionAndJobRunnerWithoutHardware()
    {
        using var transport = new VirtualGrblTransport();
        using var machine = new GrblConnection(transport);
        machine.Connect(VirtualGrblTransport.PortName);

        var settings = await machine.QuerySettingsAsync();
        Assert.Contains("$130=400.000", settings);
        Assert.Contains("$131=400.000", settings);
        Assert.Contains("$32=1", settings);

        var jog = await machine.JogAsync(10, 5, 0, 1000);
        Assert.True(jog.IsOk);
        var afterJog = RequestStatus(machine);
        Assert.Equal(new Position(10, 5, 0), afterJog.WorkPosition);

        var runner = new GCodeJobRunner(machine);
        await runner.RunAsync(["G90", "M4 S200", "G1 X20 Y10 F1000", "M5"]);
        var afterJob = RequestStatus(machine);

        Assert.Equal(JobRunState.Completed, runner.State);
        Assert.Equal(new Position(20, 10, 0), afterJob.WorkPosition);
        Assert.Equal(0, afterJob.SpindleSpeed);
        Assert.Equal(GrblMachineMode.Idle, afterJob.Mode);
    }

    [Fact]
    public async Task VirtualAlarmBlocksCommandsUntilOperatorUnlocksTheMachine()
    {
        using var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        using var machine = new GrblConnection(transport);
        machine.Connect(VirtualGrblTransport.PortName);

        transport.InjectAlarm(1);
        var blocked = await machine.SendCommandAsync("G1 X10 F1000");
        Assert.False(blocked.IsOk);
        Assert.Equal(GrblMachineMode.Alarm, RequestStatus(machine).Mode);

        Assert.True((await machine.UnlockAsync()).IsOk);
        Assert.Equal(GrblMachineMode.Idle, RequestStatus(machine).Mode);
        Assert.Null(machine.ActiveAlert);
    }

    private static MachineStatus RequestStatus(GrblConnection machine)
    {
        MachineStatus? status = null;
        void Capture(MachineStatus value) => status = value;
        machine.StatusUpdated += Capture;
        try
        {
            machine.RequestStatus();
            return status ?? throw new Xunit.Sdk.XunitException("Virtuální stroj neodeslal stav.");
        }
        finally
        {
            machine.StatusUpdated -= Capture;
        }
    }
}
