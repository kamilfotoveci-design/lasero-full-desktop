using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Brand text rule: Czech UI copy is in the neutral form. No imperative addressed to the reader (vykání: "Zapněte",
/// "Zkontrolujte", "Zkuste", "Uložte", "Vyberte") and no second person plural indicative ("můžete", "najdete",
/// "zobrazíte"), no "prosím", no "váš". The neutral replacements are infinitives and impersonal phrases:
/// "Laser je potřeba zapnout", "Zkontrolovat lze ...", "Vybrat lze ...", "Lze ...". Button labels such as "Připojit"
/// and "Uložit" are infinitives, so they never match: only the finite second person forms are flagged.
///
/// The scan reads every string literal in the app and core sources (XAML attribute values and C# literals, comments
/// removed) that looks Czech (has a diacritic or a Czech function word), and flags words by suffix and by an explicit
/// list. A word that is genuinely not a verb form is added to <see cref="Allowed"/> with the reason.
/// </summary>
public sealed class NeutralFormCopyTests
{
    private static readonly Regex Literal = new("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"", RegexOptions.Compiled);
    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Czech = new("[áčďéěíňóřšťúůýž]|\\b(se|je|lze|nebo|pro|na|do|po|před|když|jen|už|ještě|bylo|není|jsou)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Word = new("[\\p{L}]{3,}", RegexOptions.Compiled);

    // Imperative plural and second person plural suffixes. "jste", "abyste" and "byste" are handled by the explicit list.
    private static readonly Regex Suffix = new("(ujte|ejte|ějte|ňte|ďte|ťte|ěte|žte|šte|řte|ete|íte|áte|ýte|ste|rte)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Explicit = new(StringComparer.OrdinalIgnoreCase)
    {
        "prosím", "váš", "vaše", "vaší", "vašeho", "vašem", "vaším", "vašich", "vám", "vás", "abyste", "byste", "jste",
    };

    /// <summary>Words that end like a verb form but are not one (names, English identifiers, nouns), with the reason.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Delete", "template", "ControlTemplate", "DataTemplate", "ItemTemplate", "Template", // resource and binding names
        "White", "Paste", "ContentSite", "LayerPalette", "SimulatorNote", "IsConfirmingDelete", // identifiers inside bindings
        "complete", "separate", "state", "create", "translate", // English words in prompts and identifiers
        "forte", "porte", "roste", // none today, here so a harmless noun does not need a code change
    };

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private static IEnumerable<string> Sources() =>
        new[] { "Lasero.App", "Lasero.Core", "Lasero.Persistence" }
            .Select(project => Path.Combine(Root(), project))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(path => (path.EndsWith(".xaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.EndsWith(".g.cs", StringComparison.Ordinal) && !path.EndsWith(".g.i.cs", StringComparison.Ordinal)
                           && !path.EndsWith("Icons.xaml", StringComparison.Ordinal));

    public static IEnumerable<string> Offenders(string text, bool xaml)
    {
        if (xaml) text = XamlComment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;
            foreach (Match literal in Literal.Matches(line))
            {
                var value = literal.Groups[1].Value;
                if (value.Contains("netykej", StringComparison.Ordinal)) continue; // the rule written out for the language model, not text shown to a person
                if (!Czech.IsMatch(value) && !value.Contains(' ')) continue; // a lone identifier such as "Delete" is not copy
                foreach (Match match in Word.Matches(value))
                {
                    var word = match.Value;
                    if (Allowed.Contains(word)) continue;
                    if (Explicit.Contains(word) || Suffix.IsMatch(word)) yield return $"{word} in \"{Shorten(value)}\"";
                }
            }
        }
    }

    private static string Shorten(string value) => value.Length <= 90 ? value : value[..90] + "...";

    [Fact]
    public void NoCzechUiStringAddressesTheReaderDirectly()
    {
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            var name = Path.GetRelativePath(Root(), file);
            offenders.AddRange(Offenders(File.ReadAllText(file), file.EndsWith(".xaml", StringComparison.Ordinal)).Select(o => $"{name}: {o}"));
        }

        Assert.True(offenders.Count == 0, $"{offenders.Count} Czech strings use the imperative or vykání:\n" + string.Join('\n', offenders.Take(60)));
    }

    [Theory]
    [InlineData("\"Zapněte laser a připojte jej.\"", true)]
    [InlineData("\"Zkontrolujte USB kabel.\"", true)]
    [InlineData("\"Zkuste to znovu.\"", true)]
    [InlineData("\"Uložte projekt jinam.\"", true)]
    [InlineData("\"Vyberte objekt.\"", true)]
    [InlineData("\"Můžete pokračovat.\"", true)]
    [InlineData("\"Vlastnosti najdete v panelu.\"", true)]
    [InlineData("\"Přihlaste se prosím znovu.\"", true)]
    [InlineData("\"Váš počítač.\"", true)]
    [InlineData("\"Laser je potřeba zapnout a připojit.\"", false)]
    [InlineData("\"Zkontrolovat lze USB kabel.\"", false)]
    [InlineData("\"Připojit\"", false)]
    [InlineData("\"Uložit\"", false)]
    [InlineData("\"Vybrat lze objekt.\"", false)]
    [InlineData("\"Pracovní plocha se po připojení načte z GRBL.\"", false)]
    [InlineData("\"Delete\"", false)]
    public void TheScanRecognisesImperativesAndLeavesInfinitivesAlone(string source, bool flagged) =>
        Assert.Equal(flagged, Offenders(source, xaml: false).Any());

    [Fact]
    public void CommentsAreNotScanned() =>
        Assert.Empty(Offenders("// Zkontrolujte tohle\n/// Zapněte to\n", xaml: false).Concat(Offenders("<!-- Zkontrolujte \"Zkuste\" -->", xaml: true)));
}
