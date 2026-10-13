using System.Globalization;
using Avalonia.Data.Converters;
using Lasero.Core.Grbl;

namespace Lasero.Avalonia.Converters;

/// <summary>Verbatim port of Lasero.App's MachineModeToLabelConverter — same Czech uppercase labels,
/// same "unknown/not yet reported" fallback wording.</summary>
public sealed class MachineModeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
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
        _ => "ČEKÁM NA STAV",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
