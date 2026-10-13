using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia.Converters;

/// <summary>ShapeKind -> the geometry the rail button/picker draws for that shape. Mirrors
/// Lasero.App's ShapeToolIconConverter.</summary>
public sealed class ShapeKindToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ShapeKind shape) return null;
        var key = ShapeToolCatalog.Get(shape).IconKey;
        return Application.Current?.TryGetResource(key, null, out var resource) == true ? resource as Geometry : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ShapeKind -> the tooltip for the shape-tool button. Mirrors Lasero.App's
/// ShapeToolTooltipConverter.</summary>
public sealed class ShapeKindToTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ShapeKind shape) return "Tvary — obdélník, elipsa, mnohoúhelníky a hvězdy";
        var def = ShapeToolCatalog.Get(shape);
        return $"{def.TooltipText} — přidržte nebo klikněte pravým tlačítkem pro další tvary";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
