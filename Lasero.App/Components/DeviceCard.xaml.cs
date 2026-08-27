using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>The Home dashboard's device-status card — connection state (via StatusBadge), work area,
/// and a CTA into the Designer. Extracted so device status has one canonical rendering instead of
/// hand-rolled markup duplicated wherever it's shown.</summary>
public partial class DeviceCard : UserControl
{
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
        nameof(IsConnected), typeof(bool), typeof(DeviceCard), new PropertyMetadata(false));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(DeviceCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FirmwareBannerProperty = DependencyProperty.Register(
        nameof(FirmwareBanner), typeof(string), typeof(DeviceCard), new PropertyMetadata(null));

    public static readonly DependencyProperty WorkAreaWidthMmProperty = DependencyProperty.Register(
        nameof(WorkAreaWidthMm), typeof(double), typeof(DeviceCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty WorkAreaHeightMmProperty = DependencyProperty.Register(
        nameof(WorkAreaHeightMm), typeof(double), typeof(DeviceCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty OpenDesignerCommandProperty = DependencyProperty.Register(
        nameof(OpenDesignerCommand), typeof(ICommand), typeof(DeviceCard), new PropertyMetadata(null));

    public DeviceCard()
    {
        InitializeComponent();
    }

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string? FirmwareBanner
    {
        get => (string?)GetValue(FirmwareBannerProperty);
        set => SetValue(FirmwareBannerProperty, value);
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

    public ICommand? OpenDesignerCommand
    {
        get => (ICommand?)GetValue(OpenDesignerCommandProperty);
        set => SetValue(OpenDesignerCommandProperty, value);
    }
}
