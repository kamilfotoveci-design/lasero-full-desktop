using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Visible when bound to any non-null (and non-empty, for strings) value — used on the Home
/// dashboard to switch between real content and an honest empty state. Pass ConverterParameter="Invert"
/// for the opposite (visible only when null/empty) rather than adding a second converter class.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is string text ? !string.IsNullOrWhiteSpace(text) : value is not null;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
