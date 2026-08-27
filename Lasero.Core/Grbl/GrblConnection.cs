using System.Collections.Concurrent;
using System.Globalization;
using Lasero.Core.Machines;

namespace Lasero.Core.Grbl;

public enum GrblConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// Owns one reconnectable GRBL session. Line commands are serialized through a
/// per-connection queue while real-time commands bypass it. A missing response
/// closes the session because continuing would shift GRBL responses onto the
/// wrong commands and make machine state unsafe.
/// </summary>
public sealed class GrblConnection : ILaserMachine
{
    private readonly IGrblTransport _transport;
    private readonly IGrblProtocolParser _protocolParser;
    private readonly object _lifecycleLock = new();
    private readonly object _settingsLock = new();
    private readonly object _alertLock = new();
    private readonly object _commandDispatchLock = new();
    private readonly ConcurrentQueue<TaskCompletionSource<GrblCommandResult>> _pendingResponses = new();
    private readonly List<string> _settingsCollector = [];

    private BlockingCollection<QueuedCommand>? _queue;
    private CancellationTokenSource? _sessionCancellation;
    private Thread? _queueThread;
    private Timer? _statusPollTimer;
    private TaskCompletionSource<IReadOnlyList<string>>? _settingsQuery;
    private int _state = (int)GrblConnectionState.Disconnected;
    private int _disconnecting;
    private int _commandGeneration;
    private int _resetting;
    private int _settingsQueryCollecting;
    private MachineAlert? _activeAlert;

    public GrblConnection() : this(new GrblSerialTransport()) { }

    public GrblConnection(IGrblTransport transport, IGrblProtocolParser? protocolParser = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocolParser = protocolParser ?? new GrblProtocolParser();
    }

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public GrblConnectionState State => (GrblConnectionState)Volatile.Read(ref _state);
    public MachineStatus? LastStatus { get; private set; }
    public DateTime? LastStatusReceivedUtc { get; private set; }
    public string? FirmwareBanner { get; private set; }
    public MachineAlert? ActiveAlert => Volatile.Read(ref _activeAlert);
    public LaserMachineDisplayState DisplayState => LaserMachineDisplayStateResolver.Resolve(State, LastStatus?.Mode, ActiveAlert);

    public event Action<GrblConnectionState>? ConnectionStateChanged;
    public event Action<MachineStatus>? StatusUpdated;
    public event Action<string>? RawLineReceived;
    public event Action<int, string>? ErrorReceived;
    public event Action<int, string>? AlarmReceived;
    public event Action<string>? FeedbackMessageReceived;
    public event Action<string>? Connected;
    public event Action<Exception?>? Disconnected;
    public event Action<MachineAlert?>? AlertChanged;

    public static string[] GetAvailablePortNames() => GrblSerialTransport.GetAvailablePortNames();

    public void Connect(string portName, int baudRate = 115200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
        if (CommandTimeout <= TimeSpan.Zero) throw new InvalidOperationException("Časový limit příkazu musí být kladný.");

        lock (_lifecycleLock)
        {
            if (State != GrblConnectionState.Disconnected)
                throw new InvalidOperationException("Spojení už existuje — nejprve jej odpojte.");

            Interlocked.Exchange(ref _disconnecting, 0);
            SetState(GrblConnectionState.Connecting);
            try
            {
                FirmwareBanner = null;
                LastStatus = null;
                LastStatusReceivedUtc = null;
                lock (_alertLock) _activeAlert = null;
                _transport.LineReceived += OnLineReceived;
                _transport.UnexpectedlyClosed += OnUnexpectedlyClosed;
                _transport.Open(portName, baudRate);

                _queue = new BlockingCollection<QueuedCommand>();
                _sessionCancellation = new CancellationTokenSource();
                _queueThread = new Thread(() => QueueLoop(_queue, _sessionCancellation.Token))
                {
                    IsBackground = true,
                    Name = "Grbl-CommandQueue",
                };

                SetState(GrblConnectionState.Connected);
                _queueThread.Start();
            }
            catch
            {
                CleanupTransportSubscriptions();
                try { _transport.Close(); } catch { }
                _queue?.Dispose();
                _queue = null;
                _sessionCancellation?.Dispose();
                _sessionCancellation = null;
                _queueThread = null;
                SetState(GrblConnectionState.Disconnected);
                throw;
            }
        }
    }

    public void Disconnect() => DisconnectCore(null);

    public void DismissAlert() => ClearAlert();

    public void RequestStatus() => SafeWriteRealtime(GrblRealtimeCommand.StatusReportQuery);
    public void FeedHold() => SafeWriteRealtime(GrblRealtimeCommand.FeedHold);
    public void CycleStartResume() => SafeWriteRealtime(GrblRealtimeCommand.CycleStartResume);
    public void CancelJog() => SafeWriteRealtime(GrblRealtimeCommand.JogCancel);
    public void SoftReset()
    {
        if (State != GrblConnectionState.Connected) return;

        Exception? writeFailure = null;
        Interlocked.Exchange(ref _resetting, 1);
        try
        {
            lock (_commandDispatchLock)
            {
                Interlocked.Increment(ref _commandGeneration);
                FailAllPending("Zařízení bylo resetováno.");
                FailQueued(_queue, "Příkaz byl zrušen resetem zařízení.");
                FailSettingsQuery("Dotaz byl zrušen resetem zařízení.");
                try
                {
                    _transport.WriteRealtimeByte(GrblRealtimeCommand.SoftReset);
                }
                catch (Exception ex)
                {
                    writeFailure = ex;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _resetting, 0);
        }

        if (writeFailure is not null)
            DisconnectCore(writeFailure);
    }

    public void StartStatusPolling(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        StopStatusPolling();
        _statusPollTimer = new Timer(_ => RequestStatus(), null, TimeSpan.Zero, interval);
    }

    public void StopStatusPolling()
    {
        _statusPollTimer?.Dispose();
        _statusPollTimer = null;
    }

    public Task<GrblCommandResult> SendCommandAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Task.FromResult(GrblCommandResult.Failure("Prázdný příkaz nebyl odeslán."));
        if (State != GrblConnectionState.Connected)
            return Task.FromResult(GrblCommandResult.Failure("Zařízení není připojeno."));

        var queue = _queue;
        if (queue is null || queue.IsAddingCompleted)
            return Task.FromResult(GrblCommandResult.Failure("Spojení se právě ukončuje."));

        var generation = Volatile.Read(ref _commandGeneration);
        if (Volatile.Read(ref _resetting) != 0)
            return Task.FromResult(GrblCommandResult.Failure("Zařízení se právě resetuje."));

        var completion = new TaskCompletionSource<GrblCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Add(new QueuedCommand(line, completion, generation));
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(GrblCommandResult.Failure("Spojení bylo ukončeno."));
        }
        return completion.Task;
    }

    public Task<GrblCommandResult> JogAsync(double x, double y, double z, double feedRatePerMinute, bool relative = true)
    {
        var mode = relative ? "G91" : "G90";
        return SendCommandAsync($"$J={mode}{FormatAxes(x, y, z)}F{feedRatePerMinute.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    public Task<GrblCommandResult> HomeAsync() => SendCommandAsync("$H");
    public Task<GrblCommandResult> UnlockAsync() => SendCommandAsync("$X");

    public Task<GrblCommandResult> SetWorkOriginAsync(int wcsNumber, Position origin)
    {
        if (wcsNumber is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(wcsNumber));
        return SendCommandAsync($"G10 L20 P{wcsNumber}{FormatAxes(origin.X, origin.Y, origin.Z)}");
    }

    public Task<GrblCommandResult> SelectWorkCoordinateSystemAsync(int wcsNumber)
    {
        var gcode = wcsNumber switch
        {
            1 => "G54", 2 => "G55", 3 => "G56", 4 => "G57", 5 => "G58", 6 => "G59",
            _ => throw new ArgumentOutOfRangeException(nameof(wcsNumber), "Očekávané hodnoty jsou 1–6 (G54–G59)."),
        };
        return SendCommandAsync(gcode);
    }

    public async Task<IReadOnlyList<string>> QuerySettingsAsync()
    {
        TaskCompletionSource<IReadOnlyList<string>> query;
        lock (_settingsLock)
        {
            if (_settingsQuery is not null)
                throw new InvalidOperationException("Dotaz na nastavení již probíhá.");
            _settingsCollector.Clear();
            query = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _settingsQuery = query;
        }

        try
        {
            var result = await SendCommandAsync("$$").ConfigureAwait(false);
            if (!result.IsOk)
                throw new IOException(result.Message ?? "Nastavení zařízení se nepodařilo načíst.");
            return await query.Task.WaitAsync(CommandTimeout).ConfigureAwait(false);
        }
        finally
        {
            lock (_settingsLock)
            {
                if (ReferenceEquals(_settingsQuery, query))
                    _settingsQuery = null;
                _settingsCollector.Clear();
                Volatile.Write(ref _settingsQueryCollecting, 0);
            }
        }
    }

    private void QueueLoop(BlockingCollection<QueuedCommand> queue, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var command in queue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    lock (_commandDispatchLock)
                    {
                        if (command.Generation != Volatile.Read(ref _commandGeneration))
                        {
                            command.Completion.TrySetResult(GrblCommandResult.Failure("Příkaz byl zrušen resetem zařízení."));
                            continue;
                        }

                        if (command.Text == "$$")
                        {
                            lock (_settingsLock)
                            {
                                _settingsCollector.Clear();
                                Volatile.Write(ref _settingsQueryCollecting, 1);
                            }
                        }
                        _pendingResponses.Enqueue(command.Completion);
                        _transport.WriteLine(command.Text);
                    }
                    command.Completion.Task.WaitAsync(CommandTimeout, cancellationToken).GetAwaiter().GetResult();
                }
                catch (TimeoutException ex)
                {
                    RemoveOldestPending(command.Completion);
                    command.Completion.TrySetResult(GrblCommandResult.Failure("Zařízení neodpovědělo v časovém limitu."));
                    DisconnectCore(ex, joinQueueThread: false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    command.Completion.TrySetResult(GrblCommandResult.Failure("Příkaz byl zrušen při odpojení."));
                    return;
                }
                catch (Exception ex)
                {
                    RemoveOldestPending(command.Completion);
                    command.Completion.TrySetResult(GrblCommandResult.Failure($"Příkaz se nepodařilo odeslat: {ex.Message}"));
                    DisconnectCore(ex, joinQueueThread: false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect path.
        }
    }

    private void OnLineReceived(string line)
    {
        InvokeSafely(RawLineReceived, line, nameof(RawLineReceived));

        if (line.StartsWith('<') && line.EndsWith('>'))
        {
            if (_protocolParser.TryParseStatus(line, out var status))
            {
                LastStatus = status;
                LastStatusReceivedUtc = DateTime.UtcNow;
                if (status.Mode != GrblMachineMode.Alarm)
                    ClearAlertOfKind(MachineAlertKind.Alarm);
                InvokeSafely(StatusUpdated, status, nameof(StatusUpdated));
            }
            return;
        }

        if (line.StartsWith("Grbl ", StringComparison.Ordinal))
        {
            // A banner while connected means the controller restarted. Invalidate every command
            // accepted under the old parser/planner state so no stale line can run after reset.
            lock (_commandDispatchLock)
            {
                Interlocked.Increment(ref _commandGeneration);
                FailAllPending("Řadič byl restartován.");
                FailQueued(_queue, "Příkaz byl zrušen restartem řadiče.");
                FailSettingsQuery("Dotaz byl zrušen restartem řadiče.");
            }
            FirmwareBanner = line;
            InvokeSafely(Connected, line, nameof(Connected));
            return;
        }

        if (line == "ok")
        {
            lock (_settingsLock)
            {
                if (Volatile.Read(ref _settingsQueryCollecting) != 0)
                {
                    _settingsQuery?.TrySetResult(_settingsCollector.ToArray());
                    _settingsQuery = null;
                    _settingsCollector.Clear();
                    Volatile.Write(ref _settingsQueryCollecting, 0);
                }
            }
            ClearAlertOfKind(MachineAlertKind.Error);
            CompleteOldestPending(GrblCommandResult.Ok);
            return;
        }

        if (line.StartsWith("error:", StringComparison.Ordinal))
        {
            var code = ParseTrailingInt(line);
            var message = _protocolParser.DescribeError(code);
            CompleteOldestPending(GrblCommandResult.Error(code));
            SetAlert(MachineAlertKind.Error, code, message);
            InvokeSafely(ErrorReceived, code, message, nameof(ErrorReceived));
            return;
        }

        if (line.StartsWith("ALARM:", StringComparison.Ordinal))
        {
            var code = ParseTrailingInt(line);
            var message = _protocolParser.DescribeAlarm(code);
            CompleteOldestPending(GrblCommandResult.Alarm(code));
            SetAlert(MachineAlertKind.Alarm, code, message);
            InvokeSafely(AlarmReceived, code, message, nameof(AlarmReceived));
            return;
        }

        if (line.StartsWith('$') && line.Contains('='))
        {
            lock (_settingsLock)
            {
                if (Volatile.Read(ref _settingsQueryCollecting) != 0)
                    _settingsCollector.Add(line);
            }
            return;
        }

        if (line.StartsWith('[') && line.EndsWith(']'))
            InvokeSafely(FeedbackMessageReceived, line, nameof(FeedbackMessageReceived));
    }

    private void SafeWriteRealtime(byte value)
    {
        if (State != GrblConnectionState.Connected) return;
        try
        {
            _transport.WriteRealtimeByte(value);
        }
        catch (Exception ex)
        {
            DisconnectCore(ex);
        }
    }

    private void DisconnectCore(Exception? reason, bool joinQueueThread = true)
    {
        if (Interlocked.Exchange(ref _disconnecting, 1) != 0) return;
        try
        {
            StopStatusPolling();

            var cancellation = _sessionCancellation;
            var queue = _queue;
            cancellation?.Cancel();
            if (queue is not null && !queue.IsAddingCompleted)
                queue.CompleteAdding();

            FailAllPending(reason is TimeoutException
                ? "Zařízení neodpovědělo v časovém limitu."
                : "Spojení bylo ukončeno.");
            FailQueued(queue, "Spojení bylo ukončeno.");
            FailSettingsQuery("Spojení bylo ukončeno.");

            CleanupTransportSubscriptions();
            try { _transport.Close(); } catch { }

            var queueThread = _queueThread;
            if (joinQueueThread && queueThread is not null && queueThread != Thread.CurrentThread)
                queueThread.Join(TimeSpan.FromSeconds(2));

            lock (_lifecycleLock)
            {
                _queueThread = null;
                _queue = null;
                _sessionCancellation = null;
            }
            queue?.Dispose();
            cancellation?.Dispose();
            InvokeSafely(Disconnected, reason, nameof(Disconnected));
            SetState(GrblConnectionState.Disconnected);
        }
        finally
        {
            Interlocked.Exchange(ref _disconnecting, 0);
        }
    }

    private void OnUnexpectedlyClosed(Exception exception) => DisconnectCore(exception, joinQueueThread: false);

    private void CleanupTransportSubscriptions()
    {
        _transport.LineReceived -= OnLineReceived;
        _transport.UnexpectedlyClosed -= OnUnexpectedlyClosed;
    }

    private void CompleteOldestPending(GrblCommandResult result)
    {
        if (_pendingResponses.TryDequeue(out var completion))
            completion.TrySetResult(result);
    }

    private void RemoveOldestPending(TaskCompletionSource<GrblCommandResult> expected)
    {
        if (_pendingResponses.TryDequeue(out var completion) && !ReferenceEquals(completion, expected))
            completion.TrySetResult(GrblCommandResult.Failure("Došlo k nesouladu odpovědí zařízení."));
    }

    private void FailAllPending(string message)
    {
        while (_pendingResponses.TryDequeue(out var completion))
            completion.TrySetResult(GrblCommandResult.Failure(message));
    }

    private static void FailQueued(BlockingCollection<QueuedCommand>? queue, string message)
    {
        if (queue is null) return;
        while (queue.TryTake(out var command))
            command.Completion.TrySetResult(GrblCommandResult.Failure(message));
    }

    private void FailSettingsQuery(string message)
    {
        lock (_settingsLock)
        {
            _settingsQuery?.TrySetException(new IOException(message));
            _settingsQuery = null;
            _settingsCollector.Clear();
            Volatile.Write(ref _settingsQueryCollecting, 0);
        }
    }

    private void SetAlert(MachineAlertKind kind, int code, string message)
    {
        var alert = new MachineAlert { Kind = kind, Code = code, Message = message, OccurredUtc = DateTime.UtcNow };
        lock (_alertLock) _activeAlert = alert;
        InvokeSafely(AlertChanged, alert, nameof(AlertChanged));
    }

    private void ClearAlertOfKind(MachineAlertKind kind)
    {
        lock (_alertLock)
        {
            if (_activeAlert is null || _activeAlert.Kind != kind) return;
            _activeAlert = null;
        }
        InvokeSafely(AlertChanged, null, nameof(AlertChanged));
    }

    private void ClearAlert()
    {
        lock (_alertLock)
        {
            if (_activeAlert is null) return;
            _activeAlert = null;
        }
        InvokeSafely(AlertChanged, null, nameof(AlertChanged));
    }

    private void SetState(GrblConnectionState state)
    {
        var previous = (GrblConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous != state)
            InvokeSafely(ConnectionStateChanged, state, nameof(ConnectionStateChanged));
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value, string eventName)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("GRBL event subscriber {0} failed: {1}", eventName, ex);
            }
        }
    }

    private static void InvokeSafely<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second, string eventName)
    {
        if (handlers is null) return;
        foreach (Action<T1, T2> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(first, second);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("GRBL event subscriber {0} failed: {1}", eventName, ex);
            }
        }
    }

    private static string FormatAxes(double x, double y, double z) =>
        $" X{x.ToString("0.###", CultureInfo.InvariantCulture)}" +
        $" Y{y.ToString("0.###", CultureInfo.InvariantCulture)}" +
        $" Z{z.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static int ParseTrailingInt(string line)
    {
        var colon = line.IndexOf(':');
        var digits = colon >= 0 ? line[(colon + 1)..] : line;
        return int.TryParse(digits, out var number) ? number : -1;
    }

    public void Dispose()
    {
        Disconnect();
        _transport.Dispose();
    }

    private sealed record QueuedCommand(
        string Text,
        TaskCompletionSource<GrblCommandResult> Completion,
        int Generation);
}
