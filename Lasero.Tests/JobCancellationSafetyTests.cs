using System.Collections.Concurrent;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

public sealed class JobCancellationSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileAwaitingAcknowledgementAttemptsStopPromptly(bool epilogue)
    {
        using var transport = new CancellationTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(10) };
        connection.Connect("COM1");
        using var cancellation = new CancellationTokenSource();
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(epilogue ? [] : ["G1 X1", "G1 X2"], cancellationToken: cancellation.Token);
        await transport.FirstLine.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        AssertStopAttempted(transport, runner);
        Assert.Equal(epilogue ? "M5" : "G1 X1", Assert.Single(transport.Lines));
    }

    [Fact]
    public async Task CancellationBetweenLinesStopsBeforeNextLine()
    {
        using var transport = new CancellationTransport { AutoRespond = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        using var cancellation = new CancellationTokenSource();
        var runner = new GCodeJobRunner(connection);
        runner.ProgressChanged += (_, _) => cancellation.Cancel();

        await runner.RunAsync(["G1 X1", "G1 X2"], cancellationToken: cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));

        AssertStopAttempted(transport, runner);
        Assert.Equal("G1 X1", Assert.Single(transport.Lines));
    }

    [Fact]
    public async Task CancellationWaitingForIdleTerminatesAndAttemptsStop()
    {
        using var transport = new CancellationTransport { AutoRespond = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        using var cancellation = new CancellationTokenSource();
        var runner = new GCodeJobRunner(connection);
        var run = runner.RunAsync(["G1 X1"], cancellationToken: cancellation.Token);
        await transport.StatusRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        AssertStopAttempted(transport, runner);
    }

    [Fact]
    public async Task InputMutationCannotReplaceQueuedJobLines()
    {
        using var transport = new CancellationTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection);
        var lines = new List<string> { "G1 X1", "G1 X2" };
        var run = runner.RunAsync(lines);
        await transport.FirstLine.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lines[1] = "M3 S1000";
        lines.Add("G1 X999");
        transport.AutoRespond = true;
        transport.AutoIdle = true;
        transport.RespondOk();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "G1 X1", "G1 X2", "M5" }, transport.Lines.ToArray());
        Assert.Equal(2, runner.TotalLines);
        Assert.Equal(JobRunState.Completed, runner.State);
    }

    [Fact]
    public async Task NewRunIsRejectedUntilPriorRunHasFinishedCleanup()
    {
        using var transport = new CancellationTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        using var cancellation = new CancellationTokenSource();
        var runner = new GCodeJobRunner(connection);
        Task? overlappingRun = null;
        runner.StateChanged += state =>
        {
            if (state == JobRunState.Aborted)
                overlappingRun = runner.RunAsync(["G1 X999"]);
        };
        var run = runner.RunAsync(["G1 X1"], cancellationToken: cancellation.Token);
        await transport.FirstLine.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(overlappingRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => overlappingRun!);
        Assert.Equal("G1 X1", Assert.Single(transport.Lines));
    }

    [Fact]
    public async Task MissingFinalStatusFaultsWithinSilenceTimeoutAndAttemptsStop()
    {
        using var transport = new CancellationTransport { AutoRespond = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection) { StatusSilenceTimeout = TimeSpan.FromMilliseconds(150) };
        string? failure = null;
        runner.LineFailed += (_, message) => failure = message;

        await runner.RunAsync(["G1 X1"]).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(JobRunState.Faulted, runner.State);
        Assert.Contains("neodpovídá", failure);
        Assert.Contains(GrblRealtimeCommand.FeedHold, transport.Realtime);
        Assert.Contains(GrblRealtimeCommand.SoftReset, transport.Realtime);
    }

    [Fact]
    public async Task RepeatedBusyStatusesRenewSilenceDeadlineUntilIdle()
    {
        using var transport = new CancellationTransport { AutoRespond = true, AutoBusy = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        var runner = new GCodeJobRunner(connection) { StatusSilenceTimeout = TimeSpan.FromMilliseconds(800) };
        var run = runner.RunAsync(["G1 X1"]);
        // Five responses span roughly one second at the 250 ms polling interval, beyond the
        // 800 ms timeout with enough headroom for normal thread-pool scheduling.
        // Alternating Run and Hold must both keep the controller alive.
        await transport.FiveStatuses.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);
        transport.AutoIdle = true;
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(JobRunState.Completed, runner.State);
        Assert.DoesNotContain(GrblRealtimeCommand.SoftReset, transport.Realtime);
    }

    [Fact]
    public async Task NonPositiveSilenceTimeoutIsRejectedBeforeSending()
    {
        using var transport = new CancellationTransport();
        using var connection = new GrblConnection(transport);
        var runner = new GCodeJobRunner(connection) { StatusSilenceTimeout = TimeSpan.Zero };
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(["G1 X1"]));
        Assert.Empty(transport.Lines);
    }

    [Fact]
    public async Task FinalDrainPauseKeepsPollingAndRequiresFreshIdleAfterResume()
    {
        using var transport = new CancellationTransport { AutoRespond = true, AutoBusy = true, OnlyHold = true };
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        // This test checks pause/final-drain ordering, not the silence deadline. Leave scheduling
        // headroom when the release packager compresses large native dependencies concurrently.
        var runner = new GCodeJobRunner(connection) { StatusSilenceTimeout = TimeSpan.FromSeconds(3) };
        transport.BeforeStatusResponse = runner.Pause;
        var run = runner.RunAsync(["G1 X1"]);
        await transport.FiveStatuses.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(JobRunState.Paused, runner.State);
        Assert.False(run.IsCompleted);

        transport.AutoIdle = true;
        await transport.IdleReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        transport.AutoIdle = false;
        transport.BeforeStatusResponse = null;
        runner.Resume();
        await Task.Delay(300);
        Assert.False(run.IsCompleted); // Idle received during pause cannot complete after resume.
        transport.AutoIdle = true;
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JobRunState.Completed, runner.State);
        Assert.DoesNotContain(GrblRealtimeCommand.SoftReset, transport.Realtime);
    }

    private static void AssertStopAttempted(CancellationTransport transport, GCodeJobRunner runner)
    {
        Assert.Equal(JobRunState.Aborted, runner.State);
        Assert.Contains(GrblRealtimeCommand.FeedHold, transport.Realtime);
        Assert.Contains(GrblRealtimeCommand.SoftReset, transport.Realtime);
        // Written stop bytes are an attempt; they do not prove physical laser shutdown.
    }

    private sealed class CancellationTransport : IGrblTransport
    {
        public bool IsOpen { get; private set; }
        public volatile bool AutoRespond;
        public volatile bool AutoIdle;
        public volatile bool AutoBusy;
        public bool OnlyHold;
        public Action? BeforeStatusResponse;
        public TaskCompletionSource<bool> IdleReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _statusCount;
        public TaskCompletionSource<bool> FiveStatuses { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Lines { get; } = new();
        public ConcurrentQueue<byte> Realtime { get; } = new();
        public TaskCompletionSource<bool> FirstLine { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> StatusRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed { add { } remove { } }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public void RespondOk() => LineReceived?.Invoke("ok");
        public void WriteLine(string text)
        {
            Lines.Enqueue(text);
            FirstLine.TrySetResult(true);
            if (AutoRespond) RespondOk();
        }
        public void WriteRealtimeByte(byte value)
        {
            Realtime.Enqueue(value);
            if (value != GrblRealtimeCommand.StatusReportQuery) return;
            StatusRequested.TrySetResult(true);
            BeforeStatusResponse?.Invoke();
            if (AutoIdle)
            {
                LineReceived?.Invoke("<Idle|MPos:0,0,0|WPos:0,0,0|FS:0,0>");
                IdleReported.TrySetResult(true);
            }
            else if (AutoBusy)
            {
                var count = Interlocked.Increment(ref _statusCount);
                var mode = OnlyHold || count % 2 == 0 ? "Hold:0" : "Run";
                LineReceived?.Invoke($"<{mode}|MPos:0,0,0|WPos:0,0,0|FS:0,0>");
                if (count >= 5) FiveStatuses.TrySetResult(true);
            }
        }
    }
}
