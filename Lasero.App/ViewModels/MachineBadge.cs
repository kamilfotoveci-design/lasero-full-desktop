using Lasero.App.Components;
using Lasero.App.Converters;
using Lasero.Core.Machines;

namespace Lasero.App.ViewModels;

/// <summary>What the status strip's machine badge says: one word and one semantic colour.</summary>
public sealed record MachineBadgeInfo(string Label, StatePillKind Kind);

/// <summary>
/// The status strip's machine badge. It used to say "Připojeno" in green the moment the serial port
/// opened, whatever the controller was doing, so an alarmed or not yet answering machine looked ready.
/// It now reports the resolved machine state (disconnected, connecting, ready, working, paused, door
/// open, alarm, error), so each state is distinct in words and in colour, and green appears only when
/// the controller itself has said Idle. A failed connection attempt is named as such instead of
/// falling back to a bare "Nepřipojeno", and the built-in simulator is never mistaken for hardware.
/// </summary>
public static class MachineBadge
{
    public const string ConnectionFailedLabel = "Připojení selhalo";
    public const string SimulatorSuffix = " (simulátor)";

    public static MachineBadgeInfo For(LaserMachineDisplayState state, string? connectionError, bool isSimulator)
    {
        if (state == LaserMachineDisplayState.Disconnected && !string.IsNullOrWhiteSpace(connectionError))
            return new MachineBadgeInfo(ConnectionFailedLabel, StatePillKind.Error);

        var label = MachineStateText.Label(state);
        var connected = state != LaserMachineDisplayState.Disconnected;
        if (isSimulator && connected) label += SimulatorSuffix;
        return new MachineBadgeInfo(label, MachineStateText.Kind(state));
    }
}
