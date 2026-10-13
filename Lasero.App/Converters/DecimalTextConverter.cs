using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// A number typed into a settings field, in either decimal convention. Bindings parse with the
/// markup language (en-US here), so "0,5" typed by a Czech user would be rejected; this accepts both
/// "0,5" and "0.5", shows the value with the user's own separator, and reports anything else as an
/// invalid entry instead of silently keeping the old value.
/// </summary>
[ValueConversion(typeof(double), typeof(string))]
public sealed class DecimalTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double number || !double.IsFinite(number)) return string.Empty;
        var format = parameter as string ?? "0.###";
        return number.ToString(format, CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim().Replace(" ", string.Empty).Replace(',', '.');
        if (!TryParse(text, out var number))
            throw new FormatException("Hodnota není platné číslo.");
        return number;
    }

    public static bool TryParse(string? text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalised = text.Trim().Replace(" ", string.Empty).Replace(',', '.');
        return double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }
}
