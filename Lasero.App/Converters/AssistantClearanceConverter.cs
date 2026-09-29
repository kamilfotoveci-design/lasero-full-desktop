using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>
/// Margin for the floating assistant host, which is declared inside the canvas column
/// (<c>WorkspaceArea</c>) and therefore already ends at the splitter. Only the canvas's own
/// undo / redo / pan / zoom cluster (<see cref="Lasero.App.Views.CanvasViewControls"/>, bottom-right,
/// 20px in) needs to be cleared: the head shares the cluster's right edge and rests 12px above it,
/// exactly where the 2c5dcbf build put it.
///
/// Nothing here reads the inspector. An earlier version subtracted the inspector's measured width
/// from a host that spanned the inspector column too; when the inspector measured 0 (empty or not yet
/// laid out) the head landed on top of the inspector and the status strip.
/// </summary>
public sealed class AssistantClearanceConverter : IMultiValueConverter
{
    public const double RightInset = 20;
    public const double ClusterBottomInset = 20;
    public const double ClusterGap = 12;

    public static Thickness Clearance(double controlsHeight) =>
        new(0, 0, RightInset, ClusterBottomInset + Math.Max(0, controlsHeight) + ClusterGap);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Clearance(values.Length > 0 && values[0] is double value && double.IsFinite(value) ? value : 0);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
