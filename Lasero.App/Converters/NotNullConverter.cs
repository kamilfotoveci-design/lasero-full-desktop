using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>true when bound to any non-null value — used to enable/disable the object property panel
/// based on whether SceneViewModel.Selected currently points at something.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
