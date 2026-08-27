using System.Globalization;

namespace Lasero.Core.Layers;

/// <summary>Plain RGB — Lasero.Core has no WPF dependency, so this isn't System.Windows.Media.Color.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor Red = new(255, 0, 0);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    public override string ToString() => ToHex();

    public static bool TryParse(string? value, out RgbColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim().ToLowerInvariant();

        if (value is "red") { color = Red; return true; }
        if (value is "black") { color = Black; return true; }
        if (value is "none" or "transparent" or "white") return false;

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                color = new RgbColor(r, g, b);
                return true;
            }
            return false;
        }

        if (value.StartsWith("rgb(") && value.EndsWith(')'))
        {
            var parts = value[4..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 &&
                byte.TryParse(parts[0], out var r2) &&
                byte.TryParse(parts[1], out var g2) &&
                byte.TryParse(parts[2], out var b2))
            {
                color = new RgbColor(r2, g2, b2);
                return true;
            }
        }
        return false;
    }

    public bool IsApproximately(RgbColor other, int tolerance = 20) =>
        Math.Abs(R - other.R) <= tolerance && Math.Abs(G - other.G) <= tolerance && Math.Abs(B - other.B) <= tolerance;
}
