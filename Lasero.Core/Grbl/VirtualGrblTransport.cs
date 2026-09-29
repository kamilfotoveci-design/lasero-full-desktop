using System.Globalization;
using System.Text.RegularExpressions;

namespace Lasero.Core.Grbl;

/// <summary>
/// Hardware-free GRBL device. It uses the same byte/line boundary as a USB controller, so the
/// production connection, parser, jogging, framing and job runner are exercised unchanged.
/// </summary>
public sealed partial class VirtualGrblTransport : IGrblTransport
{
    public const string PortName = "SIMULÁTOR — Virtuální laser";
    private readonly object _sync = new();
    private Position _machinePosition;
    private Position _workOffset;
    private GrblMachineMode _mode = GrblMachineMode.Idle;
    private bool _absoluteMode = true;
    private double _feedRate;
    private double _spindleSpeed;
    private bool _disposed;

    public bool IsOpen { get; private set; }
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.FromMilliseconds(2);
    public event Action<string>? LineReceived;
    public event Action<Exception>? UnexpectedlyClosed;
    public static bool IsVirtualPort(string? portName) => string.Equals(portName, PortName, StringComparison.OrdinalIgnoreCase);

    public void Open(string portName, int baudRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsVirtualPort(portName)) throw new ArgumentException("Virtuální transport lze otevřít pouze přes port simulátoru.", nameof(portName));
        if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
        if (IsOpen) throw new InvalidOperationException("Virtuální zařízení je již připojeno.");
        lock (_sync)
        {
            IsOpen = true; _mode = GrblMachineMode.Idle; _absoluteMode = true; _feedRate = 0; _spindleSpeed = 0;
        }
        Emit("Grbl 1.1h [Virtuální laser Lasero]");
    }

    /// <summary>Current programmed spindle/laser S value inside the simulated controller.</summary>
    public double SpindleSpeed { get { lock (_sync) return _spindleSpeed; } }

    /// <summary>True while the simulated beam would be emitting (S greater than zero).</summary>
    public bool IsBeamOn => SpindleSpeed > 0;

    public void Close()
    {
        // Closing the host side of a cable does not switch a real controller's laser off: only M5,
        // a soft reset or an alarm does. The simulator must not hide that (Open still models the
        // board reset that a fresh serial session triggers).
        lock (_sync) { IsOpen = false; _mode = GrblMachineMode.Idle; }
    }


    public void WriteLine(string text)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(text)) { Respond("ok"); return; }
        var line = text.Trim();
        if (line == "$$")
        {
            Respond("$30=1000", "$32=1", "$130=400.000", "$131=400.000", "$132=50.000", "ok");
            return;
        }
        if (line == "$H")
        {
            lock (_sync) { _mode = GrblMachineMode.Home; _machinePosition = Position.Zero; _workOffset = Position.Zero; _mode = GrblMachineMode.Idle; }
            Respond("ok"); return;
        }
        if (line == "$X") { lock (_sync) _mode = GrblMachineMode.Idle; Respond("ok"); return; }

        lock (_sync)
        {
            if (_mode == GrblMachineMode.Alarm) { Respond("error:9"); return; }
            if (line.StartsWith("$J=", StringComparison.OrdinalIgnoreCase))
            {
                ApplyMotion(line[3..], jogging: true); _mode = GrblMachineMode.Idle;
            }
            else if (line.StartsWith("G10", StringComparison.OrdinalIgnoreCase) && line.Contains("L20", StringComparison.OrdinalIgnoreCase))
            {
                SetWorkOrigin(line);
            }
            else
            {
                ApplyModalCommands(line);
                if (ContainsMotion(line)) { _mode = GrblMachineMode.Run; ApplyMotion(line, jogging: false); _mode = GrblMachineMode.Idle; }
            }
        }
        Respond("ok");
    }

    public void WriteRealtimeByte(byte value)
    {
        EnsureOpen();
        lock (_sync)
        {
            switch (value)
            {
                case GrblRealtimeCommand.StatusReportQuery: EmitStatus(); return;
                case GrblRealtimeCommand.FeedHold: _mode = GrblMachineMode.Hold; break;
                case GrblRealtimeCommand.CycleStartResume:
                case GrblRealtimeCommand.JogCancel: _mode = GrblMachineMode.Idle; break;
                case GrblRealtimeCommand.SoftReset:
                    _mode = GrblMachineMode.Idle; _spindleSpeed = 0; Emit("Grbl 1.1h [Virtuální laser Lasero]"); break;
            }
            EmitStatus();
        }
    }

    public void InjectAlarm(int code = 1)
    {
        EnsureOpen();
        lock (_sync) { _mode = GrblMachineMode.Alarm; _spindleSpeed = 0; }
        Emit($"ALARM:{code}"); EmitStatus();
    }

    public void InjectDisconnect(string message = "Virtuální kabel byl odpojen.")
    {
        EnsureOpen();
        lock (_sync) IsOpen = false;
        UnexpectedlyClosed?.Invoke(new IOException(message));
    }

    private void ApplyModalCommands(string line)
    {
        if (HasCode(line, "G90")) _absoluteMode = true;
        if (HasCode(line, "G91")) _absoluteMode = false;
        if (HasCode(line, "M5")) _spindleSpeed = 0;
        if (HasCode(line, "M3") || HasCode(line, "M4")) _spindleSpeed = ReadWord(line, 'S') ?? _spindleSpeed;
        if (ReadWord(line, 'S') is { } spindle) _spindleSpeed = spindle;
        if (ReadWord(line, 'F') is { } feed) _feedRate = feed;
    }

    private void ApplyMotion(string line, bool jogging)
    {
        var relative = jogging ? HasCode(line, "G91") : !_absoluteMode;
        var work = WorkPosition;
        var x = ReadWord(line, 'X'); var y = ReadWord(line, 'Y'); var z = ReadWord(line, 'Z');
        var targetWork = new Position(
            x is null ? work.X : relative ? work.X + x.Value : x.Value,
            y is null ? work.Y : relative ? work.Y + y.Value : y.Value,
            z is null ? work.Z : relative ? work.Z + z.Value : z.Value);
        _machinePosition = new Position(targetWork.X + _workOffset.X, targetWork.Y + _workOffset.Y, targetWork.Z + _workOffset.Z);
        if (ReadWord(line, 'F') is { } feed) _feedRate = feed;
        if (ReadWord(line, 'S') is { } spindle) _spindleSpeed = spindle;
    }

    private void SetWorkOrigin(string line)
    {
        var desired = new Position(ReadWord(line, 'X') ?? WorkPosition.X, ReadWord(line, 'Y') ?? WorkPosition.Y, ReadWord(line, 'Z') ?? WorkPosition.Z);
        _workOffset = new Position(_machinePosition.X - desired.X, _machinePosition.Y - desired.Y, _machinePosition.Z - desired.Z);
    }

    private Position WorkPosition => new(_machinePosition.X - _workOffset.X, _machinePosition.Y - _workOffset.Y, _machinePosition.Z - _workOffset.Z);

    private void EmitStatus()
    {
        var work = WorkPosition;
        Emit($"<{ModeLabel(_mode)}|MPos:{F(_machinePosition.X)},{F(_machinePosition.Y)},{F(_machinePosition.Z)}" +
             $"|WPos:{F(work.X)},{F(work.Y)},{F(work.Z)}|WCO:{F(_workOffset.X)},{F(_workOffset.Y)},{F(_workOffset.Z)}" +
             $"|FS:{F(_feedRate)},{F(_spindleSpeed)}>");
    }

    private void Respond(params string[] lines)
    {
        if (ResponseDelay <= TimeSpan.Zero) { foreach (var line in lines) Emit(line); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(ResponseDelay).ConfigureAwait(false);
            if (!IsOpen) return;
            foreach (var line in lines) Emit(line);
        });
    }

    private void Emit(string line) => LineReceived?.Invoke(line);
    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen) throw new IOException("Virtuální zařízení není připojeno.");
    }
    private static bool ContainsMotion(string line) => HasCode(line, "G0") || HasCode(line, "G1") || HasCode(line, "G2") || HasCode(line, "G3");
    private static bool HasCode(string line, string code) => Regex.IsMatch(line, $@"(?i)(?:^|\s){Regex.Escape(code)}(?:\s|$)");
    private static double? ReadWord(string line, char word)
    {
        var match = WordRegex().Match(line);
        while (match.Success)
        {
            if (char.ToUpperInvariant(match.Groups[1].Value[0]) == char.ToUpperInvariant(word) &&
                double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
            match = match.NextMatch();
        }
        return null;
    }
    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    private static string ModeLabel(GrblMachineMode mode) => mode switch
    {
        GrblMachineMode.Hold => "Hold:0", GrblMachineMode.Jog => "Jog", GrblMachineMode.Alarm => "Alarm",
        GrblMachineMode.Home => "Home", GrblMachineMode.Run => "Run", _ => "Idle",
    };
    [GeneratedRegex(@"([A-Za-z])\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
    public void Dispose() { Close(); _disposed = true; }
}
