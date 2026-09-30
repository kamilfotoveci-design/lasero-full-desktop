using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Lasero.Tests;

/// <summary>
/// The navigation rail, the designer tool rail, the bottom dock and the status strip read as one
/// family only while their glyphs share a grid and a size. These tests keep that from drifting
/// back into a mix of hand-drawn and imported shapes.
/// </summary>
public sealed class NavigationIconSetTests
{
    private static readonly string[] RailSet =
    {
        "Home", "Design", "Materials", "Device", "Chat", "Settings",
        "Select", "Text", "Rectangle", "Ellipse", "Line", "Import",
        "Project", "Connect", "Disconnect", "Refresh", "Warning",
    };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LaseroDesktop.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Icons() => File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "Icons.xaml"));

    [Theory]
    [MemberData(nameof(Keys))]
    public void RailGlyphsParseAndStayInsideTheTwentyFourGrid(string key)
    {
        var match = Regex.Match(Icons(), $@"x:Key=""Glyph\.{key}"">([^<]+)</Geometry>");
        Assert.True(match.Success, $"Glyph.{key} is not defined as a plain Geometry.");

        var bounds = Geometry.Parse(match.Groups[1].Value).GetRenderBounds(new Pen(Brushes.Black, 1.75));

        // Stroke overhang at sharp joins is allowed a little slack beyond the 1..23 live area.
        Assert.InRange(bounds.Left, -0.5, 24);
        Assert.InRange(bounds.Top, -0.5, 24);
        Assert.InRange(bounds.Right, 0, 24.5);
        Assert.InRange(bounds.Bottom, 0, 24.5);
        Assert.InRange(bounds.Width, 12, 24.5);
        Assert.InRange(bounds.Height, 8, 24.5);
    }

    [Fact]
    public void NavigationRailAndToolRailDrawTheirGlyphsAtTheSameSize()
    {
        var main = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "MainWindow.xaml"));
        var rail = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "DesignerToolRail.xaml"));

        var railGlyph = Regex.Match(rail, @"x:Key=""RailGlyph""[\s\S]*?</Style>").Value;
        Assert.Contains("Size.Icon.Lg", railGlyph);

        // Every labelled navigation entry — including Lasero Chat, which used to be a photo — is an
        // IconLabel at that same size.
        var nav = Regex.Matches(main, @"<components:IconLabel IconData=""\{StaticResource Glyph\.(Home|Design|Materials|Device|Chat|Settings)\}""[^>]*TextMargin=""9,0,0,0""[^>]*>");
        Assert.Equal(6, nav.Count);
        foreach (Match entry in nav)
            Assert.Contains("Size.Icon.Lg", entry.Value);
    }

    [Fact]
    public void ChatHasItsOwnNavEntryThatOpensTheFullChatScreen()
    {
        var main = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "MainWindow.xaml"));
        var chat = Regex.Match(main, @"<Button Style=""\{StaticResource NavButton\.Chat\}""[\s\S]*?</Button>").Value;
        Assert.Contains("Command=\"{Binding ShowChatCommand}\"", chat);
        Assert.Contains("AutomationProperties.Name=\"Chat\"", chat);
        Assert.Contains("Glyph.Chat", chat);
        Assert.Contains("Text=\"Chat\"", chat);
    }

    public static IEnumerable<object[]> Keys() => RailSet.Select(k => new object[] { k });
}
