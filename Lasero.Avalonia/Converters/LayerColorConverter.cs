using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lasero.Core.Layers;

namespace Lasero.Avalonia.Converters;

/// <summary>RgbColor -> SolidColorBrush, for the OPERACE list's per-layer colour swatch.</summary>
public sealed class RgbColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RgbColor color ? new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
