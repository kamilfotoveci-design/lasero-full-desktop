using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lasero.Core.Import.Svg;

/// <summary>SVG/CSS lengths ("10", "5px", "20mm", "1.5in", "50%") converted to user units, where
/// one user unit is one CSS pixel (96 per inch) as the SVG spec defines for absolute units.</summary>
internal static class SvgLength
{
    public const double PxPerMm = 96.0 / 25.4;

    private static readonly Regex Pattern = new(
        @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z%]*)$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="value"/> is a valid length. A percentage needs
    /// <paramref name="percentBase"/>; without it the length is reported as unresolvable.</summary>
    public static bool TryParse(string? value, out double userUnits, double? percentBase = null)
    {
        userUnits = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Pattern.Match(value.Trim());
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
            return false;

        switch (match.Groups[2].Value.ToLowerInvariant())
        {
            case "" or "px": userUnits = number; return true;
            case "mm": userUnits = number * PxPerMm; return true;
            case "cm": userUnits = number * PxPerMm * 10; return true;
            case "in": userUnits = number * 96; return true;
            case "pt": userUnits = number * 96 / 72; return true;
            case "pc": userUnits = number * 16; return true;
            case "em": userUnits = number * 16; return true;
            case "ex": userUnits = number * 8; return true;
            case "%":
                if (percentBase is not { } basis) return false;
                userUnits = number * basis / 100;
                return true;
            default: return false;
        }
    }

    /// <summary>Reads attribute <paramref name="attribute"/> as a length in user units; a missing or
    /// unreadable value gives <paramref name="fallback"/> (and is reported through <paramref name="warn"/>
    /// when the attribute was present but unusable).</summary>
    public static double Get(XElement element, string attribute, double fallback = 0, Action<string>? warn = null, double? percentBase = null)
    {
        var raw = (string?)element.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (TryParse(raw, out var value, percentBase)) return value;
        warn?.Invoke($"Neplatná délka {attribute}=\"{raw}\" v prvku <{element.Name.LocalName}> byla ignorována.");
        return fallback;
    }
}
