using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The 2026-10 type raise: bigger sizes, one scale in one place, and real Inter weights instead of a
/// font that silently falls back to Segoe.
/// </summary>
[Collection("WpfUi")]
public sealed class TypographyTests
{
    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }

    private static string Theme() =>
        XmlComment.Replace(File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml")), "");

    private static double Size(string key)
    {
        var m = Regex.Match(Theme(), $@"<system:Double x:Key=""{Regex.Escape(key)}"">(?<v>[0-9.]+)</system:Double>");
        Assert.True(m.Success, key);
        return double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheScaleIsRaisedAndLivesInOnePlace()
    {
        Assert.Equal(15, Size("Size.Text.Body"));
        Assert.Equal(14, Size("Size.Text.Meta"));
        Assert.Equal(13, Size("Size.Text.Caption"));
        Assert.Equal(17, Size("Size.Text.Section"));
        Assert.InRange(Size("Size.Text.Heading"), 20, 22);
        Assert.InRange(Size("Size.Text.Title"), 30, 32);
        // Reading text never goes below Meta; Caption (13) is the single step below, for the status strip,
        // tooltips, rulers and badges.
        Assert.True(Size("Size.Text.Caption") >= 13);
        Assert.True(Size("Size.Text.Meta") >= 14);
    }

    [Fact]
    public void ControlsAndHitTargetsAreLarger()
    {
        Assert.True(Size("Size.Control.Base") >= 40);
        Assert.True(Size("Size.Control.Primary") >= 44);
        Assert.True(Size("Size.Control.Rail") >= 44);
        Assert.True(Size("Size.Control.Row") >= 40);
        Assert.True(Size("Size.Control.Large") >= 48);
    }

    [Fact]
    public void MutedTextIsNoLighterThanTheSecondaryGray()
    {
        var m = Regex.Match(Theme(), @"x:Key=""Brush\.TextMuted"" Color=""#(?<h>[0-9A-F]{6})""");
        Assert.True(m.Success);
        var v = Convert.ToInt32(m.Groups["h"].Value[..2], 16);
        Assert.True(v <= 0x6E, "muted text must be no lighter than #6E6E73");
    }

    [Fact]
    public void InterIsWiredIntoTheThemeAndAllFourWeightsAreBundled()
    {
        var theme = Theme();
        foreach (var key in new[] { "Font.Ui", "Font.Display", "Font.Numeric" })
            Assert.Contains("x:Key=\"" + key + "\">pack://application:,,,/Lasero.App;component/Assets/Fonts/#Inter", theme, StringComparison.Ordinal);

        var fonts = Path.Combine(Root(), "Lasero.App", "Assets", "Fonts");
        foreach (var file in new[] { "Inter-Regular.ttf", "Inter-Medium.ttf", "Inter-SemiBold.ttf", "Inter-Bold.ttf", "Inter-LICENSE.txt" })
            Assert.True(File.Exists(Path.Combine(fonts, file)), file);
        var project = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Lasero.App.csproj"));
        foreach (var file in new[] { "Inter-Regular.ttf", "Inter-Medium.ttf", "Inter-SemiBold.ttf", "Inter-Bold.ttf" })
            Assert.Contains("Assets" + Path.DirectorySeparatorChar + "Fonts" + Path.DirectorySeparatorChar + file, project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(600)]
    [InlineData(700)]
    public void EachWeightResolvesToARealInterFaceNotASynthesisedOne(int weight)
    {
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            var family = (FontFamily)Application.Current.FindResource("Font.Ui");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "no glyph typeface");
            var names = string.Join(" / ", glyphs.FamilyNames.Values.Concat(glyphs.Win32FamilyNames.Values));
            Assert.Contains("Inter", names, StringComparison.Ordinal);
            Assert.Equal(weight, glyphs.Weight.ToOpenTypeWeight());
        });
    }
}
