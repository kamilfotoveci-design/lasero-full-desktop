using System.Collections.Concurrent;
using Lasero.Core.Grbl;

namespace Lasero.Tests;

public sealed class GrblSessionBoundarySafetyTests
{
    [Fact]
    public async Task ResetRejectsCommandsAndStaleStatusUntilNewBanner()
    {
        var transport = new BoundaryTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("FAKE");
        transport.Emit("<Idle|MPos:1,2,0|FS:0,0>");
        Assert.NotNull(connection.LastStatus);
        var first = connection.SendCommandAsync("G1 X1");
        await transport.WaitForCount(1);
        var queued = connection.SendCommandAsync("G1 X2");

        connection.SoftReset();
        Assert.False((await first).IsOk);
        Assert.False((await queued).IsOk);
        transport.Emit("ok");
        transport.Emit("<Idle|MPos:1,2,0|FS:0,0>");
        Assert.Null(connection.LastStatus);
        Assert.Null(connection.LastStatusReceivedUtc);
        Assert.Null(connection.FirmwareBanner);
        Assert.False((await connection.SendCommandAsync("G1 X3")).IsOk);
        connection.CycleStartResume();
        Assert.DoesNotContain(GrblRealtimeCommand.CycleStartResume, transport.Realtime);
        Assert.Single(transport.Lines);

        transport.Emit("Grbl 1.1h ['$' for help]");
        Assert.Null(connection.LastStatus);
        var next = connection.SendCommandAsync("G1 X4");
        await transport.WaitForCount(2);
        Assert.False(next.IsCompleted);
        transport.Emit("ok");
        Assert.True((await next.WaitAsync(TimeSpan.FromSeconds(2))).IsOk);
        Assert.Equal(new[] { "G1 X1", "G1 X4" }, transport.Lines.ToArray());
    }

    [Fact]
    public async Task SpontaneousRestartInvalidatesQueuedCommandsAndReadiness()
    {
        var transport = new BoundaryTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("FAKE");
        transport.Emit("<Idle|MPos:1,2,0|FS:0,0>");
        var first = connection.SendCommandAsync("G1 X1");
        await transport.WaitForCount(1);
        var queued = connection.SendCommandAsync("G1 X2");
        transport.Emit("Grbl 1.1h ['$' for help]");
        Assert.False((await first).IsOk);
        Assert.False((await queued).IsOk);
        Assert.Null(connection.LastStatus);
        Assert.Null(connection.LastStatusReceivedUtc);
        Assert.Single(transport.Lines);
    }

    [Fact]
    public async Task DisconnectRejectsCommandsDuringCloseAndReconnectNeverReplays()
    {
        var transport = new BoundaryTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("FAKE");
        var first = connection.SendCommandAsync("G1 X1");
        await transport.WaitForCount(1);
        var queued = connection.SendCommandAsync("G1 X2");
        Task<GrblCommandResult>? closingCommand = null;
        transport.OnClose = () => closingCommand = connection.SendCommandAsync("G1 X3");
        connection.Disconnect();
        Assert.False((await first).IsOk);
        Assert.False((await queued).IsOk);
        Assert.NotNull(closingCommand);
        Assert.False((await closingCommand!).IsOk);
        transport.OnClose = null;
        connection.Connect("OTHER-FAKE");
        var next = connection.SendCommandAsync("G1 X4");
        await transport.WaitForCount(2);
        transport.Emit("ok");
        Assert.True((await next.WaitAsync(TimeSpan.FromSeconds(2))).IsOk);
        Assert.Equal(new[] { "G1 X1", "G1 X4" }, transport.Lines.ToArray());
    }

    [Fact]
    public async Task VirtualControllerBannerRestoresCommandFlowAfterReset()
    {
        using var connection = new GrblConnection(new VirtualGrblTransport());
        connection.Connect(VirtualGrblTransport.PortName);
        Assert.True((await connection.SendCommandAsync("G1 X1")).IsOk);
        connection.SoftReset();
        Assert.NotNull(connection.FirmwareBanner);
        Assert.True((await connection.SendCommandAsync("M5")).IsOk);
    }

    private sealed class BoundaryTransport : IGrblTransport
    {
        public bool IsOpen { get; private set; }
        public ConcurrentQueue<string> Lines { get; } = new();
        public ConcurrentQueue<byte> Realtime { get; } = new();
        public Action? OnClose { get; set; }
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed { add { } remove { } }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() { OnClose?.Invoke(); IsOpen = false; }
        public void Dispose() => Close();
        public void WriteLine(string text)
        {
            if (!IsOpen) throw new InvalidOperationException("Closed fake transport");
            Lines.Enqueue(text);
        }
        public void WriteRealtimeByte(byte value) => Realtime.Enqueue(value);
        public void Emit(string text) => LineReceived?.Invoke(text);
        public async Task WaitForCount(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Lines.Count < count) await Task.Delay(5, timeout.Token);
        }
    }
}
