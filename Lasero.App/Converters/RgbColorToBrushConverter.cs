using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.Core.Layers;

namespace Lasero.App.Converters;

public sealed class RgbColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is RgbColor c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
