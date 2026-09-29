using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class MachineStatusViewModelTests
{
    [Fact]
    public void Disconnect_ClearsTelemetryModeAndAlarmBanner()
    {
        var machine = new StubMachine();
        var vm = new MachineStatusViewModel(machine);

        machine.State = GrblConnectionState.Connected;
        machine.DisplayState = LaserMachineDisplayState.Alarm;
        machine.RaiseStatus(new MachineStatus
        {
            Mode = GrblMachineMode.Alarm,
            MachinePosition = new Position(10, 20, 3),
            WorkPosition = new Position(1, 2, 3),
            FeedRate = 500,
            SpindleSpeed = 800,
            TriggeredPins = "X",
        });
        machine.RaiseAlert(new MachineAlert
        {
            Kind = MachineAlertKind.Alarm,
            Code = 1,
            Message = "Hard limit",
            OccurredUtc = DateTime.UtcNow,
        });
        Assert.True(vm.HasActiveAlert);
        Assert.Equal(10, vm.MachineX);

        machine.State = GrblConnectionState.Disconnected;
        machine.DisplayState = LaserMachineDisplayState.Disconnected;
        machine.RaiseConnectionState(GrblConnectionState.Disconnected);

        Assert.Null(vm.ActiveAlert);
        Assert.False(vm.HasActiveAlert);
        Assert.Null(vm.Current);
        Assert.Equal(GrblMachineMode.Unknown, vm.Mode);
        Assert.Equal(0, vm.MachineX);
        Assert.Equal(0, vm.MachineY);
        Assert.Equal(0, vm.MachineZ);
        Assert.Equal(0, vm.WorkX);
        Assert.Equal(0, vm.WorkY);
        Assert.Equal(0, vm.WorkZ);
        Assert.Equal(0, vm.FeedRate);
        Assert.Equal(0, vm.SpindleSpeed);
        Assert.Null(vm.TriggeredPins);
        Assert.Equal(LaserMachineDisplayState.Disconnected, vm.DisplayState);
    }

    [Fact]
    public void LateStatusAfterDisconnect_IsIgnored()
    {
        var machine = new StubMachine();
        var vm = new MachineStatusViewModel(machine);
        machine.State = GrblConnectionState.Disconnected;
        machine.DisplayState = LaserMachineDisplayState.Disconnected;

        machine.RaiseStatus(new MachineStatus { Mode = GrblMachineMode.Idle, MachinePosition = new Position(5, 6, 7) });

        Assert.Null(vm.Current);
        Assert.Equal(0, vm.MachineX);
    }

    [Fact]
    public void TelemetryDisplay_ShowsDashesUntilALiveStatusArrivesAndAgainAfterDisconnect()
    {
        var machine = new StubMachine();
        var vm = new MachineStatusViewModel(machine);
        var dash = MachineStatusViewModel.NoValue;

        Assert.Equal(dash, vm.WorkXDisplay);
        Assert.Equal(dash, vm.WorkYDisplay);
        Assert.Equal(dash, vm.WorkZDisplay);
        Assert.Equal(dash, vm.FeedRateDisplay);
        Assert.Equal(dash, vm.SpindleSpeedDisplay);

        machine.State = GrblConnectionState.Connected;
        machine.RaiseStatus(new MachineStatus
        {
            Mode = GrblMachineMode.Idle,
            WorkPosition = new Position(1.5, 2, 0),
            FeedRate = 500,
            SpindleSpeed = 0,
        });

        // A live zero is real data and must stay a number.
        Assert.Equal(1.5.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture), vm.WorkXDisplay);
        Assert.Equal(0.0.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture), vm.WorkZDisplay);
        Assert.Equal("500", vm.FeedRateDisplay);
        Assert.Equal("0", vm.SpindleSpeedDisplay);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        machine.State = GrblConnectionState.Disconnected;
        machine.RaiseConnectionState(GrblConnectionState.Disconnected);

        Assert.Equal(dash, vm.WorkXDisplay);
        Assert.Equal(dash, vm.FeedRateDisplay);
        Assert.Equal(dash, vm.SpindleSpeedDisplay);
        Assert.Contains(nameof(MachineStatusViewModel.WorkXDisplay), changed);
        Assert.Contains(nameof(MachineStatusViewModel.SpindleSpeedDisplay), changed);
    }

    private sealed class StubMachine : ILaserMachine
    {
        public GrblConnectionState State { get; set; } = GrblConnectionState.Disconnected;
        public MachineStatus? LastStatus => null;
        public DateTime? LastStatusReceivedUtc => null;
        public string? FirmwareBanner => null;
        public MachineAlert? ActiveAlert { get; set; }
        public LaserMachineDisplayState DisplayState { get; set; } = LaserMachineDisplayState.Disconnected;

        public event Action<GrblConnectionState>? ConnectionStateChanged;
        public event Action<MachineStatus>? StatusUpdated;
        public event Action<string>? RawLineReceived { add { } remove { } }
        public event Action<int, string>? ErrorReceived { add { } remove { } }
        public event Action<int, string>? AlarmReceived { add { } remove { } }
        public event Action<string>? FeedbackMessageReceived { add { } remove { } }
        public event Action<string>? Connected { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public event Action<MachineAlert?>? AlertChanged;

        public void RaiseStatus(MachineStatus s) => StatusUpdated?.Invoke(s);
        public void RaiseAlert(MachineAlert? a) { ActiveAlert = a; AlertChanged?.Invoke(a); }
        public void RaiseConnectionState(GrblConnectionState s) => ConnectionStateChanged?.Invoke(s);

        public void Connect(string portName, int baudRate = 115200) => throw new NotSupportedException();
        public void Disconnect() { }
        public void RequestStatus() { }
        public void FeedHold() { }
        public void CycleStartResume() { }
        public void CancelJog() { }
        public void SoftReset() { }
        public void StartStatusPolling(TimeSpan interval) { }
        public void StopStatusPolling() { }
        public void DismissAlert() { }
        public Task<GrblCommandResult> SendCommandAsync(string line) => throw new NotSupportedException();
        public Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true) => throw new NotSupportedException();
        public Task<GrblCommandResult> HomeAsync() => throw new NotSupportedException();
        public Task<GrblCommandResult> UnlockAsync() => throw new NotSupportedException();
        public Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin) => throw new NotSupportedException();
        public Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> QuerySettingsAsync() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
