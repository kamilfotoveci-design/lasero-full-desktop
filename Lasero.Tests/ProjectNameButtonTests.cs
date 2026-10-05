using System.IO;
using System.Text.RegularExpressions;

namespace Lasero.Tests;

/// <summary>
/// The project name in the title bar and its chevron are one hit target. The chevron used to be a
/// separate MenuItem beside a plain TextBlock, so clicking the name did nothing and the owner had to
/// aim at the arrow. These pin the structure at source level (the app cannot be loaded in a test).
/// </summary>
public sealed class ProjectNameButtonTests
{
    private static string Xaml() =>
        File.ReadAllText(Path.Combine(FindRoot(), "Lasero.App", "MainWindow.xaml"));

    private static string Button()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("<ToggleButton x:Name=\"ProjectNameButton\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Project name must be a ToggleButton.");
        var end = xaml.IndexOf("</ToggleButton>\n", start, StringComparison.Ordinal);
        if (end < 0) end = xaml.IndexOf("</ToggleButton>\r\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return xaml[start..end];
    }

    [Fact]
    public void NameAndChevronLiveInsideTheSameButton()
    {
        var button = Button();

        Assert.Contains("Text=\"{Binding ProjectName}\"", button, StringComparison.Ordinal);
        Assert.Contains("Glyph.ChevronDown", button, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", button, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=", button, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDirtyMarkerStaysInsideTheSameTarget()
    {
        Assert.Contains("Binding IsDirty", Button(), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingClickableOrChevronLikeRemainsOutsideTheButton()
    {
        var xaml = Xaml();
        var outside = xaml.Replace(Button(), string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("ProjectMenuButton", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding ProjectName}\"", outside, StringComparison.Ordinal);
    }

    [Fact]
    public void ButtonIsVisibleToTheChromeHitTest()
    {
        Assert.Contains("shell:WindowChrome.IsHitTestVisibleInChrome=\"True\"", Button(), StringComparison.Ordinal);
    }

    [Fact]
    public void ButtonHasTooltipAndAutomationNameDescribingWhatTheMenuHolds()
    {
        var button = Button();

        Assert.Contains("AutomationProperties.Name=\"Projekt\"", button, StringComparison.Ordinal);
        Assert.Contains("<ToggleButton.ToolTip>", button, StringComparison.Ordinal);
        Assert.Contains("Projekt - nový, otevřít, uložit", button, StringComparison.Ordinal);
        Assert.DoesNotContain("?", button.Replace("?>", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("!", button.Replace("<!--", string.Empty), StringComparison.Ordinal);

        // The tooltip must not promise what the menu lacks.
        Assert.DoesNotContain("přejmenovat", button, StringComparison.OrdinalIgnoreCase);
        foreach (var command in new[] { "NewProjectCommand", "OpenProjectCommand", "SaveProjectCommand", "SaveProjectAsCommand" })
            Assert.Contains($"Command=\"{{Binding {command}}}\"", button, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyboardOpensTheMenuAndTheButtonKeepsFocusRingAndHitSize()
    {
        var xaml = Xaml();
        var code = File.ReadAllText(Path.Combine(FindRoot(), "Lasero.App", "MainWindow.xaml.cs"));

        Assert.Contains("PreviewKeyDown=\"OnProjectNameButtonKeyDown\"", Button(), StringComparison.Ordinal);
        Assert.Contains("Key.F4", code, StringComparison.Ordinal);
        Assert.Contains("ModifierKeys.Alt", code, StringComparison.Ordinal);

        var style = xaml[xaml.IndexOf("x:Key=\"ProjectNameButton\"", StringComparison.Ordinal)..];
        style = style[..style.IndexOf("</Style>", StringComparison.Ordinal)];
        var minHeight = Regex.Match(style, @"MinHeight"" Value=""([0-9]+)""");
        Assert.True(minHeight.Success && int.Parse(minHeight.Groups[1].Value) >= 32);
        Assert.Contains("Radius.Sm", style, StringComparison.Ordinal);
        Assert.Contains("Brush.HoverWash", style, StringComparison.Ordinal);
        Assert.Contains("components:FocusVisual.IsVisible", style, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsPressed\"", style, StringComparison.Ordinal);
        Assert.Contains("Value=\"Arrow\"", style, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LaseroDesktop.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
