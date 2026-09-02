using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// Keeps the floating assistant clear of the right inspector.
///
/// The assistant is wider than the inspector, so anchoring it to the window's right edge would bury
/// the operation panel — including the settings that "Použít na operaci" writes to, which is exactly
/// what the operator wants to watch change. Offsetting it inward by the inspector's live width parks
/// it against the canvas instead, and it follows the splitter when the inspector is resized.
///
/// On screens with no inspector the measured width is zero and the assistant sits at the normal
/// window inset.
/// </summary>
public sealed class AssistantClearanceConverter : IValueConverter
{
    private const double Inset = 20;
    private const double BottomInset = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inspectorWidth = value is double width && double.IsFinite(width) && width > 0 ? width : 0;
        return new Thickness(0, 0, Inset + inspectorWidth, BottomInset);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
