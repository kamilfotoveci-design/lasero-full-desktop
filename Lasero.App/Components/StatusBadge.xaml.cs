using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>One consistent connection-status pill — replaces the ad-hoc Ellipse+TextBlock status
/// indicators previously duplicated (with slightly different markup each time) across the top
/// command-bar chip, the Home dashboard's device card, and the left-nav "Stroj" panel.</summary>
public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
        nameof(IsConnected), typeof(bool), typeof(StatusBadge), new PropertyMetadata(false));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    public StatusBadge()
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
}
