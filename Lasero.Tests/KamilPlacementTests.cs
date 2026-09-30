using System.IO;

namespace Lasero.Tests;

/// <summary>Pins where KAMIL may appear and where its resting head sits. Both are layout facts that
/// only show up in a running window, so the tests read the XAML and code-behind the same way the
/// rest of the navigation tests do.</summary>
public sealed class KamilPlacementTests
{
    [Fact]
    public void AssistantHostIsVisibleOnlyOnTheDesignerScreen()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml"));

        var start = xaml.IndexOf("<kamil:KamilAssistantHost ", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow must declare the assistant host.");
        var tag = xaml[start..xaml.IndexOf('>', start)];

        // The host's own DataContext is the Kamil view model, so the screen has to be read from the
        // window's DataContext rather than from the element's own.
        Assert.Contains("Visibility=\"{Binding DataContext.CurrentScreen", tag, StringComparison.Ordinal);
        Assert.Contains("RelativeSource={RelativeSource AncestorType=Window}", tag, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Designer", tag, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "<kamil:KamilAssistantHost "));

        // Declared inside the canvas column, never spanning the splitter and inspector columns.
        Assert.DoesNotContain("ColumnSpan", tag, StringComparison.Ordinal);
        Assert.True(xaml.IndexOf("x:Name=\"WorkspaceArea\"", StringComparison.Ordinal) < start);
        Assert.DoesNotContain("DesignerInspector\" Path=\"ActualWidth", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RestingHeadIsNotReservedTwiceAgainstTheInspectorAndZoomCluster()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "Kamil", "KamilAssistantHost.xaml.cs"));

        var start = code.IndexOf("private Rect GetUsableBounds", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = code[start..code.IndexOf("public static Point ClampPosition", start, StringComparison.Ordinal)];

        // MainWindow's AssistantClearance margin already accounts for both. Counting them again here
        // is what moved the head from the bottom-right corner into the middle of the canvas.
        Assert.DoesNotContain("InspectorColumn", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InspectorSplitter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CanvasViewControls", body, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LaseroDesktop.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate LaseroDesktop.sln.");
    }
}

public sealed class KamilHeadAnchorTests
{
    // Mirrors MainWindow's grid: nav rail | canvas (*, min 420) | splitter (6) | inspector.
    private const double SplitterWidth = 6;
    private const double StatusStripHeight = 48;
    private const double TitleBarHeight = 60;
    private const double ControlsHeight = 40;

    [Theory]
    [InlineData(1080, 640, 56, 320)]
    [InlineData(1366, 768, 56, 320)]
    [InlineData(1480, 900, 56, 320)]
    [InlineData(1920, 1032, 56, 320)]
    [InlineData(1480, 900, 56, 560)]
    [InlineData(1000, 700, 164, 320)]
    public void HeadRestsAtTheCanvasBottomRightAboveTheZoomClusterForAnyColumnWidths(
        double windowWidth, double windowHeight, double navWidth, double inspectorWidth)
    {
        var canvasWidth = Math.Max(420, windowWidth - navWidth - SplitterWidth - inspectorWidth);
        var rowHeight = windowHeight - TitleBarHeight - StatusStripHeight;

        // The host lives in the canvas column, so its size is the canvas's, minus its margin.
        var margin = Lasero.App.Converters.AssistantClearanceConverter.Clearance(ControlsHeight);
        var hostWidth = canvasWidth - margin.Left - margin.Right;
        var hostHeight = rowHeight - margin.Top - margin.Bottom;

        var head = Lasero.App.Views.Kamil.KamilAssistantHost.HeadOrigin(hostWidth, hostHeight);
        var size = Lasero.App.Views.Kamil.KamilAssistantHost.HeadSize;
        var headRightInCanvas = margin.Left + head.X + size.Width;
        var headBottomInRow = margin.Top + head.Y + size.Height;

        Assert.Equal(canvasWidth - 20, headRightInCanvas, 3);       // shares the cluster's right edge
        Assert.True(headRightInCanvas <= canvasWidth);              // never over splitter or inspector
        Assert.True(headBottomInRow <= rowHeight - 20 - ControlsHeight - 12 + 0.001); // above the cluster
        Assert.True(headBottomInRow < rowHeight);                   // never over the status strip
    }

    [Fact]
    public void ClearanceIgnoresWhateverTheInspectorMeasures()
    {
        var converter = new Lasero.App.Converters.AssistantClearanceConverter();
        var withHeight = (System.Windows.Thickness)converter.Convert([40.0], typeof(System.Windows.Thickness), null!, System.Globalization.CultureInfo.InvariantCulture);
        var unmeasured = (System.Windows.Thickness)converter.Convert([0.0], typeof(System.Windows.Thickness), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(20, withHeight.Right);
        Assert.Equal(20, unmeasured.Right);
        Assert.Equal(72, withHeight.Bottom);
        Assert.Equal(32, unmeasured.Bottom);
    }
}
