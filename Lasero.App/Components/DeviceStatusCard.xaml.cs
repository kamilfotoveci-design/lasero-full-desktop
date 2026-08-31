using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>
/// Home's device panel: what machine is attached, how it is attached, how big its bed is, and what it
/// is doing right now. Every field is a bound value — when nothing is connected the card says so
/// rather than showing a plausible-looking machine, because a fake "Připojeno" is the one thing a
/// laser UI must never do.
/// </summary>
public partial class DeviceStatusCard : UserControl
{
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
        nameof(IsConnected), typeof(bool), typeof(DeviceStatusCard), new PropertyMetadata(false));

    public static readonly DependencyProperty MachineNameProperty = DependencyProperty.Register(
        nameof(MachineName), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata("Žádné zařízení"));

    public static readonly DependencyProperty ConnectionLabelProperty = DependencyProperty.Register(
        nameof(ConnectionLabel), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusLabelProperty = DependencyProperty.Register(
        nameof(StatusLabel), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FirmwareLabelProperty = DependencyProperty.Register(
        nameof(FirmwareLabel), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata("—"));

    public static readonly DependencyProperty StateLabelProperty = DependencyProperty.Register(
        nameof(StateLabel), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata("—"));

    public static readonly DependencyProperty StateBrushProperty = DependencyProperty.Register(
        nameof(StateBrush), typeof(Brush), typeof(DeviceStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty WorkAreaWidthMmProperty = DependencyProperty.Register(
        nameof(WorkAreaWidthMm), typeof(double), typeof(DeviceStatusCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty WorkAreaHeightMmProperty = DependencyProperty.Register(
        nameof(WorkAreaHeightMm), typeof(double), typeof(DeviceStatusCard), new PropertyMetadata(0.0));

    /// <summary>Path to the operator's photo of this machine, or null for the drawn fallback.</summary>
    public static readonly DependencyProperty PhotoPathProperty = DependencyProperty.Register(
        nameof(PhotoPath), typeof(string), typeof(DeviceStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ConnectDeviceCommandProperty = DependencyProperty.Register(
        nameof(ConnectDeviceCommand), typeof(ICommand), typeof(DeviceStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty OpenMachineControlCommandProperty = DependencyProperty.Register(
        nameof(OpenMachineControlCommand), typeof(ICommand), typeof(DeviceStatusCard), new PropertyMetadata(null));

    public DeviceStatusCard() => InitializeComponent();

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public string MachineName
    {
        get => (string)GetValue(MachineNameProperty);
        set => SetValue(MachineNameProperty, value);
    }

    public string ConnectionLabel
    {
        get => (string)GetValue(ConnectionLabelProperty);
        set => SetValue(ConnectionLabelProperty, value);
    }

    public string StatusLabel
    {
        get => (string)GetValue(StatusLabelProperty);
        set => SetValue(StatusLabelProperty, value);
    }

    public string FirmwareLabel
    {
        get => (string)GetValue(FirmwareLabelProperty);
        set => SetValue(FirmwareLabelProperty, value);
    }

    public string StateLabel
    {
        get => (string)GetValue(StateLabelProperty);
        set => SetValue(StateLabelProperty, value);
    }

    public Brush? StateBrush
    {
        get => (Brush?)GetValue(StateBrushProperty);
        set => SetValue(StateBrushProperty, value);
    }

    public double WorkAreaWidthMm
    {
        get => (double)GetValue(WorkAreaWidthMmProperty);
        set => SetValue(WorkAreaWidthMmProperty, value);
    }

    public double WorkAreaHeightMm
    {
        get => (double)GetValue(WorkAreaHeightMmProperty);
        set => SetValue(WorkAreaHeightMmProperty, value);
    }

    public string? PhotoPath
    {
        get => (string?)GetValue(PhotoPathProperty);
        set => SetValue(PhotoPathProperty, value);
    }

    public ICommand? ConnectDeviceCommand
    {
        get => (ICommand?)GetValue(ConnectDeviceCommandProperty);
        set => SetValue(ConnectDeviceCommandProperty, value);
    }

    public ICommand? OpenMachineControlCommand
    {
        get => (ICommand?)GetValue(OpenMachineControlCommandProperty);
        set => SetValue(OpenMachineControlCommandProperty, value);
    }
}
