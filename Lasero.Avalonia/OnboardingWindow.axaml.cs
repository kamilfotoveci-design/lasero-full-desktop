using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Lasero.Avalonia;

/// <summary>Port of Lasero.App/OnboardingWindow.xaml.cs.</summary>
public sealed partial class OnboardingWindow : Window
{
    public OnboardingWindow() => AvaloniaXamlLoader.Load(this);

    public bool DontShowAgainChecked => this.FindControl<CheckBox>("DontShowAgain")?.IsChecked == true;

    private void OnStartClick(object? sender, RoutedEventArgs e) => Close(true);
}
