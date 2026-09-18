using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Binds a menu item's ToolTip to a "why is this disabled" reason string (null when the
/// action is available) while falling back to the item's own always-relevant description when it
/// is enabled — used so a boolean-op/offset menu item can show a specific Czech explanation while
/// disabled (see SceneViewModel.UniteSelectionDisabledReason/OffsetSelectionDisabledReason) without
/// losing the descriptive tooltip ("Tvary vpředu odečtou plochu od tvaru vzadu" etc.) it already had
/// while enabled. ConverterParameter carries that fallback text.</summary>
public sealed class DisabledReasonOrFallbackConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } reason ? reason : parameter as string;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
