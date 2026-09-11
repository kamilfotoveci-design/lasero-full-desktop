using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>true when bound to an int greater than zero — used to enable node-edit toolbar actions
/// only while at least one node is selected.</summary>
public sealed class PositiveCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
