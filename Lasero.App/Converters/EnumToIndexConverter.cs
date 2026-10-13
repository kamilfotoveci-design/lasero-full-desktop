using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Binds an enum whose members are declared in tab order to a TabControl's SelectedIndex. No selection (-1) writes
/// nothing back, so a tab strip that is momentarily empty never overwrites the model.</summary>
public sealed class EnumToIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is Enum ? System.Convert.ToInt32(value, CultureInfo.InvariantCulture) : -1;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return value is int index && index >= 0 && Enum.IsDefined(enumType, index)
            ? Enum.ToObject(enumType, index)
            : Binding.DoNothing;
    }
}
