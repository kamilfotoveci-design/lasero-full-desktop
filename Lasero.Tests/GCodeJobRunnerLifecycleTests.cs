using System.Collections.Concurrent;
using System.IO;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

public sealed class GCodeJobRunnerLifecycleTests
{
    [Fact]
    public async Task PauseStopsSendingAdditionalLinesUntilResume()
    {
        var transport = new ControlledTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(3) };
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(["G1 X1", "G1 X2"]);
        Assert.True(transport.WaitForLineCount(1));

        runner.Pause();
        transport.RespondOk();
        await Task.Delay(100);
        Assert.Single(transport.WrittenLines);
        Assert.Equal(JobRunState.Paused, runner.State);

        runner.Resume();
        Assert.True(transport.WaitForLineCount(2));
        transport.RespondOk();
        Assert.True(transport.WaitForLineCount(3)); // safety M5 epilogue
        transport.RespondOk();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(JobRunState.Completed, runner.State);
    }

    [Fact]
    public async Task AbortWhilePausedDoesNotSendNextLine()
    {
        var transport = new ControlledTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(3) };
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(["G1 X1", "G1 X2"]);
        Assert.True(transport.WaitForLineCount(1));
        runner.Pause();
        runner.Abort();

        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(transport.WrittenLines);
        Assert.Contains(GrblRealtimeCommand.FeedHold, transport.RealtimeWrites);
        Assert.Contains(GrblRealtimeCommand.SoftReset, transport.RealtimeWrites);
        Assert.Equal(JobRunState.Aborted, runner.State);
    }

    [Fact]
    public async Task BlankLinesStillAdvanceProgress()
    {
        var transport = new ControlledTransport { AutoRespond = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var progress = new List<(int Current, int Total)>();
        runner.ProgressChanged += (current, total) => progress.Add((current, total));

        await runner.RunAsync(["", "G1 X1"]);

        Assert.Equal([(1, 2), (2, 2)], progress);
        Assert.Equal(JobRunState.Completed, runner.State);
    }

    [Fact]
    public async Task DisconnectDuringJobFaultsWithoutSendingTheNextLineOrAutoResuming()
    {
        var transport = new ControlledTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(3) };
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(["G1 X1", "G1 X2"]);
        Assert.True(transport.WaitForLineCount(1));

        transport.RaiseUnexpectedDisconnect(new IOException("Kabel byl odpojen."));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(JobRunState.Faulted, runner.State);
        Assert.Single(transport.WrittenLines);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task CompletionWaitsForFreshIdleStatusAfterAllLinesAreAccepted()
    {
        var transport = new ControlledTransport { AutoReportIdle = false };
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(3) };
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(["G1 X1"]);

        Assert.True(transport.WaitForLineCount(1));
        transport.RespondOk();
        Assert.True(transport.WaitForLineCount(2)); // safety M5 epilogue
        transport.RespondOk();
        await Task.Delay(100);

        Assert.False(run.IsCompleted);
        Assert.Equal(JobRunState.Running, runner.State);

        transport.RespondStatus(GrblMachineMode.Run);
        await Task.Delay(50);
        Assert.False(run.IsCompleted);

        transport.RespondStatus(GrblMachineMode.Idle);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JobRunState.Completed, runner.State);
    }

    private sealed class ControlledTransport : IGrblTransport
    {
        private readonly ConcurrentQueue<string> _written = new();
        private readonly AutoResetEvent _lineWritten = new(false);

        public bool IsOpen { get; private set; }
        public bool AutoRespond { get; set; }
        public bool AutoReportIdle { get; set; } = true;
        public IReadOnlyCollection<string> WrittenLines => _written.ToArray();
        public ConcurrentQueue<byte> RealtimeWrites { get; } = new();

        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed;
        public void RaiseUnexpectedDisconnect(Exception exception) => UnexpectedlyClosed?.Invoke(exception);

        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;

        public void WriteLine(string text)
        {
            if (!IsOpen) throw new IOException("Port není otevřený.");
            _written.Enqueue(text);
            _lineWritten.Set();
            if (AutoRespond) RespondOk();
        }

        public void WriteRealtimeByte(byte value)
        {
            RealtimeWrites.Enqueue(value);
            if (value == GrblRealtimeCommand.StatusReportQuery && AutoReportIdle)
                RespondStatus(GrblMachineMode.Idle);
        }
        public void RespondOk() => LineReceived?.Invoke("ok");
        public void RespondStatus(GrblMachineMode mode)
        {
            var label = mode switch
            {
                GrblMachineMode.Run => "Run",
                GrblMachineMode.Hold => "Hold:0",
                GrblMachineMode.Alarm => "Alarm",
                _ => "Idle",
            };
            LineReceived?.Invoke($"<{label}|MPos:0.000,0.000,0.000|WPos:0.000,0.000,0.000|FS:0,0>");
        }

        public bool WaitForLineCount(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (_written.Count < expected && DateTime.UtcNow < deadline)
                _lineWritten.WaitOne(TimeSpan.FromMilliseconds(50));
            return _written.Count >= expected;
        }

        public void Dispose()
        {
            Close();
            _lineWritten.Dispose();
        }
    }
}
