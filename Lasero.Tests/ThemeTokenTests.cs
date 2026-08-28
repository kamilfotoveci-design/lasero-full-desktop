using System.IO;
using System.Text.RegularExpressions;

namespace Lasero.Tests;

/// <summary>
/// A missing StaticResource key is a runtime failure in WPF, not a compile error: the build stays
/// clean and the app throws on load. Renaming or deleting a token therefore has no safety net
/// unless something reads the XAML back, which is what these tests do.
/// </summary>
public sealed class ThemeTokenTests
{
    // {StaticResource {x:Type Button}} is deliberately not matched — the inner brace is excluded,
    // so implicit-style lookups are skipped rather than reported as undefined keys.
    private static readonly Regex ResourceReference =
        new(@"\{(?:Static|Dynamic)Resource\s+([^\s}{]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex ResourceKey =
        new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    // Comments discuss resource syntax in prose, so they have to come out before scanning or the
    // prose gets reported as a dangling key.
    private static readonly Regex XmlComment =
        new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void EveryResourceReferenceResolvesToADefinedKey()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var files = AppXamlFiles().ToList();

        foreach (var file in files)
            foreach (Match match in ResourceKey.Matches(File.ReadAllText(file)))
                defined.Add(match.Groups[1].Value);

        var dangling = new List<string>();
        foreach (var file in files)
        {
            var text = XmlComment.Replace(File.ReadAllText(file), string.Empty);
            foreach (Match match in ResourceReference.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (!defined.Contains(key))
                    dangling.Add($"{Path.GetFileName(file)} -> {key}");
            }
        }

        Assert.Empty(dangling);
    }

    [Theory]
    // Removed because each was a second name for an existing token at an identical value, which
    // left no way to tell which of the two a control was supposed to use.
    [InlineData("Brush.PrimaryAction")]
    [InlineData("Brush.PrimaryActionHover")]
    [InlineData("Brush.OnPrimaryAction")]
    [InlineData("Brush.BrandRed")]
    // Removed because it carried three unrelated roles at one value: informational panels,
    // selection surfaces and a dropdown hover wash.
    [InlineData("Brush.AccentMuted")]
    // Replaced by the four-step elevation ladder.
    [InlineData("Shadow.Panel")]
    public void RetiredTokensAreNotReferencedAnywhere(string key)
    {
        var reference = "Resource " + key + "}";

        foreach (var file in AppXamlFiles().Concat(AppSourceFiles()))
        {
            var text = XmlComment.Replace(File.ReadAllText(file), string.Empty);
            Assert.False(
                text.Contains(reference, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} still references the retired token {key}");
        }
    }

    [Theory]
    [InlineData("Size.Text.Title")]
    [InlineData("Size.Text.Section")]
    [InlineData("Size.Text.Body")]
    [InlineData("Size.Text.Meta")]
    [InlineData("Shadow.Sheet")]
    [InlineData("Shadow.Tooltip")]
    [InlineData("Shadow.Dropdown")]
    [InlineData("Shadow.Modal")]
    public void DesignSystemTokenIsDefined(string key)
    {
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        Assert.Contains($"x:Key=\"{key}\"", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover and press are one ink-wash mechanic at two strengths. If a per-kind pressed colour ever
    /// appears, the same state ends up with two implementations that have to agree by hand.
    /// </summary>
    [Fact]
    public void PressIsTheHoverWashAtAHigherStrength()
    {
        // The comment block above Brush.HoverWash names both retired keys to explain why they are
        // absent, so the markup has to be stripped of comments before asserting they are unused.
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath("LaseroTheme.xaml")), string.Empty);
        Assert.Contains("Storyboard.TargetName=\"Hover\" Storyboard.TargetProperty=\"Opacity\" To=\"0.06\"", theme, StringComparison.Ordinal);
        Assert.Contains("Storyboard.TargetName=\"Hover\" Storyboard.TargetProperty=\"Opacity\" To=\"0.14\"", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("Brush.AccentPressed", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("Brush.DangerPressed", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover must never look like selected. The list row template is where the two states sit
    /// closest together, so it is the one worth pinning.
    /// </summary>
    [Fact]
    public void DropdownHighlightUsesTheNeutralWashRatherThanTheSelectionTint()
    {
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        var start = theme.IndexOf("<ControlTemplate TargetType=\"ComboBoxItem\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "ComboBoxItem template not found");
        var template = theme[start..theme.IndexOf("</ControlTemplate>", start, StringComparison.Ordinal)];

        var highlighted = template.IndexOf("Property=\"IsHighlighted\"", StringComparison.Ordinal);
        var selected = template.IndexOf("Property=\"IsSelected\"", StringComparison.Ordinal);
        Assert.True(highlighted >= 0 && selected >= 0, "both IsHighlighted and IsSelected triggers are expected");

        Assert.Contains("Brush.Field", template[highlighted..selected], StringComparison.Ordinal);
        Assert.Contains("Brush.SelectedSurface", template[selected..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Type sizes come from Size.Text.*. A literal FontSize in markup is how the scale drifted in
    /// the first place: 22 instances of 12, 15 of 16, and 15 / 17 / 21 / 22 / 24 / 26 / 27 spread
    /// across headings, with no view agreeing with another.
    /// </summary>
    [Fact]
    public void NoMarkupCarriesALiteralFontSize()
    {
        var literal = new Regex(@"FontSize=""[0-9.]+""|Property=""FontSize""\s+Value=""[0-9.]+""", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in AppXamlFiles())
            foreach (Match match in literal.Matches(XmlComment.Replace(File.ReadAllText(file), string.Empty)))
                offenders.Add($"{Path.GetFileName(file)} -> {match.Value}");

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Icon sizes come from Size.Icon.*. Glyph.Close used to appear at 10, 12 and 13 in the same
    /// dismiss role, and the product rendered icons at fourteen distinct sizes in total.
    /// </summary>
    [Fact]
    public void NoIconElementCarriesANumericSize()
    {
        var element = new Regex(@"<components:Icon(?:Glyph|Label)\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.Singleline);
        var sizeAttribute = new Regex(@"\b(?:Width|Height|IconSize)=""[0-9]+(?:\.[0-9]+)?""", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in AppXamlFiles())
            foreach (Match match in element.Matches(XmlComment.Replace(File.ReadAllText(file), string.Empty)))
                foreach (Match size in sizeAttribute.Matches(match.Value))
                    offenders.Add($"{Path.GetFileName(file)} -> {size.Value}");

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The exported SVG assets in docs/design/icons-custom carry stroke-width 1.75. If the renderer
    /// disagrees, the shipped icons are a different weight from the design assets.
    /// </summary>
    [Fact]
    public void RendererStrokeMatchesTheExportedAssets()
    {
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        Assert.Contains("x:Key=\"Size.Icon.Stroke\">1.75<", theme, StringComparison.Ordinal);

        var glyph = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Components", "IconGlyph.xaml"));
        Assert.Contains("StrokeThickness=\"{StaticResource Size.Icon.Stroke}\"", glyph, StringComparison.Ordinal);

        var assets = Path.Combine(FindRepositoryRoot(), "docs", "design", "icons-custom");
        if (!Directory.Exists(assets))
            return;

        foreach (var svg in Directory.EnumerateFiles(assets, "*.svg"))
            Assert.Contains("stroke-width=\"1.75\"", File.ReadAllText(svg), StringComparison.Ordinal);
    }

    /// <summary>
    /// Radii come from Radius.*. Two exceptions are legitimate and deliberate: 0 is the absence of a
    /// radius rather than a value, and a composite radius (a footer rounded on two corners, a chat
    /// bubble with one square corner) cannot reference a scalar token because XAML has no arithmetic.
    /// Everything else — a half-the-box value that stops being a circle when the box is resized —
    /// belongs to a token.
    /// </summary>
    [Fact]
    public void NoMarkupCarriesAScalarLiteralCornerRadius()
    {
        var literal = new Regex(@"CornerRadius=""([0-9][0-9.]*)""|Property=""CornerRadius""\s+Value=""([0-9][0-9.]*)""", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in AppXamlFiles())
        {
            var text = XmlComment.Replace(File.ReadAllText(file), string.Empty);
            foreach (Match match in literal.Matches(text))
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (value == "0")
                    continue;
                offenders.Add($"{Path.GetFileName(file)} -> {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A circle expressed as Radius.Md deforms the moment Md changes, and one expressed as half the
    /// box deforms the moment the box is resized. Radius.Pill clamps, so it survives both.
    /// </summary>
    [Fact]
    public void RoundControlsUseThePillTokenRatherThanHalfTheirBox()
    {
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath("LaseroTheme.xaml")), string.Empty);

        Assert.Contains("<CornerRadius x:Key=\"Radius.Pill\">999</CornerRadius>", theme, StringComparison.Ordinal);
        foreach (var part in new[] { "Track", "Thumb" })
        {
            var index = theme.IndexOf($"x:Name=\"{part}\"", StringComparison.Ordinal);
            Assert.True(index >= 0, $"toggle {part} not found");
            var element = theme[index..theme.IndexOf('>', index)];
            Assert.Contains("{StaticResource Radius.Pill}", element, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("PageTitle", "Size.Text.Title")]
    [InlineData("PanelTitle", "Size.Text.Section")]
    // A panel header sits at the body step, not above it — that is what keeps an inspector dense.
    [InlineData("InspectorSectionTitle", "Size.Text.Body")]
    // A property row label is the one place the meta step is correct for a label.
    [InlineData("TransformRowLabel", "Size.Text.Meta")]
    public void SharedTextStyleUsesTheExpectedStep(string styleKey, string token)
    {
        var shared = File.ReadAllText(ThemePath("SharedUiStyles.xaml"));
        var start = shared.IndexOf($"x:Key=\"{styleKey}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"style {styleKey} not found");
        var block = shared[start..shared.IndexOf("</Style>", start, StringComparison.Ordinal)];

        Assert.Contains($"Value=\"{{StaticResource {token}}}\"", block, StringComparison.Ordinal);
    }

    private static string ThemePath(string fileName) =>
        Path.Combine(FindRepositoryRoot(), "Lasero.App", "Theme", fileName);

    private static IEnumerable<string> AppXamlFiles() => EnumerateApp("*.xaml");

    private static IEnumerable<string> AppSourceFiles() => EnumerateApp("*.cs");

    private static IEnumerable<string> EnumerateApp(string pattern) =>
        Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "Lasero.App"), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
