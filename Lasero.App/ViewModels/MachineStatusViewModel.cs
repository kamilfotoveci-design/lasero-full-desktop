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
        connection.StatusUpdated += status => Application.Current.Dispatcher.Invoke(() => Apply(status));
        connection.AlertChanged += alert => Application.Current.Dispatcher.Invoke(() => ActiveAlert = alert);
        connection.ConnectionStateChanged += _ => Application.Current.Dispatcher.Invoke(() => DisplayState = connection.DisplayState);
    }

    partial void OnActiveAlertChanged(MachineAlert? value)
    {
        OnPropertyChanged(nameof(HasActiveAlert));
        DisplayState = _connection.DisplayState;
    }

    [RelayCommand]
    private void DismissAlert() => _connection.DismissAlert();

    private void Apply(MachineStatus status)
    {
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
