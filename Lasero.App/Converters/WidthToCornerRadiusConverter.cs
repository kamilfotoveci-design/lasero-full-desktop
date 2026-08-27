using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Makes fixed-size buttons (jog pad, icon buttons) render as perfect circles/pills without a bespoke template per size.</summary>
public sealed class WidthToCornerRadiusConverter : IValueConverter
{
    public double DefaultRadius { get; set; } = 8;

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && !double.IsNaN(width) && width > 0)
            return new CornerRadius(width / 2);
        return new CornerRadius(DefaultRadius);
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
