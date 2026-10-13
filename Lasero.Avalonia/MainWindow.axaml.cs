using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Lasero.Avalonia;

public sealed partial class MainWindow : Window
{
    private AvaloniaPath? maximizeGlyph;
    private bool onboardingChecked;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        maximizeGlyph = this.FindControl<AvaloniaPath>("MaximizeGlyph");
        UpdateMaximizeGlyph();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateMaximizeGlyph();
        };
        Opened += (_, _) => ShowOnboardingIfFirstRun();
    }

    // Port of Lasero.App/MainWindow.xaml.cs's own onboarding-seen marker check — same file, same
    // path, so a machine that has already seen WPF's onboarding does not see Avalonia's again (and
    // vice versa). No device/account state is queried here; the screen itself is purely informational.
    private void ShowOnboardingIfFirstRun()
    {
        if (onboardingChecked) return;
        onboardingChecked = true;

        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "onboarding-seen");
        if (File.Exists(marker)) return;

        var onboarding = new OnboardingWindow();
        onboarding.Closed += (_, _) =>
        {
            if (!onboarding.DontShowAgainChecked) return;
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        };
        onboarding.ShowDialog(this);
    }

    // Mirrors WPF MainWindow's MaximizeGlyph swap: the button always toggles maximize/restore, only
    // the glyph communicates which action pressing it will perform next.
    private void UpdateMaximizeGlyph()
    {
        if (maximizeGlyph is null) return;
        maximizeGlyph.Data = (global::Avalonia.Media.Geometry)this.FindResource(
            WindowState == WindowState.Maximized ? "Glyph.Restore" : "Glyph.Maximize")!;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
            return;

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
