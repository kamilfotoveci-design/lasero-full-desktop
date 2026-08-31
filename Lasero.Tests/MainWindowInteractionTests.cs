using System.IO;

namespace Lasero.Tests;

public sealed class MainWindowInteractionTests
{
    [Fact]
    public void SelectionBarValueFieldsCommitKeyboardValuesOnEnter()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "SelectionPropertiesBar.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "SelectionPropertiesBar.xaml.cs"));

        string[] deferredFields =
        [
            "SelectedX", "SelectedY", "SelectedWidth", "SelectedHeight", "SelectedRotation",
            "SelectedTextValue", "SelectedTextHeight",
        ];

        Assert.Equal(deferredFields.Length, CountOccurrences(xaml, "KeyDown=\"OnValueFieldKeyDown\""));
        Assert.Contains("GetBindingExpression(TextBox.TextProperty)?.UpdateSource()", codeBehind, StringComparison.Ordinal);

        // Every one of these fields defers its binding to LostFocus, so a half-typed number never
        // reaches the scene. That is what makes the Enter handler necessary: without it a typed value
        // would sit uncommitted until focus moved.
        foreach (var property in deferredFields)
        {
            var binding = xaml[xaml.IndexOf($"Scene.{property},", StringComparison.Ordinal)..];
            Assert.Contains("UpdateSourceTrigger=LostFocus", binding[..binding.IndexOf('}')], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TooltipsForwardTheirForegroundToTextContent()
    {
        var theme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Theme", "LaseroTheme.xaml"));

        Assert.Contains("<Style TargetType=\"ToolTip\">", theme, StringComparison.Ordinal);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", theme, StringComparison.Ordinal);

        // The surface has to agree with the app-wide implicit TextBlock style rather than fight it.
        // WPF resolves implicit styles for template-built elements against Application.Resources only,
        // so the TextBlock a ContentPresenter builds for a plain string tooltip always takes the
        // app-wide near-black foreground and TextElement.Foreground on the template loses. A dark
        // tooltip surface therefore rendered as a solid black block with no readable text.
        var style = theme[theme.IndexOf("<Style TargetType=\"ToolTip\">", StringComparison.Ordinal)..];
        style = style[..style.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{StaticResource Brush.TextPrimary}\" />", style, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{StaticResource Brush.Panel}\" />", style, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"{StaticResource Brush.OnAccent}\"", style, StringComparison.Ordinal);

        // Long tooltips wrap. An implicit DataTemplate, unlike an implicit Style, is resolved through
        // the normal lookup from the ContentPresenter's own position, so declaring it in the template
        // actually takes effect.
        Assert.Contains("<DataTemplate DataType=\"{x:Type system:String}\">", style, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", style, StringComparison.Ordinal);
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
