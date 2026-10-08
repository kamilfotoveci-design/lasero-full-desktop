using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

/// <summary>A machine with no profile and no link, for view-model tests that must not touch a port.</summary>
internal sealed class AsyncJobMachineBase : ILaserMachine
{
    public GrblConnectionState State => GrblConnectionState.Disconnected;
    public MachineStatus? LastStatus => null;
    public DateTime? LastStatusReceivedUtc => null;
    public string? FirmwareBanner => null;
    public MachineAlert? ActiveAlert => null;
    public LaserMachineDisplayState DisplayState => LaserMachineDisplayState.Disconnected;

    public event Action<GrblConnectionState>? ConnectionStateChanged { add { } remove { } }
    public event Action<MachineStatus>? StatusUpdated { add { } remove { } }
    public event Action<string>? RawLineReceived { add { } remove { } }
    public event Action<int, string>? ErrorReceived { add { } remove { } }
    public event Action<int, string>? AlarmReceived { add { } remove { } }
    public event Action<string>? FeedbackMessageReceived { add { } remove { } }
    public event Action<string>? Connected { add { } remove { } }
    public event Action<Exception?>? Disconnected { add { } remove { } }
    public event Action<MachineAlert?>? AlertChanged { add { } remove { } }

    public void Connect(string portName, int baudRate = 115200) => throw new NotSupportedException();
    public void Disconnect() { }
    public void RequestStatus() { }
    public void FeedHold() { }
    public void CycleStartResume() { }
    public void CancelJog() { }
    public void SoftReset() { }
    public void StartStatusPolling(TimeSpan interval) { }
    public void StopStatusPolling() { }
    public void DismissAlert() { }
    public Task<GrblCommandResult> SendCommandAsync(string line) => throw new NotSupportedException();
    public Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true) => throw new NotSupportedException();
    public Task<GrblCommandResult> HomeAsync() => throw new NotSupportedException();
    public Task<GrblCommandResult> UnlockAsync() => throw new NotSupportedException();
    public Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin) => throw new NotSupportedException();
    public Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> QuerySettingsAsync() => throw new NotSupportedException();
    public void Dispose() { }
}
