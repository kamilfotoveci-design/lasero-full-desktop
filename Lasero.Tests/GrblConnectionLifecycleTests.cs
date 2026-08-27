using System.Collections.Concurrent;
using System.IO;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class GrblConnectionLifecycleTests
{
    [Fact]
    public void GreetingLineSetsFirmwareBannerAndRaisesConnected()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");

        string? bannerFromEvent = null;
        connection.Connected += banner => bannerFromEvent = banner;
        transport.RaiseLine("Grbl 1.1h ['$' for help]");

        Assert.Equal("Grbl 1.1h ['$' for help]", connection.FirmwareBanner);
        Assert.Equal("Grbl 1.1h ['$' for help]", bannerFromEvent);
    }

    [Fact]
    public async Task ErrorLineRaisesErrorReceivedSetsActiveAlertAndClearsOnNextOk()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(2) };
        connection.Connect("COM1");

        (int Code, string Message)? received = null;
        MachineAlert? changedTo = null;
        connection.ErrorReceived += (code, message) => received = (code, message);
        connection.AlertChanged += alert => changedTo = alert;

        var pending = connection.SendCommandAsync("G0 X1");
        Assert.True(transport.WaitForLineCount(1));
        transport.RaiseLine("error:9");

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(result.IsOk);
        Assert.Equal(9, received!.Value.Code);
        Assert.Contains("odemkněte", received.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(connection.ActiveAlert);
        Assert.Equal(MachineAlertKind.Error, connection.ActiveAlert!.Kind);
        Assert.Equal(9, connection.ActiveAlert.Code);
        Assert.NotNull(changedTo);
        Assert.Equal(LaserMachineDisplayState.Error, connection.DisplayState);

        var second = connection.SendCommandAsync("G0 X2");
        Assert.True(transport.WaitForLineCount(2));
        transport.RaiseLine("ok");
        await second.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(connection.ActiveAlert);
    }

    [Fact]
    public void AlarmLineRaisesAlarmReceivedAndDisplayStateBecomesAlarmUntilStatusClearsIt()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");

        (int Code, string Message)? received = null;
        connection.AlarmReceived += (code, message) => received = (code, message);
        transport.RaiseLine("ALARM:1");

        Assert.Equal(1, received!.Value.Code);
        Assert.NotNull(connection.ActiveAlert);
        Assert.Equal(MachineAlertKind.Alarm, connection.ActiveAlert!.Kind);
        Assert.Equal(LaserMachineDisplayState.Alarm, connection.DisplayState);

        transport.RaiseLine("<Idle|MPos:0,0,0|FS:0,0>");

        Assert.Null(connection.ActiveAlert);
        Assert.Equal(LaserMachineDisplayState.Idle, connection.DisplayState);
    }

    [Fact]
    public void DismissAlertClearsActiveAlertImmediately()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport);
        connection.Connect("COM1");
        transport.RaiseLine("ALARM:2");
        Assert.NotNull(connection.ActiveAlert);

        connection.DismissAlert();

        Assert.Null(connection.ActiveAlert);
    }

    [Theory]
    [InlineData(GrblConnectionState.Disconnected, null, LaserMachineDisplayState.Disconnected)]
    [InlineData(GrblConnectionState.Connecting, null, LaserMachineDisplayState.Connecting)]
    [InlineData(GrblConnectionState.Connected, null, LaserMachineDisplayState.Connecting)]
    [InlineData(GrblConnectionState.Connected, GrblMachineMode.Idle, LaserMachineDisplayState.Idle)]
    [InlineData(GrblConnectionState.Connected, GrblMachineMode.Run, LaserMachineDisplayState.Run)]
    public void DisplayStateResolverMapsLinkAndModeWithoutAlert(
        GrblConnectionState link, GrblMachineMode? mode, LaserMachineDisplayState expected)
    {
        Assert.Equal(expected, LaserMachineDisplayStateResolver.Resolve(link, mode, null));
    }

    [Fact]
    public void DisplayStateResolverPrefersActiveAlertOverMode()
    {
        var alarm = new MachineAlert { Kind = MachineAlertKind.Alarm, Code = 1, Message = "x", OccurredUtc = DateTime.UtcNow };
        var error = new MachineAlert { Kind = MachineAlertKind.Error, Code = 9, Message = "y", OccurredUtc = DateTime.UtcNow };

        Assert.Equal(LaserMachineDisplayState.Alarm, LaserMachineDisplayStateResolver.Resolve(GrblConnectionState.Connected, GrblMachineMode.Idle, alarm));
        Assert.Equal(LaserMachineDisplayState.Error, LaserMachineDisplayStateResolver.Resolve(GrblConnectionState.Connected, GrblMachineMode.Idle, error));
    }

    [Fact]
    public async Task ConnectionCanDisconnectAndReconnect()
    {
        var transport = new FakeTransport { AutoResponse = "ok" };
        using var connection = new GrblConnection(transport);

        connection.Connect("COM1");
        Assert.True((await connection.SendCommandAsync("G0 X1")).IsOk);
        connection.Disconnect();
        connection.Connect("COM1");
        Assert.True((await connection.SendCommandAsync("G0 X2")).IsOk);

        Assert.Equal(2, transport.OpenCount);
        Assert.Equal(GrblConnectionState.Connected, connection.State);
    }

    [Fact]
    public void FailedOpenReturnsConnectionToDisconnectedState()
    {
        var transport = new FakeTransport { OpenException = new IOException("Port je obsazený.") };
        using var connection = new GrblConnection(transport);

        Assert.Throws<IOException>(() => connection.Connect("COM1"));
        Assert.Equal(GrblConnectionState.Disconnected, connection.State);

        transport.OpenException = null;
        connection.Connect("COM1");
        Assert.Equal(GrblConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task MissingResponseTimesOutAndClosesUnsafeSession()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromMilliseconds(60) };
        connection.Connect("COM1");

        var result = await connection.SendCommandAsync("G0 X1").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsOk);
        Assert.Contains("časovém limitu", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GrblConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task DisconnectCompletesPendingAndQueuedCommands()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(10) };
        connection.Connect("COM1");
        var first = connection.SendCommandAsync("G0 X1");
        var second = connection.SendCommandAsync("G0 X2");
        Assert.True(transport.LineWritten.Wait(TimeSpan.FromSeconds(1)));

        connection.Disconnect();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.False(result.IsOk));
        Assert.Equal(GrblConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task SettingsQueryStartsCollectingOnlyWhenItsOwnCommandIsSent()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(2) };
        connection.Connect("COM1");
        var normalCommand = connection.SendCommandAsync("G0 X1");
        Assert.True(transport.WaitForLineCount(1));

        var settingsQuery = connection.QuerySettingsAsync();
        transport.RaiseLine("ok");
        Assert.True((await normalCommand).IsOk);
        Assert.True(transport.WaitForLineCount(2));
        transport.RaiseLine("$130=500.000");
        transport.RaiseLine("ok");

        var settings = await settingsQuery.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(["$130=500.000"], settings);
    }

    [Fact]
    public async Task SoftResetCancelsPendingAndQueuedCommandsWithoutSendingTheNextLine()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(10) };
        connection.Connect("COM1");
        var first = connection.SendCommandAsync("G1 X1");
        var second = connection.SendCommandAsync("G1 X2");
        Assert.True(transport.WaitForLineCount(1));

        connection.SoftReset();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.False(result.IsOk));
        Assert.Single(transport.WrittenLines);
        Assert.Equal(GrblConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task ThrowingUiSubscriberCannotBreakProtocolParsingOrDisconnectMachine()
    {
        var transport = new FakeTransport();
        using var connection = new GrblConnection(transport) { CommandTimeout = TimeSpan.FromSeconds(2) };
        connection.Connect("COM1");
        MachineStatus? receivedStatus = null;
        connection.RawLineReceived += _ => throw new InvalidOperationException("UI handler failed");
        connection.StatusUpdated += status => receivedStatus = status;

        transport.RaiseLine("<Idle|MPos:1,2,3|FS:0,0>");

        Assert.NotNull(receivedStatus);
        Assert.Equal(GrblConnectionState.Connected, connection.State);

        var command = connection.SendCommandAsync("G0 X1");
        Assert.True(transport.WaitForLineCount(1));
        transport.RaiseLine("ok");
        Assert.True((await command.WaitAsync(TimeSpan.FromSeconds(1))).IsOk);
        Assert.Equal(GrblConnectionState.Connected, connection.State);
    }

    private sealed class FakeTransport : IGrblTransport
    {
        private readonly ConcurrentQueue<string> _writtenLines = new();

        public bool IsOpen { get; private set; }
        public int OpenCount { get; private set; }
        public Exception? OpenException { get; set; }
        public string? AutoResponse { get; set; }
        public ManualResetEventSlim LineWritten { get; } = new();
        public IReadOnlyCollection<string> WrittenLines => _writtenLines.ToArray();

        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed;

        public void Open(string portName, int baudRate)
        {
            if (OpenException is not null) throw OpenException;
            IsOpen = true;
            OpenCount++;
        }

        public void Close() => IsOpen = false;

        public void WriteLine(string text)
        {
            if (!IsOpen) throw new IOException("Port není otevřený.");
            _writtenLines.Enqueue(text);
            LineWritten.Set();
            if (AutoResponse is not null)
                LineReceived?.Invoke(AutoResponse);
        }

        public void WriteRealtimeByte(byte value)
        {
            if (!IsOpen) throw new IOException("Port není otevřený.");
        }

        public void RaiseDisconnect(Exception exception) => UnexpectedlyClosed?.Invoke(exception);
        public void RaiseLine(string line) => LineReceived?.Invoke(line);

        public bool WaitForLineCount(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (_writtenLines.Count < expected && DateTime.UtcNow < deadline)
                LineWritten.Wait(TimeSpan.FromMilliseconds(20));
            return _writtenLines.Count >= expected;
        }
        public void Dispose() => Close();
    }
}
