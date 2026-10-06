using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Lasero.App.Views;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The shape picker of the Návrh rail must never be on screen outside Návrh. Regression: it was left open when
/// the rail was Collapsed (a screen change does not unload it) and floated over the Home navigation.
/// </summary>
[Collection("WpfUi")]
public sealed class PopupLifecycleTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 600,
            Height = 600,
            Content = content,
        };
        window.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return window;
    }

    private static IEnumerable<Popup> Popups(DependencyObject root)
    {
        if (root is Popup p) yield return p;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Popups(child)) yield return found;
        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var found in Popups(VisualTreeHelper.GetChild(root, i))) yield return found;
    }

    [Fact]
    public void NothingIsOpenWhenTheRailAndHomeAreRealised()
    {
        Ui.Invoke(() =>
        {
            var rail = new DesignerToolRail();
            var home = new HomeView();
            var panel = new StackPanel();
            panel.Children.Add(rail);
            panel.Children.Add(home);
            var window = Host(panel);
            try
            {
                Assert.False(rail.ShapePickerPopup.IsOpen, "the shape picker is open at startup");
                Assert.DoesNotContain(Popups(panel), p => p.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CollapsingTheRailClosesAnOpenShapePicker()
    {
        Ui.Invoke(() =>
        {
            var rail = new DesignerToolRail();
            var window = Host(rail);
            try
            {
                rail.ShapePickerPopup.IsOpen = true;
                Assert.True(rail.ShapePickerPopup.IsOpen);

                rail.Visibility = Visibility.Collapsed; // what leaving Návrh does
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(rail.ShapePickerPopup.IsOpen, "the picker stayed open after the rail was collapsed");

                // Showing the rail again must not bring it back.
                rail.Visibility = Visibility.Visible;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(rail.ShapePickerPopup.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThePickerIsDismissedByOutsideClickEscapeAndOwnerVisibilityInSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "Lasero.App"))) root = root.Parent!;
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "Lasero.App", "Views", "DesignerToolRail.xaml"));
        var code = File.ReadAllText(Path.Combine(root.FullName, "Lasero.App", "Views", "DesignerToolRail.xaml.cs"));
        Assert.Contains("StaysOpen=\"False\"", xaml, StringComparison.Ordinal);          // outside click
        Assert.DoesNotContain("IsOpen=\"True\"", xaml, StringComparison.Ordinal);       // never at startup
        Assert.Contains("Key.Escape", code, StringComparison.Ordinal);                  // Esc
        Assert.Contains("IsVisibleChanged", code, StringComparison.Ordinal);            // owner hidden
    }

    [Fact]
    public void NoPopupInTheAppIsOpenByDefaultOrStaysOpenWithoutAnOwnerBinding()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "Lasero.App"))) root = root.Parent!;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"<Popup\b[^>]*>"))
            {
                Assert.DoesNotContain("IsOpen=\"True\"", m.Value, StringComparison.Ordinal);
                Assert.DoesNotContain("StaysOpen=\"True\"", m.Value, StringComparison.Ordinal);
            }
        }
    }
}
