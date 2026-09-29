using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.App.ViewModels;

public partial class MachineStatusViewModel : ObservableObject
{
    private readonly ILaserMachine _connection;

    [ObservableProperty] private GrblMachineMode _mode = GrblMachineMode.Unknown;
    [ObservableProperty] private double _machineX;
    [ObservableProperty] private double _machineY;
    [ObservableProperty] private double _machineZ;
    [ObservableProperty] private double _workX;
    [ObservableProperty] private double _workY;
    [ObservableProperty] private double _workZ;
    [ObservableProperty] private double _feedRate;
    [ObservableProperty] private double _spindleSpeed;
    [ObservableProperty] private string? _triggeredPins;
    [ObservableProperty] private MachineStatus? _current;
    [ObservableProperty] private MachineAlert? _activeAlert;
    [ObservableProperty] private LaserMachineDisplayState _displayState = LaserMachineDisplayState.Disconnected;

    public bool HasActiveAlert => ActiveAlert is not null;

    public MachineStatusViewModel(ILaserMachine connection)
    {
        _connection = connection;
        connection.StatusUpdated += status => RunOnUi(() => Apply(status));
        connection.AlertChanged += alert => RunOnUi(() => ApplyAlert(alert));
        connection.ConnectionStateChanged += state => RunOnUi(() => ApplyConnectionState(state));
    }

    // No WPF Application exists under unit tests; run inline there.
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    partial void OnActiveAlertChanged(MachineAlert? value)
    {
        OnPropertyChanged(nameof(HasActiveAlert));
        DisplayState = _connection.DisplayState;
    }

    [RelayCommand]
    private void DismissAlert() => _connection.DismissAlert();

    private void ApplyAlert(MachineAlert? alert)
    {
        // A late alert raised while the link is down must not resurrect the banner.
        if (alert is not null && _connection.State == GrblConnectionState.Disconnected) return;
        ActiveAlert = alert;
    }

    private void ApplyConnectionState(GrblConnectionState state)
    {
        if (state == GrblConnectionState.Disconnected)
        {
            ResetTelemetry();
            ActiveAlert = null;
        }
        DisplayState = _connection.DisplayState;
    }

    /// <summary>Drops the last known position/mode so a dead link never shows live-looking numbers.</summary>
    private void ResetTelemetry()
    {
        Current = null;
        Mode = GrblMachineMode.Unknown;
        MachineX = MachineY = MachineZ = 0;
        WorkX = WorkY = WorkZ = 0;
        FeedRate = 0;
        SpindleSpeed = 0;
        TriggeredPins = null;
    }

    private void Apply(MachineStatus status)
    {
        // A status report queued before the disconnect must not repopulate the cleared telemetry.
        if (_connection.State == GrblConnectionState.Disconnected) return;
        Current = status;
        Mode = status.Mode;
        MachineX = status.MachinePosition.X;
        MachineY = status.MachinePosition.Y;
        MachineZ = status.MachinePosition.Z;
        WorkX = status.WorkPosition.X;
        WorkY = status.WorkPosition.Y;
        WorkZ = status.WorkPosition.Z;
        FeedRate = status.FeedRate;
        SpindleSpeed = status.SpindleSpeed;
        TriggeredPins = status.TriggeredPins;
        DisplayState = _connection.DisplayState;
    }
}
