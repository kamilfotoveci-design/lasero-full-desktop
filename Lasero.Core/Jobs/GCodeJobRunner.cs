using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Core.Jobs;

/// <summary>
/// Job streamer with a real queue pause: feed-hold stops physical motion and the runner also
/// stops sending subsequent lines until Resume. Depends on ILaserMachine rather than GrblConnection
/// directly so it can run against a future simulated machine unchanged.
/// </summary>
public sealed class GCodeJobRunner
{
    private readonly ILaserMachine _connection;
    private volatile bool _abortRequested;
    private TaskCompletionSource<bool>? _resumeSignal;
    private TaskCompletionSource<bool>? _terminationSignal;

    public JobRunState State { get; private set; } = JobRunState.Idle;
    public int CurrentLine { get; private set; }
    public int TotalLines { get; private set; }

    public event Action<JobRunState>? StateChanged;
    public event Action<int, int>? ProgressChanged;
    public event Action<int, string>? LineFailed;

    public GCodeJobRunner(ILaserMachine connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task RunAsync(IReadOnlyList<string> lines, bool abortOnError = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (State is JobRunState.Running or JobRunState.Paused)
            throw new InvalidOperationException("Úloha již běží.");

        _abortRequested = false;
        ReleasePause();
        var terminationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _terminationSignal, terminationSignal)?.TrySetResult(true);
        TotalLines = lines.Count;
        CurrentLine = 0;
        SetState(JobRunState.Running);

        void OnDisconnected(Exception? reason)
        {
            _abortRequested = true;
            ReleasePause();
            terminationSignal.TrySetResult(true);
            LineFailed?.Invoke(Math.Max(CurrentLine, 0), reason?.Message ?? "Spojení se zařízením bylo přerušeno.");
            SetState(JobRunState.Faulted);
        }

        _connection.Disconnected += OnDisconnected;

        try
        {
            for (var index = 0; index < lines.Count; index++)
            {
                await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                if (_abortRequested || cancellationToken.IsCancellationRequested)
                {
                    if (State != JobRunState.Faulted) SetState(JobRunState.Aborted);
                    return;
                }

                var line = lines[index];
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var result = await _connection.SendCommandAsync(line).ConfigureAwait(false);
                    if (_abortRequested)
                    {
                        if (State != JobRunState.Faulted) SetState(JobRunState.Aborted);
                        return;
                    }

                    if (!result.IsOk)
                    {
                        LineFailed?.Invoke(index, result.Message ?? "Neznámá chyba zařízení.");
                        if (result.AlarmCode is not null || abortOnError)
                        {
                            StopMachineAndClearPlanner();
                            SetState(JobRunState.Faulted);
                            return;
                        }
                    }
                }

                CurrentLine = index + 1;
                ProgressChanged?.Invoke(CurrentLine, TotalLines);
            }

            if (_abortRequested)
            {
                if (State != JobRunState.Faulted) SetState(JobRunState.Aborted);
                return;
            }

            // A project file is not allowed to leave the spindle/laser enabled after its last line.
            // This line is intentionally outside user progress because it is a safety epilogue.
            var laserOff = await _connection.SendCommandAsync("M5").ConfigureAwait(false);
            if (!laserOff.IsOk)
            {
                LineFailed?.Invoke(Math.Max(CurrentLine - 1, 0), laserOff.Message ?? "Laser se nepodařilo vypnout.");
                StopMachineAndClearPlanner();
                SetState(JobRunState.Faulted);
                return;
            }

            if (!await WaitForConfirmedIdleAsync(terminationSignal.Task, cancellationToken).ConfigureAwait(false))
            {
                if (State is not (JobRunState.Faulted or JobRunState.Aborted))
                    SetState(JobRunState.Aborted);
                return;
            }

            if (State != JobRunState.Faulted)
                SetState(_abortRequested ? JobRunState.Aborted : JobRunState.Completed);
        }
        catch (OperationCanceledException)
        {
            _abortRequested = true;
            StopMachineAndClearPlanner();
            SetState(JobRunState.Aborted);
        }
        catch (Exception ex)
        {
            StopMachineAndClearPlanner();
            LineFailed?.Invoke(Math.Max(CurrentLine, 0), ex.Message);
            SetState(JobRunState.Faulted);
        }
        finally
        {
            _connection.Disconnected -= OnDisconnected;
            ReleasePause();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _terminationSignal, null, terminationSignal), terminationSignal))
                terminationSignal.TrySetResult(true);
        }
    }

    public void Pause()
    {
        if (State != JobRunState.Running) return;
        Interlocked.CompareExchange(
            ref _resumeSignal,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
        _connection.FeedHold();
        SetState(JobRunState.Paused);
    }

    public void Resume()
    {
        if (State != JobRunState.Paused) return;
        _connection.CycleStartResume();
        ReleasePause();
        SetState(JobRunState.Running);
    }

    public void Abort()
    {
        if (State is not (JobRunState.Running or JobRunState.Paused)) return;
        _abortRequested = true;
        StopMachineAndClearPlanner();
        ReleasePause();
        Volatile.Read(ref _terminationSignal)?.TrySetResult(true);
        SetState(JobRunState.Aborted);
    }

    private async Task<bool> WaitForConfirmedIdleAsync(Task terminationTask, CancellationToken cancellationToken)
    {
        var statusSignal = new TaskCompletionSource<GrblMachineMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(MachineStatus status)
        {
            if (status.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm)
                statusSignal.TrySetResult(status.Mode);
        }

        _connection.StatusUpdated += OnStatus;
        try
        {
            while (!_abortRequested)
            {
                await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                if (_abortRequested) return false;

                _connection.RequestStatus();
                var pollDelay = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                var completed = await Task.WhenAny(statusSignal.Task, terminationTask, pollDelay).ConfigureAwait(false);
                if (completed == terminationTask) return false;
                if (completed != statusSignal.Task) continue;

                var mode = await statusSignal.Task.ConfigureAwait(false);
                if (mode == GrblMachineMode.Idle) return true;

                LineFailed?.Invoke(Math.Max(CurrentLine - 1, 0), "Zařízení přešlo během dokončování úlohy do alarmu.");
                SetState(JobRunState.Faulted);
                return false;
            }

            return false;
        }
        finally
        {
            _connection.StatusUpdated -= OnStatus;
        }
    }

    private void StopMachineAndClearPlanner()
    {
        _connection.FeedHold();
        _connection.SoftReset();
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        var signal = Volatile.Read(ref _resumeSignal);
        if (signal is not null)
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReleasePause() => Interlocked.Exchange(ref _resumeSignal, null)?.TrySetResult(true);

    private void SetState(JobRunState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
