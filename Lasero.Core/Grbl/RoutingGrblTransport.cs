namespace Lasero.Core.Grbl;

/// <summary>Selects the physical serial adapter or the built-in virtual device when a session opens.</summary>
public sealed class RoutingGrblTransport : IGrblTransport
{
    private readonly IGrblTransport _physical;
    private readonly VirtualGrblTransport _virtual;
    private IGrblTransport? _active;
    private bool _disposed;

    public RoutingGrblTransport(IGrblTransport physical, VirtualGrblTransport virtualTransport)
    {
        _physical = physical ?? throw new ArgumentNullException(nameof(physical));
        _virtual = virtualTransport ?? throw new ArgumentNullException(nameof(virtualTransport));
        _physical.LineReceived += ForwardLine;
        _physical.UnexpectedlyClosed += ForwardDisconnect;
        _virtual.LineReceived += ForwardLine;
        _virtual.UnexpectedlyClosed += ForwardDisconnect;
    }

    public bool IsOpen => _active?.IsOpen == true;
    public bool IsVirtualActive => ReferenceEquals(_active, _virtual) && _virtual.IsOpen;

    public event Action<string>? LineReceived;
    public event Action<Exception>? UnexpectedlyClosed;

    public void Open(string portName, int baudRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active?.IsOpen == true) throw new InvalidOperationException("Spojení je již otevřené.");
        _active = VirtualGrblTransport.IsVirtualPort(portName) ? _virtual : _physical;
        try { _active.Open(portName, baudRate); }
        catch { _active = null; throw; }
    }

    public void Close()
    {
        var active = _active;
        _active = null;
        active?.Close();
    }

    public void WriteLine(string text) => Active().WriteLine(text);
    public void WriteRealtimeByte(byte value) => Active().WriteRealtimeByte(value);
    private IGrblTransport Active() => _active ?? throw new InvalidOperationException("Spojení není otevřené.");
    private void ForwardLine(string line) => LineReceived?.Invoke(line);
    private void ForwardDisconnect(Exception exception) => UnexpectedlyClosed?.Invoke(exception);

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _physical.LineReceived -= ForwardLine;
        _physical.UnexpectedlyClosed -= ForwardDisconnect;
        _virtual.LineReceived -= ForwardLine;
        _virtual.UnexpectedlyClosed -= ForwardDisconnect;
        _physical.Dispose();
        _virtual.Dispose();
        _disposed = true;
    }
}
