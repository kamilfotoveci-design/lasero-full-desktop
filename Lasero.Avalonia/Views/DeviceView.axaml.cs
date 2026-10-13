using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia.Views;

/// <summary>
/// Hosts Připojení/Pohyb/Konzola. The two pieces that need real UI (not just command bindings) are
/// wired here: the positioning-laser button is held, not clicked (press starts the beam, release or
/// losing the pointer always stops it — never a toggle that can be left on), and the two safety
/// confirmations (SoftReset, risky console commands) go through JogViewModel/ConsoleViewModel's
/// ConfirmDangerousAction delegate, pointed at ConfirmationDialog.ShowAsync against this control's own
/// window. Neither JogViewModel nor ConsoleViewModel is told about Avalonia.Controls.Window directly.
/// </summary>
public sealed partial class DeviceView : UserControl
{
    public DeviceView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => WireConfirmations();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void WireConfirmations()
    {
        if (Shell is not { } shell) return;
        Task<bool> Confirm(string title, string message, string primaryText, bool destructive)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            return owner is null
                ? Task.FromResult(false)
                : ConfirmationDialog.ShowAsync(owner, title, message, primaryText, destructive: destructive);
        }
        shell.Jog.ConfirmDangerousAction = Confirm;
        shell.Console.ConfirmDangerousAction = Confirm;
    }

    // ---------------------------------------------------------------------------------------
    // Positioning laser: press-and-hold only. Mirrors Lasero.App's OnPositioningLaserPressed/
    // Released/LostMouseCapture — every path that can end the press (release, pointer leaving the
    // button, losing capture) must call StopPositioningLaserAsync, or the beam could stay lit.
    // ---------------------------------------------------------------------------------------

    private void OnPositioningLaserPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Shell is { } shell) _ = shell.Jog.StartPositioningLaserAsync();
    }

    private void OnPositioningLaserReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Shell is { } shell) _ = shell.Jog.StopPositioningLaserAsync();
    }

    private void OnPositioningLaserExited(object? sender, PointerEventArgs e)
    {
        if (Shell is { } shell) _ = shell.Jog.StopPositioningLaserAsync();
    }

    private void OnConsoleInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Send enforces its own CanExecute (connected, idle, non-empty input) — Enter is just a
        // shortcut for the same button click, so a stale Enter while it is disabled is swallowed
        // the same way a disabled button click would be, not surfaced as an error.
        if (e.Key != Key.Enter || Shell is not { } shell) return;
        shell.Console.Send.Execute().Subscribe(_ => { }, _ => { });
    }
}
