using System.Windows;
using Lasero.App.ViewModels;

namespace Lasero.App;

/// <summary>
/// The manual machine controls, as a panel that sits beside the design instead of a screen the
/// operator has to switch to. It hosts MachinePanelView verbatim, so nothing about jogging, homing,
/// unlocking, origin or the positioning beam changes — including every CanExecute gate.
/// </summary>
public partial class MachineControlWindow : Window
{
    public MachineControlWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Opens against the owner's right edge, next to the inspector the operator came from,
    /// and clamped to the screen so a window dragged onto a second monitor still lands on-screen.</summary>
    public void PositionBeside(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var left = owner.Left + owner.ActualWidth - Width - 24;
        var top = owner.Top + 96;

        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
