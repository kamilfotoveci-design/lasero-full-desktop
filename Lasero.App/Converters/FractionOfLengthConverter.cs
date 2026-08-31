using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// A share of another element's measured length, for a MaxHeight that has to scale with the window.
///
/// The inspector's selection section is an Auto row above the tab switch, so it takes whatever height
/// its content wants and the tab content gets the remainder. A text selection makes that section tall
/// enough to starve the tabs at a small window, and a fixed cap is wrong in the other direction: it
/// hides the font picker on a large one. The cap is therefore a fraction of the panel.
/// </summary>
public sealed class FractionOfLengthConverter : IValueConverter
{
    /// <summary>Below this the cap is not applied at all — a share of a not-yet-measured element is
    /// zero, which would collapse the section entirely on the first layout pass.</summary>
    private const double MinimumUsableLength = 80;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double length || !double.IsFinite(length) || length < MinimumUsableLength)
            return double.PositiveInfinity;

        var fraction = parameter is null
            ? 0.5
            : System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        if (!double.IsFinite(fraction) || fraction <= 0) return double.PositiveInfinity;

        return length * fraction;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
