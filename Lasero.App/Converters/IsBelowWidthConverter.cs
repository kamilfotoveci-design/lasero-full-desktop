using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// True when the bound width is below the threshold passed as ConverterParameter.
///
/// WPF has no media queries, so adaptive chrome is driven by binding to the window's ActualWidth and
/// comparing here. Thresholds are content-driven — the width at which a particular toolbar actually
/// stops fitting — not generic device breakpoints.
/// </summary>
public sealed class IsBelowWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width || !double.IsFinite(width)) return false;

        var threshold = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0d,
        };

        // Width is 0 during the first measure pass; treating that as "narrow" would make the toolbar
        // flash into compact mode on every window open.
        return width > 0 && width < threshold;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
