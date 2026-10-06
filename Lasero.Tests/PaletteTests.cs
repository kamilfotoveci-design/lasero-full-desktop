using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Lasero.Tests;

/// <summary>
/// Guards for the neutral-first palette (DESIGN.md, "Color"). The palette is a set of rules rather
/// than a set of values, so these tests assert the rules: surfaces and washes are neutral, text
/// passes WCAG against the surfaces it is drawn on, and the saturated red is a signal that only an
/// allow-list of tokens and files may use.
///
/// Like ThemeTokenTests they read the XAML and source back, so no window is needed.
/// </summary>
public sealed class PaletteTests
{
    // Tokens that may carry chroma. Everything else in the theme must be a neutral (R, G and B within
    // a few steps of each other). Green and amber exist as semantic text/icon/dot colours; the reds
    // are the signal family plus the error/destructive colour.
    private static readonly string[] ChromaticTokens =
    {
        "Brush.Signal", "Brush.SignalHover", "Brush.SignalPressed", "Brush.Brand",
        "Brush.Danger", "Brush.Success", "Brush.Warning",
    };

    // The tokens whose hue is red. A new red token must be added here deliberately.
    private static readonly string[] RedTokens =
    {
        "Brush.Signal", "Brush.SignalHover", "Brush.SignalPressed", "Brush.Brand", "Brush.Danger",
    };

    // The only files outside the theme that may reference the signal red tokens. Each is a place the
    // brief names: the live-job indicator, status dots, canvas selection and beam markers.
    private static readonly string[] SignalConsumers =
    {
        "StatusBadge.xaml",               // 6px state dot for a busy/live machine
        "SceneCanvas.xaml.cs",            // selection outline and handles
        "MachineDisplayStateConverters.cs", // live-job state colour
        "MachineModeConverters.cs",       // live-job state dot
        "TipChip.xaml",                   // the tiny brand dot on the tip chip
        "TourOverlay.xaml",               // the red dot closing the welcome headline
        "TourOverlay.xaml.cs",            // same dot, built in code
        "MotionPalette.cs",               // the brand dot in the intro animation (token-resolved)
        "HomeView.xaml",                  // the 6px brand dot beside a section heading
    };

    // Source files whose hex literals are content, not chrome.
    private static readonly string[] ContentColourFiles =
    {
        "MaterialSwatchViewModels.cs", // the colour of real materials (birch, slate, acrylic ...)
        "SceneViewModel.cs",           // a comment quoting the KAMIL avatar sample colour
    };

    // The one saturated literal allowed outside the theme: the signal red, used by the G-code
    // preview's beam marker, which is a drawing rather than a resource-bound control.
    private static readonly string[] AllowedLiterals = { "E5302B" };

    private static readonly Regex Hex =
        new(@"#(?<h>[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\b", RegexOptions.Compiled);

    private static readonly Regex FromRgb =
        new(@"Color\.FromRgb\(\s*0x(?<r>[0-9A-Fa-f]{2})\s*,\s*0x(?<g>[0-9A-Fa-f]{2})\s*,\s*0x(?<b>[0-9A-Fa-f]{2})\s*\)", RegexOptions.Compiled);

    private static readonly Regex FromArgb =
        new(@"Color\.FromArgb\(\s*0x(?<a>[0-9A-Fa-f]{2})\s*,\s*0x(?<r>[0-9A-Fa-f]{2})\s*,\s*0x(?<g>[0-9A-Fa-f]{2})\s*,\s*0x(?<b>[0-9A-Fa-f]{2})\s*\)", RegexOptions.Compiled);

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly record struct Rgba(int A, int R, int G, int B)
    {
        public int Spread => Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));
        public double Alpha => A / 255.0;
    }

    // ------------------------------------------------------------------ token parsing

    private static Dictionary<string, Rgba> ThemeBrushes()
    {
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath()), string.Empty);
        var result = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(theme, @"<SolidColorBrush\s+x:Key=""(?<k>[^""]+)""\s+Color=""#(?<h>[0-9A-Fa-f]{6,8})"""))
            result[m.Groups["k"].Value] = ParseHex(m.Groups["h"].Value);
        return result;
    }

    private static Rgba ParseHex(string hex)
    {
        int Byte(int i) => int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 8
            ? new Rgba(Byte(0), Byte(2), Byte(4), Byte(6))
            : new Rgba(255, Byte(0), Byte(2), Byte(4));
    }

    private static Rgba Token(string key)
    {
        var all = ThemeBrushes();
        Assert.True(all.TryGetValue(key, out var colour), $"token {key} is not defined as a SolidColorBrush");
        return colour;
    }

    // ------------------------------------------------------------------ WCAG

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Rgba c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Contrast(Rgba a, Rgba b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo) (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void ContrastHelperMatchesTheWcagReferenceValues()
    {
        Assert.Equal(21.0, Contrast(ParseHex("000000"), ParseHex("FFFFFF")), 2);
        Assert.Equal(1.0, Contrast(ParseHex("777777"), ParseHex("777777")), 5);
        // The well-known "#767676 is the lightest gray that passes AA on white" reference point.
        Assert.InRange(Contrast(ParseHex("767676"), ParseHex("FFFFFF")), 4.5, 4.6);
    }

    [Theory]
    // Body text must reach AA (4.5:1) on every surface it is drawn on; primary text far exceeds it.
    [InlineData("Brush.TextPrimary", "Brush.Surface", 7.0)]
    [InlineData("Brush.TextPrimary", "Brush.Canvas", 7.0)]
    [InlineData("Brush.TextPrimary", "Brush.Field", 7.0)]
    [InlineData("Brush.TextSecondary", "Brush.Surface", 4.5)]
    [InlineData("Brush.TextSecondary", "Brush.Canvas", 4.5)]
    [InlineData("Brush.TextSecondary", "Brush.Field", 4.5)]
    // Muted is metadata, units and placeholders: AA on white. On the gray canvas it sits a little
    // lower (4.3) because #747478 is the lightest value that still clears 4.5 on white — Apple's own
    // #8E8E93 is only 3.3:1 there and fails.
    [InlineData("Brush.TextMuted", "Brush.Surface", 4.5)]
    [InlineData("Brush.TextMuted", "Brush.Canvas", 4.0)]
    [InlineData("Brush.TextMuted", "Brush.Field", 4.0)]
    // Disabled text is exempt from 4.5 by WCAG, but must stay legible: 3:1.
    [InlineData("Brush.TextDisabled", "Brush.Surface", 3.0)]
    // Semantic text on white.
    [InlineData("Brush.Success", "Brush.Surface", 4.5)]
    [InlineData("Brush.Warning", "Brush.Surface", 4.5)]
    [InlineData("Brush.Danger", "Brush.Surface", 4.5)]
    // Text on filled controls.
    [InlineData("Brush.OnPrimaryAction", "Brush.PrimaryAction", 7.0)]
    [InlineData("Brush.OnPrimaryAction", "Brush.PrimaryActionHover", 7.0)]
    [InlineData("Brush.OnActiveTool", "Brush.ActiveTool", 7.0)]
    [InlineData("Brush.OnAccent", "Brush.Danger", 4.5)]
    // Non-text indicators need 3:1 (WCAG 1.4.11): focus ring, selection handles, the signal dot.
    [InlineData("Brush.FocusRing", "Brush.Surface", 3.0)]
    [InlineData("Brush.FocusRing", "Brush.Canvas", 3.0)]
    [InlineData("Brush.Signal", "Brush.Surface", 3.0)]
    [InlineData("Brush.Signal", "Brush.Canvas", 3.0)]
    [InlineData("Brush.PanelBorderStrong", "Brush.Surface", 1.3)]
    public void TextAndIndicatorPairsMeetWcagContrast(string foreground, string background, double minimum)
    {
        var ratio = Contrast(Token(foreground), Token(background));
        Assert.True(ratio >= minimum, $"{foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    // ------------------------------------------------------------------ neutral-first rules

    [Fact]
    public void SurfacesAreWhiteAndTheCanvasIsOneCoolGray()
    {
        foreach (var key in new[] { "Brush.Surface", "Brush.Panel", "Brush.TitleBar", "Brush.Sidebar", "Brush.Canvas.WorkArea" })
            Assert.Equal(ParseHex("FFFFFF"), Token(key));

        foreach (var key in new[] { "Brush.Canvas", "Brush.Background", "Brush.Canvas.Surround" })
            Assert.Equal(ParseHex("F5F5F7"), Token(key));

        // Cool, not warm: blue is the highest channel on every gray the chrome uses.
        foreach (var (key, c) in ThemeBrushes().Where(p => p.Value.A == 255 && p.Value.Spread > 0 && !ChromaticTokens.Contains(p.Key)))
            Assert.True(c.B >= c.R, $"{key} is a warm gray (#{c.R:X2}{c.G:X2}{c.B:X2}); the palette is cool neutral");
    }

    [Fact]
    public void EveryThemeBrushIsNeutralExceptTheAllowListedSemanticColours()
    {
        var offenders = ThemeBrushes()
            .Where(p => !ChromaticTokens.Contains(p.Key) && p.Value.Spread > 6)
            .Select(p => $"{p.Key} = #{p.Value.A:X2}{p.Value.R:X2}{p.Value.G:X2}{p.Value.B:X2} (spread {p.Value.Spread})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Tinted brushes are not allowed (no pink/beige/blue washes). Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ShadowsAreNeutralAndLowOpacity()
    {
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath()), string.Empty);
        var shadows = Regex.Matches(theme, @"<DropShadowEffect\s+x:Key=""(?<k>[^""]+)""\s+Color=""#(?<h>[0-9A-Fa-f]{6})""\s+Opacity=""(?<o>[0-9.]+)""");
        Assert.Equal(4, shadows.Count);
        foreach (Match m in shadows)
        {
            Assert.True(ParseHex(m.Groups["h"].Value).Spread <= 6, $"{m.Groups["k"].Value} is a coloured glow");
            Assert.True(double.Parse(m.Groups["o"].Value, CultureInfo.InvariantCulture) <= 0.16,
                $"{m.Groups["k"].Value} is heavier than a soft neutral shadow");
        }
    }

    [Fact]
    public void HoverAndPressedAreNeutralInkWashesAtTheSpecifiedStrength()
    {
        var hover = Token("Brush.Hover");
        var pressed = Token("Brush.Pressed");
        var selected = Token("Brush.Selected");

        Assert.InRange(hover.Alpha, 0.045, 0.055);
        Assert.InRange(pressed.Alpha, 0.075, 0.085);
        Assert.InRange(selected.Alpha, 0.055, 0.065);
        foreach (var wash in new[] { hover, pressed, selected, Token("Brush.HoverWash") })
            Assert.True(wash.Spread <= 6, "a wash must be neutral ink, never a tint");

        Assert.Equal(Token("Brush.Selected"), Token("Brush.SelectedSurface"));
        // Hover and selected must never be mistaken for each other.
        Assert.NotEqual(hover.Alpha, selected.Alpha);
    }

    [Fact]
    public void AccentNamesResolveToGraphiteNotRed()
    {
        var graphite = ParseHex("1D1D1F");
        foreach (var key in new[] { "Brush.Accent", "Brush.AccentText", "Brush.FocusRing", "Brush.PrimaryAction", "Brush.ActiveTool", "Brush.TextPrimary" })
            Assert.Equal(graphite, Token(key));
    }

    [Fact]
    public void TheRedFamilyIsExactlyTheAllowListedTokens()
    {
        var reds = ThemeBrushes()
            .Where(p => p.Value.R > 150 && p.Value.G < 70 && p.Value.B < 70)
            .Select(p => p.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RedTokens.OrderBy(k => k, StringComparer.Ordinal).ToArray(), reds);
        Assert.Equal(ParseHex("E5302B"), Token("Brush.Signal"));
        Assert.Equal(ParseHex("CF2A26"), Token("Brush.SignalHover"));
        Assert.Equal(ParseHex("B9231F"), Token("Brush.SignalPressed"));
    }

    [Fact]
    public void SignalRedIsOnlyReferencedFromAllowListedFiles()
    {
        var reference = new Regex(@"Brush\.(Signal|SignalHover|SignalPressed|Brand)\b");
        var offenders = new List<string>();

        foreach (var file in AppFiles("*.xaml").Concat(AppFiles("*.cs")))
        {
            var name = Path.GetFileName(file);
            if (name == "LaseroTheme.xaml" || SignalConsumers.Contains(name)) continue;
            var text = File.ReadAllText(file);
            if (name.EndsWith(".xaml", StringComparison.Ordinal)) text = XmlComment.Replace(text, string.Empty);
            if (reference.IsMatch(text)) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "Red is a signal. These files use it without being on the allow-list in PaletteTests: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoChromaticColourLiteralsOutsideTheThemeExceptContentAndTheSignalRed()
    {
        var offenders = new List<string>();

        foreach (var file in AppFiles("*.xaml").Concat(AppFiles("*.cs")))
        {
            var name = Path.GetFileName(file);
            if (name == "LaseroTheme.xaml" || ContentColourFiles.Contains(name)) continue;
            var text = File.ReadAllText(file);
            if (name.EndsWith(".xaml", StringComparison.Ordinal)) text = XmlComment.Replace(text, string.Empty);

            foreach (Match m in Hex.Matches(text))
                Check(name, m.Value, ParseHex(m.Groups["h"].Value), m.Groups["h"].Value[^6..]);
            foreach (Match m in FromRgb.Matches(text))
                Check(name, m.Value, ParseHex(m.Groups["r"].Value + m.Groups["g"].Value + m.Groups["b"].Value),
                    m.Groups["r"].Value + m.Groups["g"].Value + m.Groups["b"].Value);
            foreach (Match m in FromArgb.Matches(text))
                Check(name, m.Value, ParseHex(m.Groups["r"].Value + m.Groups["g"].Value + m.Groups["b"].Value),
                    m.Groups["r"].Value + m.Groups["g"].Value + m.Groups["b"].Value);
        }

        Assert.True(offenders.Count == 0,
            "Chromatic colour literals outside the theme (pastel/tint/hard-coded semantic colour):\n" + string.Join("\n", offenders));

        void Check(string file, string literal, Rgba colour, string rgbHex)
        {
            if (colour.Spread <= 6) return;
            if (AllowedLiterals.Contains(rgbHex.ToUpperInvariant())) return;
            offenders.Add($"{file}: {literal}");
        }
    }

    [Fact]
    public void ThemeFileOnlyContainsNeutralOrAllowListedHexValues()
    {
        var allowed = ChromaticTokens.Select(k => ThemeBrushes()[k]).Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}").ToHashSet();
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath()), string.Empty);
        var offenders = new List<string>();

        foreach (Match m in Hex.Matches(theme))
        {
            var c = ParseHex(m.Groups["h"].Value);
            var rgb = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            if (c.Spread > 6 && !allowed.Contains(rgb)) offenders.Add(m.Value);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void BannerAndChipBackgroundsAreNeutralAliases()
    {
        // The *Muted names survive so no view needed editing; they must all be the one neutral gray.
        foreach (var key in new[] { "Brush.SuccessMuted", "Brush.WarningMuted", "Brush.DangerMuted", "Brush.InfoMuted" })
            Assert.Equal(Token("Brush.Canvas"), Token(key));
    }

    [Fact]
    public void ButtonTemplateRingsAreGraphiteWithAWhiteGap()
    {
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath()), string.Empty);
        Assert.Contains("x:Name=\"FocusGap\"", theme, StringComparison.Ordinal);
        Assert.Contains("Brush.FocusGap", theme, StringComparison.Ordinal);
        Assert.Equal(ParseHex("FFFFFF"), Token("Brush.FocusGap"));
    }

    // ------------------------------------------------------------------ plumbing

    private static string ThemePath() => Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml");

    private static IEnumerable<string> AppFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "Lasero.App"), pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
