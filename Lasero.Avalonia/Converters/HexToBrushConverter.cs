using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lasero.Avalonia.Converters;

/// <summary>"#RRGGBB" -> SolidColorBrush. WPF binds these swatch colours as plain strings and relies
/// on its default BrushConverter; Avalonia's compiled bindings need this explicit instead.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
