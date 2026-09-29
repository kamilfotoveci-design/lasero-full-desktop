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
    }

    [Fact]
    public void RestingHeadIsNotReservedTwiceAgainstTheInspectorAndZoomCluster()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "Kamil", "KamilAssistantHost.xaml.cs"));

        var start = code.IndexOf("private Rect GetUsableBounds", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = code[start..code.IndexOf("private Point ClampPosition", start, StringComparison.Ordinal)];

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
