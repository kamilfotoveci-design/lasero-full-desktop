using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.App.ViewModels;

namespace Lasero.App.Converters;

/// <summary>DesignerTool -> the geometry the toolbar/picker draws for that shape, via ShapeToolCatalog.</summary>
public sealed class ShapeToolIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DesignerTool tool || !ShapeToolCatalog.IsShapeTool(tool)) return null;
        var key = ShapeToolCatalog.Get(tool).IconKey;
        return Application.Current.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>DesignerTool -> the tooltip for the shape-tool button, naming the remembered shape and
/// hinting that a press-and-hold reaches the rest.</summary>
public sealed class ShapeToolTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DesignerTool tool || !ShapeToolCatalog.IsShapeTool(tool))
            return "Tvary — obdélník, elipsa, mnohoúhelníky a hvězdy";
        var def = ShapeToolCatalog.Get(tool);
        return $"{def.TooltipText} — přidržením nebo pravým tlačítkem lze zvolit další tvary";
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>DesignerTool -> whether it is one of the eight shape tools, so the rail button can show
/// itself as "active" the same way the RadioButton tools do while a shape tool is drawing.</summary>
public sealed class IsShapeToolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is DesignerTool tool && ShapeToolCatalog.IsShapeTool(tool);

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
