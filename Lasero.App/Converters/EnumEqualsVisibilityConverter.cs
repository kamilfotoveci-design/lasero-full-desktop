using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Visible when the bound enum value's name matches the converter parameter (e.g. ConverterParameter="Svg").</summary>
public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
