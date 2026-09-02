using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>The materials library is a window, not a screen, so it is the window that opens it —
    /// same route the navigation rail's Materiály button takes.</summary>
    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.OpenMaterials();
    }
}
