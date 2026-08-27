using System.Windows;

namespace Lasero.App;

public partial class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
