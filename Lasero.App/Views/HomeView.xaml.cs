using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Serilog;

namespace Lasero.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.OpenMaterials();
    }

    /// <summary>A three-dot button has to open its own menu — WPF only shows a ContextMenu on right
    /// click by default, and the mockup's affordance is a left click.</summary>
    private void OnRecentProjectMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://lasero.net/napoveda") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not open the help page");
        }
    }
}
