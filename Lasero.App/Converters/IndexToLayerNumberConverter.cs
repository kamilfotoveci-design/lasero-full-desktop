using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

public sealed class IndexToLayerNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString("00", culture) : "--";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
