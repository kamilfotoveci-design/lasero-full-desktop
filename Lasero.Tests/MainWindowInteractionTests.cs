using System.IO;

namespace Lasero.Tests;

public sealed class MainWindowInteractionTests
{
    [Fact]
    public void InspectorValueFieldsCommitKeyboardValuesOnEnter()
    {
        // The transform fields live in the right inspector, not the toolbar above the canvas: the
        // toolbar could not hold them on one row at any window size.
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerInspectorView.xaml.cs"));

        string[] deferredFields =
        [
            "SelectedX", "SelectedY", "SelectedWidth", "SelectedHeight", "SelectedRotation",
            "SelectedTextValue", "SelectedTextHeight",
        ];

        Assert.Equal(deferredFields.Length, CountOccurrences(xaml, "KeyDown=\"OnTransformFieldKeyDown\""));
        Assert.Contains("GetBindingExpression(TextBox.TextProperty)?.UpdateSource()", codeBehind, StringComparison.Ordinal);

        // Every one of these fields defers its binding to LostFocus, which is what makes the Enter
        // handler necessary: without it a typed value would sit uncommitted until focus moved.
        foreach (var property in deferredFields)
        {
            var binding = xaml[xaml.IndexOf($"Scene.{property},", StringComparison.Ordinal)..];
            Assert.Contains("UpdateSourceTrigger=LostFocus", binding[..binding.IndexOf('}')], StringComparison.Ordinal);
        }

        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        Assert.DoesNotContain("OnTransformFieldKeyDown", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void TooltipsForwardTheirForegroundToTextContent()
    {
        var theme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Theme", "LaseroTheme.xaml"));

        Assert.Contains("<Style TargetType=\"ToolTip\">", theme, StringComparison.Ordinal);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", theme, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0; index += fragment.Length)
            count++;
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Lasero repository root was not found.");
    }
}
