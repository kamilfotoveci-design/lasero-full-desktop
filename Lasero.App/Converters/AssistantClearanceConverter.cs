using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// Keeps the floating assistant clear of the right inspector and of the canvas's own undo / redo /
/// pan / zoom cluster (<see cref="Lasero.App.Views.CanvasViewControls"/>), bottom-right of the
/// workspace.
///
/// The assistant is wider than the inspector, so anchoring it to the window's right edge would bury
/// the operation panel — including the settings that "Použít na operaci" writes to, which is exactly
/// what the operator wants to watch change. Offsetting it inward by the inspector's live width parks
/// it against the canvas instead, and it follows the splitter when the inspector is resized.
///
/// The assistant used to sit almost flush on top of the view controls because the only vertical
/// reservation was a fixed 16px inset, not the controls' actual measured height. Taking that height
/// as a second input keeps the assistant's Minimized/QuickAsk/Expanded shapes clear of the cluster at
/// every window size, rather than only on the one size someone happened to test at.
///
/// On screens with no inspector, or before <see cref="Lasero.App.Views.CanvasViewControls"/> has
/// measured, the corresponding input is zero and that term drops out.
/// </summary>
public sealed class AssistantClearanceConverter : IMultiValueConverter
{
    private const double Inset = 20;
    private const double BottomInset = 16;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var inspectorWidth = MeasureOf(values, 0);
        var controlsHeight = MeasureOf(values, 1);
        return new Thickness(0, 0, Inset + inspectorWidth, BottomInset + controlsHeight + BottomInset);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double MeasureOf(object[] values, int index) =>
        index < values.Length && values[index] is double value && double.IsFinite(value) && value > 0
            ? value
            : 0;
}
