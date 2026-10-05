using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.Core.Grbl;

namespace Lasero.App.Converters;

/// <summary>
/// Resolves the semantic brushes from the theme at conversion time, so no status colour is
/// hard-coded here and the palette stays in LaseroTheme.xaml.
/// </summary>
internal static class ThemeBrushes
{
    public static Brush Resolve(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class MachineAlertKindToBrushConverter : IValueConverter
{
    private static Brush Warn => ThemeBrushes.Resolve("Brush.Warning");
    private static Brush Danger => ThemeBrushes.Resolve("Brush.Danger");

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
        // Unknown is also the state of a freshly connected controller that has not reported yet —
        // labelling it "NEPŘIPOJENO" contradicted the connection indicator sitting right next to it.
        _ => "ČEKÁM NA STAV",
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToConnectionBrushConverter : IValueConverter
{
    private static Brush Connected => ThemeBrushes.Resolve("Brush.Success");
    private static Brush Disconnected => ThemeBrushes.Resolve("Brush.TextMuted");

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MachineModeToBrushConverter : IValueConverter
{
    // A running or jogging machine is the live-job indicator, which is one of the few things the
    // signal red is reserved for.
    private static Brush Idle => ThemeBrushes.Resolve("Brush.Success");
    private static Brush Run => ThemeBrushes.Resolve("Brush.Signal");
    private static Brush Warn => ThemeBrushes.Resolve("Brush.Warning");
    private static Brush Danger => ThemeBrushes.Resolve("Brush.Danger");
    private static Brush Neutral => ThemeBrushes.Resolve("Brush.TextMuted");

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
