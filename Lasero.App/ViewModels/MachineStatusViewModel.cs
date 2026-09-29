using System.Globalization;
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

    // Telemetry read-outs. Without a live status report (disconnected, or nothing received yet) a
    // zero is indistinguishable from "at work origin", so the panel shows an em dash instead.
    public const string NoValue = "—";
    public string WorkXDisplay => Current is null ? NoValue : WorkX.ToString("0.00", CultureInfo.CurrentCulture);
    public string WorkYDisplay => Current is null ? NoValue : WorkY.ToString("0.00", CultureInfo.CurrentCulture);
    public string WorkZDisplay => Current is null ? NoValue : WorkZ.ToString("0.00", CultureInfo.CurrentCulture);
    public string FeedRateDisplay => Current is null ? NoValue : FeedRate.ToString("0", CultureInfo.CurrentCulture);
    public string SpindleSpeedDisplay => Current is null ? NoValue : SpindleSpeed.ToString("0", CultureInfo.CurrentCulture);

    private void NotifyTelemetryDisplay()
    {
        OnPropertyChanged(nameof(WorkXDisplay));
        OnPropertyChanged(nameof(WorkYDisplay));
        OnPropertyChanged(nameof(WorkZDisplay));
        OnPropertyChanged(nameof(FeedRateDisplay));
        OnPropertyChanged(nameof(SpindleSpeedDisplay));
    }

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
        NotifyTelemetryDisplay();
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
        NotifyTelemetryDisplay();
        DisplayState = _connection.DisplayState;
    }
}
