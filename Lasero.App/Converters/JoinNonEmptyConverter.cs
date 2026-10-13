using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Joins the non-empty string values of a MultiBinding with a middle dot, so a line such as
/// "Návrh · Překližka 3 mm · xTool D1" never shows a separator next to a fact the application does not
/// hold. ConverterParameter overrides the separator.</summary>
public sealed class JoinNonEmptyConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var separator = parameter as string ?? " · ";
        return string.Join(separator, values.OfType<string>().Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
