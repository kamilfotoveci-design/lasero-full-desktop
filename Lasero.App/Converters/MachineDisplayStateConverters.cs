using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.Core.Machines;

namespace Lasero.App.Converters;

/// <summary>
/// The machine's display state in Czech. Presentation only — nothing here gates a command. Note that
/// Disconnected is "Nepřipojeno" and Idle is "Připraveno": Idle means the controller has answered and
/// reported that it is standing still, which is the only case where claiming readiness is honest.
/// </summary>
public sealed class MachineDisplayStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LaserMachineDisplayState state
            ? state switch
            {
                LaserMachineDisplayState.Disconnected => "Nepřipojeno",
                LaserMachineDisplayState.Connecting => "Připojuji",
                LaserMachineDisplayState.Idle => "Připraveno",
                LaserMachineDisplayState.Run => "Pracuje",
                LaserMachineDisplayState.Hold => "Pozastaveno",
                LaserMachineDisplayState.Jog => "Ruční posun",
                LaserMachineDisplayState.Alarm => "Alarm",
                LaserMachineDisplayState.Door => "Otevřená dvířka",
                LaserMachineDisplayState.Check => "Kontrolní režim",
                LaserMachineDisplayState.Home => "Hledá počátek",
                LaserMachineDisplayState.Sleep => "Spánek",
                LaserMachineDisplayState.Error => "Chyba",
                _ => "Neznámý stav",
            }
            : "Nepřipojeno";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour for that same state. Green only for Idle, because green here reads as "you may
/// start"; anything the app cannot confirm stays neutral grey rather than optimistic.</summary>
public sealed class MachineDisplayStateToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is LaserMachineDisplayState state
            ? state switch
            {
                LaserMachineDisplayState.Idle => "Brush.Success",
                LaserMachineDisplayState.Run or LaserMachineDisplayState.Jog or LaserMachineDisplayState.Home => "Brush.Accent",
                LaserMachineDisplayState.Hold or LaserMachineDisplayState.Door or LaserMachineDisplayState.Check => "Brush.Warning",
                LaserMachineDisplayState.Alarm or LaserMachineDisplayState.Error => "Brush.Danger",
                _ => "Brush.TextMuted",
            }
            : "Brush.TextMuted";

        return System.Windows.Application.Current?.TryFindResource(key) as Brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
