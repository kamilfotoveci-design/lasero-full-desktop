using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Avalonia.Tests;

/// <summary>Minimal ILaserMachine test double, same shape as Lasero.Tests/DeviceScannerTests.cs's own
/// FakeMachine — tracks every command it was actually asked to send, so a test can assert "nothing was
/// sent" as directly as "something was sent", which matters for safety-gate regression tests.</summary>
public sealed class FakeLaserMachine : ILaserMachine
{
    public bool IsOpen { get; private set; }
    public List<string> SentCommands { get; } = new();
    public int SoftResetCount { get; private set; }
    public MachineStatus? StatusToReport { get; set; }
    public MachineAlert? AlertToReport { get; set; }

    public GrblConnectionState State => IsOpen ? GrblConnectionState.Connected : GrblConnectionState.Disconnected;
    public MachineStatus? LastStatus => StatusToReport;
    public DateTime? LastStatusReceivedUtc => StatusToReport is null ? null : DateTime.UtcNow;
    public string? FirmwareBanner => IsOpen ? "Grbl 1.1f ['$' for help]" : null;
    public MachineAlert? ActiveAlert => AlertToReport;
    public LaserMachineDisplayState DisplayState => IsOpen ? LaserMachineDisplayState.Idle : LaserMachineDisplayState.Disconnected;

    public event Action<GrblConnectionState>? ConnectionStateChanged;
    public event Action<MachineStatus>? StatusUpdated;
    public event Action<string>? RawLineReceived;
    public event Action<int, string>? ErrorReceived;
    public event Action<int, string>? AlarmReceived;
    public event Action<string>? FeedbackMessageReceived;
    public event Action<string>? Connected;
    public event Action<Exception?>? Disconnected;
    public event Action<MachineAlert?>? AlertChanged;

    public void Connect(string portName, int baudRate = 115200)
    {
        IsOpen = true;
        ConnectionStateChanged?.Invoke(GrblConnectionState.Connecting);
        ConnectionStateChanged?.Invoke(GrblConnectionState.Connected);
        Connected?.Invoke(FirmwareBanner!);
    }

    public void Disconnect()
    {
        IsOpen = false;
        ConnectionStateChanged?.Invoke(GrblConnectionState.Disconnected);
        Disconnected?.Invoke(null);
    }

    public void RaiseStatus(MachineStatus status)
    {
        StatusToReport = status;
        StatusUpdated?.Invoke(status);
    }

    public void RequestStatus() { }
    public void FeedHold() { }
    public void CycleStartResume() { }
    public void CancelJog() { }
    public void SoftReset() => SoftResetCount++;
    public void StartStatusPolling(TimeSpan interval) { }
    public void StopStatusPolling() { }
    public void DismissAlert() { }

    public Task<GrblCommandResult> SendCommandAsync(string line)
    {
        SentCommands.Add(line);
        return Task.FromResult(GrblCommandResult.Ok);
    }

    public Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true) =>
        SendCommandAsync($"jog {x},{y},{z}");
    public Task<GrblCommandResult> HomeAsync() => SendCommandAsync("$H");
    public Task<GrblCommandResult> UnlockAsync() => SendCommandAsync("$X");
    public Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin) => SendCommandAsync("origin");
    public Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber) => SendCommandAsync("wcs");
    public Task<IReadOnlyList<string>> QuerySettingsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public void Dispose() { }
}
