using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Shortens a raw GRBL firmware banner (e.g. "Grbl 1.1h ['$' for help]") down to the part an
/// operator would actually recognise. Same chop-at-'[' rule <c>DeviceSetupViewModel.FirmwareLabel</c>
/// already uses for the Zařízení status card — kept here as its own converter so this view can reuse
/// the exact behaviour without reaching into that unrelated view-model.</summary>
public sealed class FirmwareBannerToNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var banner = value as string;
        if (string.IsNullOrWhiteSpace(banner)) return "Neznámo";
        var bracket = banner.IndexOf('[');
        return (bracket > 0 ? banner[..bracket] : banner).Trim();
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
