using System.IO;

namespace Lasero.Tests;

public sealed class MainWindowInteractionTests
{
    [Fact]
    public void TransformFieldsCommitKeyboardValuesOnEnter()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml.cs"));

        Assert.Equal(5, CountOccurrences(xaml, "KeyDown=\"OnTransformFieldKeyDown\""));
        Assert.Equal(5, CountOccurrences(xaml, "UpdateSourceTrigger=LostFocus"));
        Assert.Contains("GetBindingExpression(TextBox.TextProperty)?.UpdateSource()", codeBehind, StringComparison.Ordinal);
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
