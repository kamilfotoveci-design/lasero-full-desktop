using System.Globalization;
using System.Text.RegularExpressions;

namespace Lasero.App.ViewModels;

/// <summary>
/// Machine settings Kamil named in an answer, lifted out of the prose so they can be applied to an
/// operation with one click instead of retyped by hand.
///
/// Parsed, never invented. The endpoint returns free text, so the only honest source for a
/// recommendation card is the reply itself: if the numbers are not in there, there is no card. Speed
/// and power are both required — a card offering to apply half a recipe would be worse than no card,
/// because the operator would have to notice which half was missing.
/// </summary>
public sealed partial class ParameterRecommendation
{
    public double SpeedMmPerMinute { get; }
    public double PowerPercent { get; }
    public int Passes { get; }
    public int? Dpi { get; }

    /// <summary>
    /// Which operation was selected in the workspace when the message carrying this recommendation
    /// arrived, if known. Not part of parsing — TryParse only ever reads the reply text — so this is
    /// set once by <see cref="ChatMessageItem.Recommendation"/> right after parsing, from the layer id
    /// the message itself captured. <see cref="KamilAssistantViewModel.IsRecommendationStale"/> uses
    /// it to tell whether an old card in scrollback still matches what is selected now.
    /// </summary>
    public Guid? OriginLayerId { get; internal set; }

    /// <summary>Scan-line spacing implied by the DPI, in the same unit LayerSettings stores. Falls
    /// back to the layer default when the answer named no resolution.</summary>
    public double FillLineIntervalMm => Dpi is > 0 ? 25.4 / Dpi.Value : 25.4 / 254;

    public string SpeedLabel => SpeedMmPerMinute.ToString("0", CultureInfo.CurrentCulture);
    public string PowerLabel => PowerPercent.ToString("0.#", CultureInfo.CurrentCulture);
    public string PassesLabel => Passes.ToString("0", CultureInfo.CurrentCulture);
    public string DpiLabel => Dpi?.ToString("0", CultureInfo.CurrentCulture) ?? "Neuvedeno";
    public bool HasDpi => Dpi is > 0;

    private ParameterRecommendation(double speed, double power, int passes, int? dpi)
    {
        SpeedMmPerMinute = speed;
        PowerPercent = power;
        Passes = passes;
        Dpi = dpi;
    }

    /// <summary>
    /// Pulls a recommendation out of an assistant reply, or returns null when the reply does not
    /// carry one.
    ///
    /// Deliberately conservative. Every value must appear next to its own label, and every value is
    /// range-checked against what a diode engraver can actually be set to — a stray "40" in a
    /// sentence about 40 mm of material must never turn into 40 % power on a real machine.
    /// </summary>
    public static ParameterRecommendation? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // The service sends the settings as one machine line, "PARAMS:speed=400,power=85,passes=1,dpi=0".
        // That is the authoritative form when present; the prose patterns below remain for replies without it.
        if (TryParseMachineLine(text) is { } fromLine) return fromLine;

        var speed = MatchNumber(SpeedPattern(), text);
        var power = MatchNumber(PowerPattern(), text);
        if (speed is null || power is null) return null;
        if (speed is <= 0 or > 60000) return null;
        if (power is < 0 or > 100) return null;

        var passes = MatchNumber(PassesPattern(), text);
        var dpi = MatchNumber(DpiPattern(), text);

        return new ParameterRecommendation(
            speed.Value,
            power.Value,
            passes is >= 1 and <= 50 ? (int)passes.Value : 1,
            dpi is >= 50 and <= 2000 ? (int)dpi.Value : null);
    }
    /// <summary>The reply with its machine-readable PARAMS line (and a lone "Doporučené parametry:" heading
    /// left above it) removed, so the operator sees the card instead of the raw line.</summary>
    public static string StripMachineLine(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("PARAMS:", StringComparison.OrdinalIgnoreCase)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].TrimStart().StartsWith("PARAMS:", StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(i);
            if (i > 0 && lines[i - 1].Trim().TrimEnd(':').Equals("Doporučené parametry", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i - 1);
        }
        return string.Join('\n', lines).Trim();
    }

    private static ParameterRecommendation? TryParseMachineLine(string text)
    {
        var match = MachineLinePattern().Match(text);
        if (!match.Success) return null;
        double? speed = null, power = null, passes = null, dpi = null;
        foreach (Match pair in MachinePairPattern().Matches(match.Groups["body"].Value))
        {
            var raw = pair.Groups["value"].Value.Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            switch (pair.Groups["key"].Value.ToLowerInvariant())
            {
                case "speed": speed = value; break;
                case "power": power = value; break;
                case "passes": passes = value; break;
                case "dpi": dpi = value; break;
            }
        }

        if (speed is null || power is null || speed is <= 0 or > 60000 || power is < 0 or > 100) return null;
        return new ParameterRecommendation(
            speed.Value,
            power.Value,
            passes is >= 1 and <= 50 ? (int)passes.Value : 1,
            dpi is >= 50 and <= 2000 ? (int)dpi.Value : null);
    }

    [GeneratedRegex(@"PARAMS:(?<body>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineLinePattern();

    [GeneratedRegex(@"(?<key>speed|power|passes|dpi)\s*=\s*(?<value>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachinePairPattern();

    private static double? MatchNumber(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        if (!match.Success) return null;
        var raw = match.Groups["value"].Value.Replace(',', '.').Replace(" ", string.Empty);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // "rychlost 4000 mm/min", "rychlost: 4 000 mm/min", "speed 4000mm/min". The unit is required, so
    // a bare number after the word cannot be mistaken for a feed rate.
    [GeneratedRegex(@"(?:rychlost|posuv|speed)\D{0,12}?(?<value>\d[\d\s]{0,6}(?:[.,]\d+)?)\s*mm\s*/\s*min",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedPattern();

    [GeneratedRegex(@"(?:výkon|vykon|power)\D{0,12}?(?<value>\d{1,3}(?:[.,]\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerPattern();

    // Either "průchody 2" / "2 průchody" / "2×" directly after the label.
    [GeneratedRegex(@"(?:průchod\w*|pruchod\w*|passes?)\D{0,12}?(?<value>\d{1,2})\s*(?:×|x)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassesPattern();

    [GeneratedRegex(@"(?:dpi|rozlišení|rozliseni)\D{0,12}?(?<value>\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DpiPattern();
}
