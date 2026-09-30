using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using Lasero.App.Components;

namespace Lasero.Tests;

/// <summary>
/// Pins the visual rules that a compiler and a screenshot diff both miss: a rounded card whose square
/// children paint over its corners, a literal radius standing in for a token, tab strips that clip
/// their own pills. Each rule was a defect on screen before it became a test.
/// </summary>
public sealed class VisualConsistencyTests
{
    // ---------------------------------------------------------------- RoundedClip geometry

    [Fact]
    public void InnerRadiusIsTheOuterRadiusMinusTheBorderOnEachEdge()
    {
        var inner = RoundedClip.InnerRadius(12, horizontalEdge: 1, verticalEdge: 1);
        Assert.Equal(11, inner.X);
        Assert.Equal(11, inner.Y);
    }

    [Fact]
    public void InnerRadiusNeverGoesNegative()
    {
        var inner = RoundedClip.InnerRadius(2, horizontalEdge: 5, verticalEdge: 5);
        Assert.Equal(0, inner.X);
        Assert.Equal(0, inner.Y);
    }

    [Fact]
    public void ClipRemovesTheSquareCornersButKeepsTheBody()
    {
        var clip = RoundedClip.BuildClip(new Size(200, 100), new CornerRadius(12), new Thickness(1));

        // The exact corner pixel is outside a rounded rectangle; a point well inside is not.
        Assert.False(clip.FillContains(new Point(0.5, 0.5)));
        Assert.False(clip.FillContains(new Point(199.5, 0.5)));
        Assert.False(clip.FillContains(new Point(0.5, 99.5)));
        Assert.False(clip.FillContains(new Point(199.5, 99.5)));
        Assert.True(clip.FillContains(new Point(100, 50)));
        // Along the straight edges the clip reaches the full extent, so nothing else is trimmed.
        Assert.True(clip.FillContains(new Point(100, 0.5)));
        Assert.True(clip.FillContains(new Point(0.5, 50)));
    }

    [Fact]
    public void ClipHonoursPerCornerRadii()
    {
        // Only the top corners rounded: the footer of a card that stays square at the bottom.
        var clip = RoundedClip.BuildClip(new Size(200, 100), new CornerRadius(12, 12, 0, 0), new Thickness(0));

        Assert.False(clip.FillContains(new Point(0.5, 0.5)));
        Assert.True(clip.FillContains(new Point(0.5, 99.5)));
        Assert.True(clip.FillContains(new Point(199.5, 99.5)));
    }

    // ---------------------------------------------------------------- source-level rules

    [Fact]
    public void ProjectThumbnailCardClipsItsContentToTheCardRadius()
    {
        var doc = XDocument.Parse(ReadApp("Components", "ProjectThumbCard.xaml"));
        var xName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var surface = doc.Descendants().Single(e => (string?)e.Attribute(xName) == "Surface");

        Assert.Equal("{StaticResource Radius.Lg}", (string?)surface.Attribute("CornerRadius"));
        // ClipToBounds clips to the rectangle, so it is not a substitute for the rounded clip.
        Assert.Null(surface.Attribute("ClipToBounds"));

        var content = surface.Elements().Single(e => !e.Name.LocalName.Contains('.'));
        var clip = content.Attributes().Single(a => a.Name.LocalName == "RoundedClip.ToParentBorder");
        Assert.Equal("True", clip.Value);
    }

    [Theory]
    // Files whose rounded surface holds full-bleed square fills (a thumbnail well, a footer band, list
    // rows with a hover wash). Each must clip its content to the surface instead of faking a radius.
    [InlineData("Components/ProjectThumbCard.xaml")]
    [InlineData("LaseroDialogWindow.xaml")]
    [InlineData("Views/HomeView.xaml")]
    [InlineData("Views/DeviceSetup/DeviceWizardOverlay.xaml")]
    public void RoundedSurfacesWithFullBleedContentUseRoundedClip(string relativePath)
    {
        var text = ReadApp(relativePath.Split('/'));
        Assert.Contains("RoundedClip.ToParentBorder=\"True\"", text);
    }

    [Fact]
    public void NoScalarCornerRadiusLiteralsOutsideTheTokens()
    {
        // Scalar radii come from Radius.*; composites (a footer rounded on two corners) stay literal
        // because XAML has no arithmetic. "0" is square and is what a caption-less WindowChrome takes.
        var offenders = new List<string>();
        var literal = new Regex("CornerRadius=\"(?<v>[^\"{]+)\"", RegexOptions.Compiled);
        foreach (var file in AppXamlFiles())
        {
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            foreach (Match m in literal.Matches(text))
            {
                var v = m.Groups["v"].Value.Trim();
                if (v == "0" || v.Contains(',')) continue;
                offenders.Add($"{Path.GetFileName(file)}: CornerRadius=\"{v}\"");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void MaterialTabsDoNotUseTabItemMargin()
    {
        // TabPanel arranges each tab at a desired size that already includes its margin and the tab then
        // subtracts the margin again, so a margin on a TabItem makes the pill narrower than its text.
        var style = StyleBlock(ReadApp("MaterialsWindow.xaml"), "x:Key=\"MaterialTab\"");

        Assert.NotEmpty(style);
        Assert.DoesNotContain("Property=\"Margin\"", style);
    }

    [Fact]
    public void TabContentDoesNotInheritTheHeaderColourOrWeight()
    {
        // The content of a TabItem is its logical child, so Foreground/FontWeight set on the item flow into
        // the whole page (the empty state turned red and semibold on the selected tab).
        var style = StyleBlock(ReadApp("MaterialsWindow.xaml"), "x:Key=\"MaterialTab\"");
        var itemLevelSetters = Regex.Matches(style, "<Setter Property=\"(?<p>[A-Za-z.]+)\"")
            .Select(m => m.Groups["p"].Value).ToList();

        Assert.DoesNotContain("Foreground", itemLevelSetters);
        Assert.DoesNotContain("FontWeight", itemLevelSetters);
    }

    [Fact]
    public void BadgesBesideButtonsShareTheButtonHeight()
    {
        var aligned = StyleBlock(ReadApp("Theme", "LaseroTheme.xaml"), "x:Key=\"Badge.Aligned\"");
        Assert.Contains("Size.Control.Base", aligned);

        // The two header rows that put a status chip next to a button use it.
        Assert.Contains("Badge.Aligned", ReadApp("MaterialsWindow.xaml"));
        Assert.Contains("Badge.Aligned", ReadApp("Views", "DeviceView.xaml"));
    }

    [Fact]
    public void TextBoxDoesNotPadItsContentTwice()
    {
        // TextBoxBase applies Padding to the content host itself; a Margin bound to Padding as well put
        // the text 20px in while a ComboBox beside it sat at 10px.
        var theme = ReadApp("Theme", "LaseroTheme.xaml");
        var textBox = Regex.Match(theme, "<Style TargetType=\"TextBox\">.*?</Style>", RegexOptions.Singleline).Value;

        Assert.NotEmpty(textBox);
        Assert.DoesNotContain("Margin=\"{TemplateBinding Padding}\"", textBox);
    }

    [Fact]
    public void NumericAndSelectFieldsShareTheTextFieldSurface()
    {
        var unit = StyleBlock(ReadApp("Theme", "SharedUiStyles.xaml"), "x:Key=\"UnitField.Shell\"");
        var select = StyleBlock(ReadApp("Theme", "LaseroTheme.xaml"), "x:Key=\"Field.Select\"");

        foreach (var style in new[] { unit, select })
        {
            Assert.Matches("Property=\"Background\" Value=\"\\{DynamicResource Brush\\.Field\\}\"", style);
            Assert.Matches("Property=\"BorderBrush\" Value=\"\\{DynamicResource Brush\\.PanelBorder\\}\"", style);
        }
    }

    [Fact]
    public void FocusableCanvasHostDoesNotDrawTheStockDottedRectangle()
    {
        Assert.Contains("FocusVisualStyle=\"{x:Null}\"", ReadApp("Controls", "SceneCanvas.xaml"));
    }

    // ---------------------------------------------------------------- helpers

    private static string StyleBlock(string xaml, string keyAttribute)
    {
        var start = xaml.IndexOf(keyAttribute, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : xaml[start..end];
    }

    private static string ReadApp(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot(), "Lasero.App" }.Concat(relative).ToArray()));

    private static IEnumerable<string> AppXamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindRoot(), "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
