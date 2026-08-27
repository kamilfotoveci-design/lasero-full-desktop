using System.Globalization;
using System.Windows.Data;
using Lasero.Core.Layers;

namespace Lasero.App.Converters;

/// <summary>
/// Names a palette swatch so an icon-free colour square still has something to say in a tooltip and
/// to a screen reader. Falls back to the hex value for anything outside the standard palette rather
/// than inventing a name for it.
/// </summary>
public sealed class RgbColorToNameConverter : IValueConverter
{
    private static readonly (byte R, byte G, byte B, string Name)[] Known =
    [
        (0x00, 0x00, 0x00, "Černá"),
        (0xE0, 0x1B, 0x24, "Červená"),
        (0xF2, 0x71, 0x1C, "Oranžová"),
        (0xF5, 0xC2, 0x11, "Žlutá"),
        (0x2E, 0xA0, 0x43, "Zelená"),
        (0x12, 0xB5, 0xCB, "Tyrkysová"),
        (0x25, 0x63, 0xEB, "Modrá"),
        (0x7C, 0x3A, 0xED, "Fialová"),
        (0xDB, 0x27, 0x77, "Růžová"),
        (0x8B, 0x4A, 0x1F, "Hnědá"),
        (0x6B, 0x70, 0x6D, "Šedá"),
        (0xB6, 0xBB, 0xB8, "Světle šedá"),
    ];

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RgbColor color) return "Barva vrstvy";

        foreach (var (r, g, b, name) in Known)
        {
            // Palette entries drift by a shade or two between the editor and imported artwork, so
            // match on proximity rather than requiring an exact triple.
            if (Math.Abs(color.R - r) <= 12 && Math.Abs(color.G - g) <= 12 && Math.Abs(color.B - b) <= 12)
                return $"Vrstva: {name}";
        }

        return $"Vrstva: {color.ToHex()}";
    }

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
