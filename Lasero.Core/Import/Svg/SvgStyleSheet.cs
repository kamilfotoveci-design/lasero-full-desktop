using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lasero.Core.Import.Svg;

/// <summary>
/// Minimal CSS for SVG &lt;style&gt; blocks — what Illustrator/Inkscape/Figma actually emit
/// (".cls-1{fill:none;stroke:#f00}"). Supports type, .class, #id and compound selectors
/// ("path.a#b"), "*", and comma lists; ignores @-rules. Combinators, attribute selectors and
/// pseudo-classes are not interpreted and are reported through <see cref="UnsupportedSelectors"/>
/// so the importer can tell the operator instead of silently dropping the rule.
/// </summary>
internal sealed class SvgStyleSheet
{
    private sealed record Rule(string? Tag, string? Id, IReadOnlyList<string> Classes, int Specificity, int Order,
        IReadOnlyDictionary<string, string> Declarations);

    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AtStatements = new(@"@(?:import|charset|namespace)[^;]*;", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Compound = new(@"^(\*|[A-Za-z][\w-]*)?((?:[.#][\w-]+)*)$", RegexOptions.Compiled);
    private static readonly Regex Part = new(@"([.#])([\w-]+)", RegexOptions.Compiled);

    private readonly List<Rule> _rules = [];

    public static SvgStyleSheet Empty { get; } = new();

    public bool HasRules => _rules.Count > 0;
    public int SkippedAtRules { get; private set; }
    public List<string> UnsupportedSelectors { get; } = [];

    public static SvgStyleSheet Build(XElement root)
    {
        var sheet = new SvgStyleSheet();
        foreach (var style in root.DescendantsAndSelf().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
        {
            var type = (string?)style.Attribute("type");
            if (!string.IsNullOrWhiteSpace(type) && !type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase)) continue;
            sheet.AddCss(style.Value);
        }
        return sheet;
    }

    public void AddCss(string css)
    {
        var text = AtStatements.Replace(Comments.Replace(css, string.Empty), string.Empty);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            if (open < 0) break;
            var header = text[index..open].Trim();
            var depth = 1;
            var cursor = open + 1;
            while (cursor < text.Length && depth > 0)
            {
                if (text[cursor] == '{') depth++;
                else if (text[cursor] == '}') depth--;
                cursor++;
            }
            var bodyEnd = depth == 0 ? cursor - 1 : cursor;
            var body = text.Substring(open + 1, bodyEnd - (open + 1));
            index = cursor;

            if (header.StartsWith('@')) { SkippedAtRules++; continue; }

            var declarations = ParseDeclarations(body);
            if (declarations.Count == 0) continue;

            foreach (var selectorText in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var match = Compound.Match(selectorText);
                if (!match.Success)
                {
                    UnsupportedSelectors.Add(selectorText);
                    continue;
                }

                var tag = match.Groups[1].Success && match.Groups[1].Value != "*" ? match.Groups[1].Value : null;
                string? id = null;
                var classes = new List<string>();
                foreach (Match part in Part.Matches(match.Groups[2].Value))
                {
                    if (part.Groups[1].Value == "#") id = part.Groups[2].Value;
                    else classes.Add(part.Groups[2].Value);
                }

                var specificity = (id is null ? 0 : 100) + classes.Count * 10 + (tag is null ? 0 : 1);
                _rules.Add(new Rule(tag, id, classes, specificity, _rules.Count, declarations));
            }
        }
    }

    /// <summary>Declarations of every rule matching <paramref name="element"/>, lower specificity
    /// (then earlier rules) first so later entries override earlier ones.</summary>
    public IReadOnlyDictionary<string, string> Match(XElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_rules.Count == 0) return result;

        var name = element.Name.LocalName;
        var id = (string?)element.Attribute("id");
        var classAttribute = (string?)element.Attribute("class");
        var classes = string.IsNullOrWhiteSpace(classAttribute)
            ? Array.Empty<string>()
            : classAttribute.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rule in _rules
                     .Where(rule => (rule.Tag is null || rule.Tag.Equals(name, StringComparison.OrdinalIgnoreCase))
                                    && (rule.Id is null || rule.Id == id)
                                    && rule.Classes.All(required => classes.Contains(required, StringComparer.Ordinal)))
                     .OrderBy(rule => rule.Specificity).ThenBy(rule => rule.Order))
            foreach (var declaration in rule.Declarations)
                result[declaration.Key] = declaration.Value;
        return result;
    }

    /// <summary>Splits "a:b;c:d" into a dictionary (also used for inline style attributes).</summary>
    public static Dictionary<string, string> ParseDeclarations(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair[0].Length == 0) continue;
            var value = pair[1];
            var important = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (important >= 0) value = value[..important].Trim();
            if (value.Length > 0) result[pair[0]] = value;
        }
        return result;
    }
}
