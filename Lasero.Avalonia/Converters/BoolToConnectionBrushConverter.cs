using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lasero.Avalonia.Converters;

/// <summary>Connection.IsConnected -> the status dot colour. Mirrors Lasero.App's
/// BooleanToConnectionBrushConverter (green when connected, muted grey otherwise).</summary>
public sealed class BoolToConnectionBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = new(Color.FromRgb(0x3D, 0xBE, 0x64));
    private static readonly SolidColorBrush Disconnected = new(Color.FromRgb(0x6B, 0x6B, 0x73));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
