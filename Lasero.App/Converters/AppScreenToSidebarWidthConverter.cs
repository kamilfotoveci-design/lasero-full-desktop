using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Lasero.App.ViewModels;

namespace Lasero.App.Converters;

public sealed class AppScreenToSidebarWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(176);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
