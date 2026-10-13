using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia.Views.DeviceSetup;

/// <summary>
/// Port of Lasero.App's DeviceWizardOverlay.xaml.cs, minus the animation machinery (see the .axaml
/// file's own remarks) — the functional behaviors it kept: Esc/close-button cancel any in-flight scan
/// or connection attempt before closing (never leaves a port open behind a closed overlay), and the
/// work-area fields notify the view model on LostFocus so the Finish gate re-evaluates.
/// </summary>
public sealed partial class DeviceWizardOverlay : UserControl
{
    public DeviceWizardOverlay()
    {
        AvaloniaXamlLoader.Load(this);
        Focusable = true;
        KeyDown += OnKeyDown;
    }

    private DeviceWizardViewModel? Wizard => DataContext as DeviceWizardViewModel;
    private ShellViewModel? Shell => (TopLevel.GetTopLevel(this) as Window)?.DataContext as ShellViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        RequestClose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => RequestClose();

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        RequestClose();
        Shell?.GoSettings.Execute().Subscribe();
    }

    private void OnWorkAreaEdited(object? sender, RoutedEventArgs e) => Wizard?.NotifyWorkAreaEdited();

    /// <summary>Esc and the close button both funnel through here. If a scan or a connection attempt
    /// is genuinely in flight, cancel it first — reusing the wizard's own CancelScan and the
    /// connection's own Disconnect — rather than leaving a port open behind a closed overlay.</summary>
    private void RequestClose()
    {
        var wizard = Wizard;
        if (wizard is not null)
        {
            if (wizard.IsScanning) wizard.CancelScan.Execute().Subscribe();
            if (wizard.IsConnecting && wizard.Connection.IsConnected) wizard.Connection.Disconnect.Execute().Subscribe();
        }

        Shell?.CloseDeviceWizard.Execute().Subscribe();
    }
}
