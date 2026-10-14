using System.Globalization;
using System.Windows.Data;
using Lasero.Core.Layers;

namespace Lasero.App.Converters;

/// <summary>
/// Names a palette swatch so an icon-free colour square still has something to say in a tooltip and
/// to a screen reader. The swatch is a colour, not a layer (a layer of that colour may not exist yet),
/// so the text is the colour's name alone. Falls back to the hex value for anything outside the
/// standard palette rather than inventing a name for it.
/// </summary>
public sealed class RgbColorToNameConverter : IValueConverter
{
    // Exactly the SceneViewModel.LayerPalette values, in the same order.
    private static readonly (byte R, byte G, byte B, string Name)[] Known =
    [
        (18, 18, 18, "Černá"),
        (214, 42, 42, "Červená"),
        (57, 78, 59, "Tmavě zelená"),
        (232, 180, 0, "Žlutá"),
        (42, 157, 82, "Zelená"),
        (0, 151, 167, "Tyrkysová"),
        (31, 95, 204, "Modrá"),
        (92, 76, 196, "Fialová"),
        (179, 62, 153, "Purpurová"),
        (117, 72, 42, "Hnědá"),
        (98, 105, 113, "Šedá"),
        (173, 181, 189, "Světle šedá"),
    ];

    /// <summary>The Czech name of a palette colour, or null for a colour outside the palette.
    /// Palette entries drift by a shade or two between the editor and imported artwork, so the match
    /// is by proximity (closest entry within a small tolerance) rather than an exact triple.</summary>
    public static string? KnownName(RgbColor color)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var (r, g, b, name) in Known)
        {
            if (Math.Abs(color.R - r) > 12 || Math.Abs(color.G - g) > 12 || Math.Abs(color.B - b) > 12) continue;
            var distance = Math.Abs(color.R - r) + Math.Abs(color.G - g) + Math.Abs(color.B - b);
            if (distance >= bestDistance) continue;
            best = name;
            bestDistance = distance;
        }
        return best;
    }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is RgbColor color ? KnownName(color) ?? color.ToHex() : "Barva";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when a palette swatch is the colour of the currently selected layer, so the swatch row can
/// show which entry is the active one instead of presenting twelve identical squares.
/// </summary>
public sealed class RgbColorMatchesConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not RgbColor swatch || values[1] is not RgbColor selected)
            return false;

        return swatch.IsApproximately(selected);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
