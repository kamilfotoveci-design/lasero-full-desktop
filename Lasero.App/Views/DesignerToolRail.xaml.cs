using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Views;

/// <summary>
/// The Designer workspace's 56px rail. Screens above and below, drawing tools in the middle, with a
/// hairline between the groups. Materials and Settings open windows rather than switching screens,
/// so those two go through the window the same way the full sidebar's entries do.
/// </summary>
public partial class DesignerToolRail : UserControl
{
    public DesignerToolRail()
    {
        InitializeComponent();
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenMaterials();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenSettings();
    }
}
