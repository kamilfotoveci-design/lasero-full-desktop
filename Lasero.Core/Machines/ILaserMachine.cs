using Lasero.Core.Grbl;

namespace Lasero.Core.Machines;

/// <summary>
/// The machine-agnostic seam ViewModels depend on instead of the concrete GrblConnection —
/// named generically (not IGrblConnection) so a future hardware-free SimulatedLaserMachine
/// can implement it with no ViewModel rework. Shape mirrors GrblConnection's current public
/// surface exactly; existing types (GrblCommandResult, MachineStatus, GrblConnectionState,
/// Position) are reused as-is rather than renamed.
/// </summary>
public interface ILaserMachine : IDisposable
{
    GrblConnectionState State { get; }
    MachineStatus? LastStatus { get; }
    DateTime? LastStatusReceivedUtc { get; }
    string? FirmwareBanner { get; }
    MachineAlert? ActiveAlert { get; }
    LaserMachineDisplayState DisplayState { get; }

    event Action<GrblConnectionState>? ConnectionStateChanged;
    event Action<MachineStatus>? StatusUpdated;
    event Action<string>? RawLineReceived;
    event Action<int, string>? ErrorReceived;
    event Action<int, string>? AlarmReceived;
    event Action<string>? FeedbackMessageReceived;
    event Action<string>? Connected;
    event Action<Exception?>? Disconnected;
    event Action<MachineAlert?>? AlertChanged;

    void Connect(string portName, int baudRate = 115200);
    void Disconnect();
    void RequestStatus();
    void FeedHold();
    void CycleStartResume();
    void CancelJog();
    void SoftReset();
    void StartStatusPolling(TimeSpan interval);
    void StopStatusPolling();
    void DismissAlert();

    Task<GrblCommandResult> SendCommandAsync(string line);
    Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true);
    Task<GrblCommandResult> HomeAsync();
    Task<GrblCommandResult> UnlockAsync();
    Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin);
    Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber);
    Task<IReadOnlyList<string>> QuerySettingsAsync();
}

/// <summary>
/// A single UI-facing state combining link state + controller mode + any active alarm/error —
/// computed on demand (see LaserMachineDisplayStateResolver), never stored or merged into
/// GrblConnectionState/GrblMachineMode, which keep modeling genuinely different concerns
/// ("can we talk to it" vs. "what is it doing").
/// </summary>
public enum LaserMachineDisplayState
{
    Disconnected,
    Connecting,
    Idle,
    Run,
    Hold,
    Jog,
    Alarm,
    Door,
    Check,
    Home,
    Sleep,
    Error,
}

/// <summary>Pure resolver so display-state logic is unit-testable without a live connection.</summary>
public static class LaserMachineDisplayStateResolver
{
    public static LaserMachineDisplayState Resolve(GrblConnectionState link, GrblMachineMode? mode, MachineAlert? activeAlert)
    {
        if (link == GrblConnectionState.Disconnected) return LaserMachineDisplayState.Disconnected;
        if (link == GrblConnectionState.Connecting) return LaserMachineDisplayState.Connecting;

        if (activeAlert is { Kind: MachineAlertKind.Alarm }) return LaserMachineDisplayState.Alarm;
        if (activeAlert is { Kind: MachineAlertKind.Error }) return LaserMachineDisplayState.Error;

        return mode switch
        {
            GrblMachineMode.Idle => LaserMachineDisplayState.Idle,
            GrblMachineMode.Run => LaserMachineDisplayState.Run,
            GrblMachineMode.Hold => LaserMachineDisplayState.Hold,
            GrblMachineMode.Jog => LaserMachineDisplayState.Jog,
            GrblMachineMode.Alarm => LaserMachineDisplayState.Alarm,
            GrblMachineMode.Door => LaserMachineDisplayState.Door,
            GrblMachineMode.Check => LaserMachineDisplayState.Check,
            GrblMachineMode.Home => LaserMachineDisplayState.Home,
            GrblMachineMode.Sleep => LaserMachineDisplayState.Sleep,
            _ => LaserMachineDisplayState.Connecting,
        };
    }
}
