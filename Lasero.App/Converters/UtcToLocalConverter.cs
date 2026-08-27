using System.Globalization;
using System.Windows.Data;

namespace Lasero.App.Converters;

/// <summary>Converts a stored UTC timestamp to local time before any StringFormat is applied — job
/// history and recent-project timestamps are persisted in UTC, but should read as the user's own
/// wall-clock time, not UTC.</summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime dt ? dt.ToLocalTime() : value;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
