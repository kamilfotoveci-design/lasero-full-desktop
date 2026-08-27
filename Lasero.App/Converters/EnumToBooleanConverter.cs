using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Binds an enum property to a RadioButton's IsChecked — ConverterParameter is the enum value name this RadioButton represents.</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter?.ToString() ?? string.Empty) : Binding.DoNothing;
}
