using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>True only when the bound count is exactly 1 — used for node-edit operations like "Break
/// at Node" that are only meaningful for a single selected node, unlike PositiveCountConverter's
/// "one or more" (delete/convert-type, which apply fine to a multi-node selection).</summary>
public sealed class SingleCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count == 1;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
