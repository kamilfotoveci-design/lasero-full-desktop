using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lasero.Tests;

/// <summary>
/// Source-level guards for the "buttons turn black" class of defects. Every one of them was a
/// template or style whose hover, press, checked or disabled state was missing, inherited the wrong
/// colours, or fell through to stock Windows chrome. Like ThemeTokenTests these read the XAML back,
/// so no window is needed.
/// </summary>
public sealed class ButtonStateTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ButtonLike = { "Button", "ToggleButton", "RadioButton", "RepeatButton" };

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Lasero.App");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Theme(string name) => Path.Combine(AppRoot(), "Theme", name);

    private static string TypeName(string? targetType) =>
        Regex.Replace(targetType ?? "", @"^\{x:Type\s+|\}$", "").Trim();

    private static IEnumerable<(string File, XElement Template)> ButtonLikeTemplates() =>
        XamlFiles().SelectMany(f => XDocument.Load(f).Descendants(P + "ControlTemplate")
            .Where(t => ButtonLike.Contains(TypeName((string?)t.Attribute("TargetType")))
                        && !t.Ancestors(P + "ControlTemplate").Any())
            .Select(t => (Path.GetFileName(f), t)));

    private static bool HasTrigger(XElement template, string property) =>
        template.Descendants().Any(e =>
            (e.Name == P + "Trigger" || e.Name == P + "MultiTrigger" || e.Name == P + "Condition")
            && (string?)e.Attribute("Property") == property);

    private static string Describe(string file, XElement t)
    {
        var owner = t.Ancestors(P + "Style").FirstOrDefault();
        var key = (string?)owner?.Attribute(X + "Key") ?? (string?)owner?.Attribute("TargetType") ?? "?";
        return $"{file} -> {key} ({(string?)t.Attribute("TargetType")})";
    }

    [Theory]
    [InlineData("ToggleButton")]
    [InlineData("RadioButton")]
    [InlineData("RepeatButton")]
    [InlineData("Expander")]
    [InlineData("Button")]
    public void EveryPressableControlTypeHasAnImplicitStyle(string type)
    {
        // An unstyled ToggleButton, RadioButton, RepeatButton or Expander renders the stock Windows
        // chrome: blue hover, grey bevel, circled arrow.
        var doc = XDocument.Load(Theme("LaseroTheme.xaml"));
        Assert.Contains(doc.Root!.Elements(P + "Style"),
            s => (string?)s.Attribute("TargetType") == type && s.Attribute(X + "Key") is null);
    }

    [Fact]
    public void NoTemplateOrStyleHardCodesBlackOrSystemColours()
    {
        var comment = new Regex(@"<!--.*?-->", RegexOptions.Singleline);
        var literal = new Regex(
            @"=""(Black|#000000|#FF000000|#FF000|#000)""|SystemColors\.|SystemParameters\.\w*Brush",
            RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            var text = comment.Replace(File.ReadAllText(file), "");
            foreach (Match m in literal.Matches(text))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryButtonLikeTemplateHasAPressedState()
    {
        var missing = ButtonLikeTemplates()
            .Where(x => HasTrigger(x.Template, "IsMouseOver") && !HasTrigger(x.Template, "IsPressed"))
            .Select(x => Describe(x.File, x.Template))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryButtonLikeTemplateHasADisabledState()
    {
        var missing = ButtonLikeTemplates()
            .Where(x => !HasTrigger(x.Template, "IsEnabled"))
            .Select(x => Describe(x.File, x.Template))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void PressedAndHoverBrushesAreDefinedTokensNeverLiterals()
    {
        // Any setter inside an IsMouseOver / IsPressed trigger of a button-like template must take its
        // colour from a Brush.* resource. A literal there is how a stray dark fill gets in.
        var literalColour = new Regex(@"^(#[0-9A-Fa-f]{3,8}|[A-Za-z]+)$");
        var offenders = new List<string>();
        foreach (var (file, t) in ButtonLikeTemplates())
            foreach (var trig in t.Descendants(P + "Trigger")
                         .Where(e => (string?)e.Attribute("Property") is "IsMouseOver" or "IsPressed" or "IsChecked"))
                foreach (var s in trig.Elements(P + "Setter"))
                {
                    var prop = (string?)s.Attribute("Property");
                    var value = (string?)s.Attribute("Value");
                    if (prop is "Background" or "BorderBrush" or "Foreground" or "Fill" or "Stroke"
                        && value is not null && literalColour.IsMatch(value)
                        && value is not "Transparent")
                        offenders.Add($"{Describe(file, t)}: {prop}={value}");
                }
        Assert.Empty(offenders);
    }

    [Fact]
    public void PrimaryHoverAndPressBackgroundsOnlyApplyWhileEnabled()
    {
        // A Style trigger outranks the template's disabled trigger, and Button.Large and the styles
        // built on Button.Primary inherit it. Unguarded, a disabled primary button was painted with the
        // graphite hover colour, and a *Secondary* built on it went dark with dark text under the pointer.
        var doc = XDocument.Load(Theme("LaseroTheme.xaml"));
        var primary = doc.Root!.Elements(P + "Style").Single(s => (string?)s.Attribute(X + "Key") == "Button.Primary");
        var triggers = primary.Element(P + "Style.Triggers")!;
        Assert.Empty(triggers.Elements(P + "Trigger"));
        var multi = triggers.Elements(P + "MultiTrigger").ToList();
        Assert.Equal(2, multi.Count);
        foreach (var m in multi)
            Assert.Contains(m.Descendants(P + "Condition"),
                c => (string?)c.Attribute("Property") == "IsEnabled" && (string?)c.Attribute("Value") == "True");
    }

    [Fact]
    public void NoSecondaryStyleInheritsThePrimaryStateTriggers()
    {
        // Styles that repaint the Background of something derived from Button.Primary (LargeSecondary was
        // exactly this) fight its state triggers. A style that sets a light Background must not have
        // Button.Primary in its BasedOn chain.
        var styles = XamlFiles()
            .SelectMany(f => XDocument.Load(f).Descendants(P + "Style"))
            .Where(s => s.Attribute(X + "Key") is not null)
            .GroupBy(s => (string)s.Attribute(X + "Key")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        static string? BasedOn(XElement s) =>
            Regex.Match((string?)s.Attribute("BasedOn") ?? "", @"StaticResource\s+([^\s}]+)") is { Success: true } m
                ? m.Groups[1].Value : null;

        var offenders = new List<string>();
        foreach (var (key, style) in styles)
        {
            var bg = style.Elements(P + "Setter")
                .FirstOrDefault(s => (string?)s.Attribute("Property") == "Background");
            var value = (string?)bg?.Attribute("Value") ?? "";
            if (bg is null || value.Contains("PrimaryAction") || value.Contains("Danger")
                || value.Contains("Accent") || value.Contains("SelectedSurface"))
                continue;
            for (var cur = BasedOn(style); cur is not null; cur = styles.TryGetValue(cur, out var p) ? BasedOn(p) : null)
                if (cur == "Button.Primary")
                {
                    offenders.Add(key);
                    break;
                }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void LargeSecondaryIsNotDerivedFromPrimary()
    {
        // The exact defect from the Home command row: disabled or hovered "Importovat" rendered as a
        // dark grey block with dark text.
        var text = File.ReadAllText(Theme("SharedUiStyles.xaml"));
        var m = Regex.Match(text, @"<Style x:Key=""Button\.LargeSecondary""[^>]*BasedOn=""\{StaticResource ([^}]+)\}""");
        Assert.True(m.Success);
        Assert.Equal("Button.Secondary", m.Groups[1].Value);
    }
}
