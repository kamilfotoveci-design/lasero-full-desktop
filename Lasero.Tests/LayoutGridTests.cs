using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The layout grid, enforced the way ThemeTokenTests enforces tokens. Every literal Margin and Padding in markup
/// is a multiple of 4 (8 for sections, 4 for tight pairs: an icon and its label, a label and its value). Anything
/// else is how a screen ends up with 10, 14 and 22 next to each other. Literal FontSize and CornerRadius are banned
/// by ThemeTokenTests and VisualConsistencyTests; this file adds the spacing half. Optical corrections that cannot
/// sit on the grid (a focus ring drawn 3 px outside its control) are listed below with the reason.
/// </summary>
public sealed class LayoutGridTests
{
    private static readonly Regex Spacing = new(
        @"(?:\b(?<attr>Margin|Padding)=""|Property=""(?<attr>Margin|Padding)""\s+Value="")(?<value>[^""{}]*)""",
        RegexOptions.Compiled);

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>File name and exact value. Each entry is an optical offset, never a spacing choice.</summary>
    private static readonly HashSet<(string File, string Value)> Optical =
    [
        ("SharedUiStyles.xaml", "-6,0,0,0"),// inline title input: pulls the text back over its own 6 px padding
        ("DesignerToolRail.xaml", "-1,0,0,0"), // selected marker overlaps the pill edge by its half width
        ("DesignerToolRail.xaml", "-3"),    // rail focus ring, same rule as the checkbox
    ];

    private static IEnumerable<string> MarkupFiles()
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
            // LaseroTheme.xaml holds control templates: focus-ring offsets, glyph insets, scroll-thumb insets. Those are
            // device-pixel geometry of one control, not layout, and are pinned by the render tests (TintRenderTests).
            .Where(path => !path.EndsWith("LaseroTheme.xaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    public static IEnumerable<string> OffGridValues(string markup, string fileName)
    {
        var text = XmlComment.Replace(markup, string.Empty);
        foreach (Match match in Spacing.Matches(text))
        {
            var raw = match.Groups["value"].Value.Trim();
            if (raw.Length == 0 || Optical.Contains((fileName, raw))) continue;
            var parts = raw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => !double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))) continue; // Auto or a binding
            var onGrid = parts.All(part =>
            {
                var number = double.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                return Math.Abs(number % 4) < 0.0001;
            });
            if (!onGrid) yield return $"{match.Groups["attr"].Value}=\"{raw}\"";
        }
    }

    [Fact]
    public void EveryLiteralMarginAndPaddingSitsOnTheFourPixelGrid()
    {
        var offenders = new List<string>();
        foreach (var file in MarkupFiles())
        {
            var name = Path.GetFileName(file);
            offenders.AddRange(OffGridValues(File.ReadAllText(file), name).Select(value => $"{name}: {value}"));
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} spacing values are off the 4 px grid:\n" + string.Join('\n', offenders.Distinct().Take(60)));
    }

    [Theory]
    [InlineData("Margin=\"0,0,10,0\"", true)]
    [InlineData("Padding=\"14\"", true)]
    [InlineData("Margin=\"0,0,12,8\"", false)]
    [InlineData("<Setter Property=\"Padding\" Value=\"9,4\" />", true)]
    [InlineData("<Setter Property=\"Margin\" Value=\"0,0,0,16\" />", false)]
    [InlineData("Margin=\"{StaticResource Layout.PagePadding}\"", false)]
    [InlineData("Margin=\"Auto\"", false)]
    public void TheGridRuleRecognisesOffGridValues(string markup, bool offGrid) =>
        Assert.Equal(offGrid, OffGridValues(markup, "Test.xaml").Any());

    [Fact]
    public void TheLayoutTokensAreOnTheGrid()
    {
        var theme = File.ReadAllText(Path.Combine(RepositoryRoot(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        foreach (Match match in Regex.Matches(theme, @"x:Key=""(?<key>Layout\.[A-Za-z]+)"">(?<value>[^<]+)<"))
        {
            foreach (var part in match.Groups["value"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Assert.True(double.Parse(part) % 4 == 0, $"{match.Groups["key"].Value} = {part} is off the 4 px grid");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException(AppContext.BaseDirectory);
    }
}
