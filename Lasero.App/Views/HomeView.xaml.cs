using System.Windows.Controls;
using System.Windows;

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
}
