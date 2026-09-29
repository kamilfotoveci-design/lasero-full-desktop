using System.IO.Ports;
using System.Text;

namespace Lasero.Core.Grbl;

/// <summary>
/// Low-level serial byte plumbing for a GRBL controller. Deliberately does not
/// rely on SerialPort.DataReceived/ReadLine: some common USB-serial chipsets
/// used on hobbyist laser boards (CH340/CH341 clones) coalesce or delay that
/// event, so lines can arrive merged or split. Instead a dedicated background
/// thread does a raw blocking read loop over the port's stream and splits on
/// '\n' itself, which is robust regardless of chipset/driver quirks.
///
/// This class knows nothing about GRBL semantics (streaming protocol, status
/// parsing, jogging) — that lives in GrblConnection. It only knows "open a
/// port, read lines, write bytes".
/// </summary>
public sealed class GrblSerialTransport : IGrblTransport
{
    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _running;
    private readonly object _writeLock = new();

    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>Raised on the background read thread — subscribers must marshal to UI thread themselves.</summary>
    public event Action<string>? LineReceived;

    /// <summary>Raised if the port drops unexpectedly (unplugged, driver error) while open.</summary>
    public event Action<Exception>? UnexpectedlyClosed;

    public static string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void Open(string portName, int baudRate)
    {
        if (IsOpen)
            throw new InvalidOperationException("Port je již otevřený. Nejprve jej zavřete.");

        var port = new SerialPort(portName, baudRate)
        {
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            NewLine = "\n",
        };

        port.Open();
        // Discard whatever was buffered before we started listening (e.g. a
        // partial banner line if the board was already running).
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        _port = port;
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Grbl-SerialRead",
        };
        _readThread.Start();
    }

    public void Close()
    {
        _running = false;
        try
        {
            _port?.Close();
        }
        catch
        {
            // Closing a port that's already faulted is fine to ignore.
        }
        var readThread = _readThread;
        if (readThread is not null && readThread != Thread.CurrentThread)
            readThread.Join(TimeSpan.FromSeconds(1));
        _port?.Dispose();
        _port = null;
        _readThread = null;
    }

    /// <summary>Writes a line of G-code / a "$" command, appending the newline GRBL expects.</summary>
    public void WriteLine(string text)
    {
        var port = _port ?? throw new InvalidOperationException("Port není otevřený.");
        lock (_writeLock)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\n");
            port.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Writes a single real-time command byte immediately, bypassing any line framing.</summary>
    public void WriteRealtimeByte(byte b)
    {
        var port = _port ?? throw new InvalidOperationException("Port není otevřený.");
        lock (_writeLock)
        {
            port.Write([b], 0, 1);
        }
    }

    private void ReadLoop()
    {
        var port = _port!;
        var buffer = new List<byte>(256);
        var readBuf = new byte[256];

        while (_running)
        {
            int read;
            try
            {
                read = port.BaseStream.Read(readBuf, 0, readBuf.Length);
            }
            catch (Exception ex) when (_running)
            {
                _running = false;
                UnexpectedlyClosed?.Invoke(ex);
                return;
            }
            catch
            {
                // Port was closed deliberately (Close() sets _running=false first) — just exit.
                return;
            }

            if (read <= 0)
                continue;

            for (int i = 0; i < read; i++)
            {
                var b = readBuf[i];
                if (b == (byte)'\n')
                {
                    var line = Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
                    buffer.Clear();
                    if (line.Length > 0)
                        LineReceived?.Invoke(line);
                }
                else
                {
                    buffer.Add(b);
                }
            }
        }
    }

    public void Dispose() => Close();
}
