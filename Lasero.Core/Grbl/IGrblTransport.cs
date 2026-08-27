namespace Lasero.Core.Grbl;

/// <summary>
/// Byte/line transport seam used by the GRBL session. The production adapter
/// is serial; tests use an in-memory adapter so connection lifecycle and
/// timeout behavior can be verified without physical hardware.
/// </summary>
public interface IGrblTransport : IDisposable
{
    bool IsOpen { get; }
    event Action<string>? LineReceived;
    event Action<Exception>? UnexpectedlyClosed;
    void Open(string portName, int baudRate);
    void Close();
    void WriteLine(string text);
    void WriteRealtimeByte(byte value);
}
