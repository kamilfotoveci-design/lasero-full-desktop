using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.Core.Machines;

namespace Lasero.App.Converters;

/// <summary>
/// The machine's display state in Czech, as words. Presentation only, nothing here gates a command.
/// Kept as plain static functions so the status strip, the home rail and the inspector all read one
/// table and the wording is testable without WPF.
/// </summary>
public static class MachineStateText
{
    public static string Label(LaserMachineDisplayState state) => state switch
    {
        LaserMachineDisplayState.Disconnected => "Nepřipojeno",
        LaserMachineDisplayState.Connecting => "Připojování",
        LaserMachineDisplayState.Idle => "Připraveno",
        LaserMachineDisplayState.Run => "Pracuje",
        LaserMachineDisplayState.Hold => "Pozastaveno",
        LaserMachineDisplayState.Jog => "Ruční posun",
        LaserMachineDisplayState.Alarm => "Alarm",
        LaserMachineDisplayState.Door => "Otevřená dvířka",
        LaserMachineDisplayState.Check => "Kontrolní režim",
        LaserMachineDisplayState.Home => "Najíždí do výchozí polohy",
        LaserMachineDisplayState.Sleep => "Spánek",
        LaserMachineDisplayState.Error => "Chyba",
        _ => "Neznámý stav",
    };

    public static Components.StatePillKind Kind(LaserMachineDisplayState state) => state switch
    {
        LaserMachineDisplayState.Idle => Components.StatePillKind.Ready,
        LaserMachineDisplayState.Run or LaserMachineDisplayState.Jog or LaserMachineDisplayState.Home
            => Components.StatePillKind.Busy,
        LaserMachineDisplayState.Hold or LaserMachineDisplayState.Door or LaserMachineDisplayState.Check
            => Components.StatePillKind.Warning,
        LaserMachineDisplayState.Alarm or LaserMachineDisplayState.Error => Components.StatePillKind.Error,
        _ => Components.StatePillKind.Neutral,
    };
}

/// <summary>
/// The machine's display state in Czech. Presentation only — nothing here gates a command. Note that
/// Disconnected is "Nepřipojeno" and Idle is "Připraveno": Idle means the controller has answered and
/// reported that it is standing still, which is the only case where claiming readiness is honest.
/// </summary>
public sealed class MachineDisplayStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LaserMachineDisplayState state ? MachineStateText.Label(state) : "Nepřipojeno";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The same state as a <see cref="Components.StatePillKind"/>, so a StatusBadge can report the
/// machine without a second colour table. Follows the brush converter exactly: Ready only for Idle,
/// and anything unconfirmed stays Neutral rather than optimistic.</summary>
public sealed class MachineDisplayStateToStatePillKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LaserMachineDisplayState state ? MachineStateText.Kind(state) : Components.StatePillKind.Neutral;

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
                LaserMachineDisplayState.Run or LaserMachineDisplayState.Jog or LaserMachineDisplayState.Home => "Brush.Signal",
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
