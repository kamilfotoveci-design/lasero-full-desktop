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
    // Brush.PrimaryAction/PrimaryActionHover/OnPrimaryAction were removed in 2026-08 as a second
    // name for Brush.Accent at an identical value, then reintroduced in the 2026-09 graphite/red
    // palette migration once Accent (red, interaction/selection) and PrimaryAction (graphite,
    // ordinary buttons) became genuinely different colours — the original "no way to tell which of
    // the two a control was supposed to use" reasoning no longer applies once the two names carry
    // two different values.
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
    /// Static desktop text must be formatted on the device-pixel grid. Ideal mode is useful for
    /// document typography, but at LASERO's 12–16px control sizes it produces softer fractional
    /// glyph placement. The Window root is the one inheritance boundary that should own this.
    /// </summary>
    [Fact]
    public void WindowRenderingUsesDisplayClearTypeAndFixedHinting()
    {
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        var startup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "App.xaml.cs"));
        var windowStyle = theme[theme.IndexOf("<Style TargetType=\"Window\">", StringComparison.Ordinal)..];
        windowStyle = windowStyle[..windowStyle.IndexOf("</Style>", StringComparison.Ordinal)];

        Assert.Contains("Property=\"UseLayoutRounding\" Value=\"True\"", windowStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"SnapsToDevicePixels\" Value=\"True\"", windowStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextOptions.TextFormattingMode\" Value=\"Display\"", windowStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextOptions.TextRenderingMode\" Value=\"ClearType\"", windowStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextOptions.TextHintingMode\" Value=\"Fixed\"", windowStyle, StringComparison.Ordinal);

        // Lasero windows derive from Window, so an implicit Window style is not a reliable root
        // setter. Metadata on Window is inherited by MainWindow and every dialog subclass.
        Assert.Contains("TextOptions.TextFormattingModeProperty.OverrideMetadata", startup, StringComparison.Ordinal);
        Assert.Contains("TextFormattingMode.Display", startup, StringComparison.Ordinal);
        Assert.Contains("TextOptions.TextRenderingModeProperty.OverrideMetadata", startup, StringComparison.Ordinal);
        Assert.Contains("TextRenderingMode.ClearType", startup, StringComparison.Ordinal);
        Assert.Contains("TextOptions.TextHintingModeProperty.OverrideMetadata", startup, StringComparison.Ordinal);
        Assert.Contains("TextHintingMode.Fixed", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantAvatarsUseOneCanonicalArtworkAndReusableControl()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "Lasero.App", "Lasero.App.csproj"));
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        var markup = string.Join('\n', AppXamlFiles().Select(File.ReadAllText));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var kamilHost = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "Kamil", "KamilAssistantHost.xaml"));

        Assert.Contains("<Resource Include=\"Assets\\LaseroAvatar.png\"", project, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Image.LaseroAvatarSource\"", theme, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth=\"256\"", theme, StringComparison.Ordinal);
        Assert.True(Regex.Matches(kamilHost, "<components:LaseroAvatar\\b").Count >= 3);
        // ChatView (the full-screen "Lasero Chat" nav destination) is a legitimate, separate
        // consumer of the shared LaseroAvatar control — reusing it there is the point of having a
        // reusable control, not a violation of it. Only MainWindow (which should reach the avatar
        // exclusively through KamilAssistantHost/ChatView, never inline) is asserted against below.
        Assert.DoesNotContain("<components:LaseroAvatar", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotMatch("<components:LaseroAvatar[^>]*Size=\"[0-9]+\\.[0-9]+\"", markup);
        Assert.DoesNotContain("Image.KamilAvatar", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Image.KamilSidebarAvatar", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("KamilAvatarSidebar.svg", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationSidebarIsPermanentlyExpanded()
    {
        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var windowCode = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "Lasero.App", "ViewModels", "MainViewModel.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "Lasero.App", "AppSettingsStore.cs"));
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));

        Assert.Contains("x:Name=\"NavColumn\" Width=\"164\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("IsNavCollapsed", window + windowCode + viewModel + settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleNavCommand", window + viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Button.RailHandle", window + theme, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactMode=", window, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimizedAssistantIsOnlyTheBreathingArtwork()
    {
        var host = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "Views", "Kamil", "KamilAssistantHost.xaml"));
        var start = host.IndexOf("x:Key=\"Kamil.MinimizedAvatar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Kamil.MinimizedAvatar style not found");
        var style = host[start..host.IndexOf("</Style>", start, StringComparison.Ordinal)];

        Assert.Contains("x:Name=\"BreatheScale\"", style, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding Chat.IsBusy}\" Value=\"True\"", style, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior=\"Forever\"", style, StringComparison.Ordinal);
        Assert.Contains("EasingFunction=\"{StaticResource Ease.Breathe}\"", style, StringComparison.Ordinal);
        Assert.Contains("To=\"1.015\"", style, StringComparison.Ordinal);
        Assert.DoesNotContain("To=\"1.09\"", style, StringComparison.Ordinal);
        Assert.DoesNotContain("HoverRing", style, StringComparison.Ordinal);
        Assert.DoesNotContain("Shadow.Sheet", style, StringComparison.Ordinal);
        Assert.DoesNotContain("<Ellipse", style, StringComparison.Ordinal);

        var minimizedState = host.IndexOf("Value=\"Minimized\"", style.Length + start, StringComparison.Ordinal);
        Assert.True(minimizedState >= 0, "Minimized surface state not found");
        var minimizedBlock = host[minimizedState..host.IndexOf("</DataTrigger>", minimizedState, StringComparison.Ordinal)];
        Assert.Contains("Property=\"Effect\" Value=\"{x:Null}\"", minimizedBlock, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0\"", minimizedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void MotionTokensUseRestrainedDesktopSpringAndRespectReducedMotion()
    {
        var root = FindRepositoryRoot();
        var theme = File.ReadAllText(ThemePath("LaseroTheme.xaml"));
        var accessibility = File.ReadAllText(Path.Combine(root, "Lasero.App", "UiAccessibility.cs"));

        Assert.Contains("x:Key=\"Motion.Spatial\">0:0:0.26", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Motion.Panel\">0:0:0.34", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Motion.Breathe\">0:0:1.6", theme, StringComparison.Ordinal);
        Assert.Contains("To=\"0.975\"", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("To=\"0.96\" Duration=\"0:0:0.05\"", theme, StringComparison.Ordinal);

        foreach (var key in new[] { "Motion.VeryFast", "Motion.Fast", "Motion.Base", "Motion.Spatial", "Motion.Panel" })
            Assert.Contains($"Resources[\"{key}\"] = new Duration(TimeSpan.Zero)", accessibility, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover and press are one ink-wash mechanic at two strengths. If a per-kind pressed colour ever
    /// appears, the same state ends up with two implementations that have to agree by hand.
    ///
    /// The two strengths are now two layers of the same brush rather than two opacities of one layer.
    /// Sharing a layer meant press had to restore hover's value on release, and it never did: a click
    /// that opened a window left the button washed dark for the rest of the session, because the
    /// pointer leaves without WPF raising the hover exit. Independent layers cannot strand each other.
    /// </summary>
    [Fact]
    public void PressIsTheHoverWashAtAHigherStrength()
    {
        // The comment block above Brush.HoverWash names both retired keys to explain why they are
        // absent, so the markup has to be stripped of comments before asserting they are unused.
        var theme = XmlComment.Replace(File.ReadAllText(ThemePath("LaseroTheme.xaml")), string.Empty).Replace("\r\n", "\n");

        // One brush for both states.
        Assert.Contains("<Border x:Name=\"Hover\"\n                                Background=\"{StaticResource Brush.HoverWash}\"", theme, StringComparison.Ordinal);
        Assert.Contains("<Border x:Name=\"Press\"\n                                Background=\"{StaticResource Brush.HoverWash}\"", theme, StringComparison.Ordinal);

        Assert.Contains("Storyboard.TargetName=\"Hover\" Storyboard.TargetProperty=\"Opacity\" To=\"0.05\"", theme, StringComparison.Ordinal);
        Assert.Contains("Storyboard.TargetName=\"Press\" Storyboard.TargetProperty=\"Opacity\" To=\"0.08\"", theme, StringComparison.Ordinal);

        // Every wash that goes up has to come back down, or the state is strandable again.
        Assert.Contains("Storyboard.TargetName=\"Hover\" Storyboard.TargetProperty=\"Opacity\" To=\"0\"", theme, StringComparison.Ordinal);
        Assert.Contains("Storyboard.TargetName=\"Press\" Storyboard.TargetProperty=\"Opacity\" To=\"0\"", theme, StringComparison.Ordinal);

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

        Assert.Contains("Brush.Hover", template[highlighted..selected], StringComparison.Ordinal);
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
        Assert.Contains("Property=\"StrokeThickness\" Value=\"{StaticResource Size.Icon.Stroke}\"", glyph, StringComparison.Ordinal);

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

        // The value matters, not just the key: a Pill quietly redefined as 12 turns every circle in the
        // app into a rounded rectangle, and nothing else would catch it. That the token is the only way
        // to express a radius at all is enforced by NoMarkupCarriesAScalarLiteralCornerRadius above.
        //
        // This used to pin the toggle switch's Track and Thumb by name. The switch is gone - replaced
        // by a square checkbox - so that version was asserting against one control rather than the rule.
        var square = new Regex(
            @"<Border[^>]*?Width=""(?<w>\d+)""\s+Height=""(?<h>\d+)""[^>]*?CornerRadius=""\{StaticResource Radius\.Pill\}""",
            RegexOptions.Singleline);

        var circles = AppXamlFiles()
            .SelectMany(file => square.Matches(XmlComment.Replace(File.ReadAllText(file), string.Empty)))
            .Count(match => match.Groups["w"].Value == match.Groups["h"].Value);

        Assert.True(circles > 0, "No square Border uses Radius.Pill, so nothing is holding the token honest.");
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
