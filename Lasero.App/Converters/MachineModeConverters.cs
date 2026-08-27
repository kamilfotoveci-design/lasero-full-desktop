using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.Core.Grbl;

namespace Lasero.App.Converters;

public sealed class MachineAlertKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xE6, 0xA9, 0x3E));
    private static readonly SolidColorBrush Danger = new(Color.FromRgb(0xD9, 0x34, 0x2B));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MachineAlertKind.Alarm => Danger,
        MachineAlertKind.Error => Warn,
        _ => Danger,
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MachineModeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        GrblMachineMode.Idle => "PŘIPRAVENO",
        GrblMachineMode.Run => "PROBÍHÁ",
        GrblMachineMode.Hold => "POZASTAVENO",
        GrblMachineMode.Jog => "JOG",
        GrblMachineMode.Alarm => "ALARM",
        GrblMachineMode.Door => "KRYT OTEVŘEN",
        GrblMachineMode.Check => "KONTROLA",
        GrblMachineMode.Home => "HOMING",
        GrblMachineMode.Sleep => "SPÁNEK",
        _ => "NEPŘIPOJENO",
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToConnectionBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = new(Color.FromRgb(0x3D, 0xBE, 0x64));
    private static readonly SolidColorBrush Disconnected = new(Color.FromRgb(0x6B, 0x6B, 0x73));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MachineModeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Idle = new(Color.FromRgb(0x3D, 0xBE, 0x64));
    private static readonly SolidColorBrush Run = new(Color.FromRgb(0x3D, 0x9B, 0xE6));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xE6, 0xA9, 0x3E));
    private static readonly SolidColorBrush Danger = new(Color.FromRgb(0xFE, 0x00, 0x00));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x6B, 0x6B, 0x73));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        GrblMachineMode.Idle => Idle,
        GrblMachineMode.Run or GrblMachineMode.Jog or GrblMachineMode.Home => Run,
        GrblMachineMode.Hold or GrblMachineMode.Check or GrblMachineMode.Sleep => Warn,
        GrblMachineMode.Alarm or GrblMachineMode.Door => Danger,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
