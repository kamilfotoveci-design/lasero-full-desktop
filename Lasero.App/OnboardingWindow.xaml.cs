using System.Windows;

namespace Lasero.App;

public partial class OnboardingWindow : Window
{
    public bool DontShowAgainChecked => DontShowAgain.IsChecked == true;

    public OnboardingWindow()
    {
        InitializeComponent();
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
