using System.Globalization;
using System.Text.RegularExpressions;
using Lasero.Core.Grbl;

namespace Lasero.Core.Machines;

/// <summary>Where a serial port comes from, as far as Windows can tell. Only used to decide the order
/// a scan tries ports in, and to skip ports that can never be a laser.</summary>
public enum SerialPortKind
{
    Unknown,
    /// <summary>CH340/CH341, CP210x, FTDI or a native USB CDC device: what nearly every GRBL laser board uses.</summary>
    UsbSerialAdapter,
    /// <summary>A Bluetooth serial profile. Opening one can block for many seconds and it is never a wired laser.</summary>
    Bluetooth,
}

public sealed record SerialPortCandidate(string PortName, string? Description = null, SerialPortKind Kind = SerialPortKind.Unknown)
{
    /// <summary>Best-effort classification from a Windows device description such as
    /// "USB-SERIAL CH340 (COM3)". Returns Unknown rather than guessing when nothing matches.</summary>
    public static SerialPortKind Classify(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return SerialPortKind.Unknown;
        if (description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return SerialPortKind.Bluetooth;
        string[] usbMarkers = ["CH340", "CH341", "CH9102", "CP210", "Silicon Labs", "FTDI", "USB Serial", "USB-SERIAL", "Arduino", "USB Serial Device", "CDC"];
        return usbMarkers.Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? SerialPortKind.UsbSerialAdapter
            : SerialPortKind.Unknown;
    }
}

/// <summary>Lists the serial ports Windows knows about. Implementations must not open any of them.</summary>
public interface ISerialPortEnumerator
{
    IReadOnlyList<SerialPortCandidate> GetPorts();
}

/// <summary>Creates a fresh, independent transport per probe so a probe never touches the live session.</summary>
public interface IGrblTransportFactory
{
    IGrblTransport Create();
}

public enum GrblProbeOutcome
{
    /// <summary>The port answered like a GRBL controller.</summary>
    Grbl,
    /// <summary>Another program holds the port (access denied).</summary>
    Busy,
    /// <summary>The port could not be opened, or opening it hung.</summary>
    OpenFailed,
    /// <summary>Opened fine but stayed silent at every baud rate tried.</summary>
    NoResponse,
    /// <summary>Something answered, but not in GRBL's dialect.</summary>
    NotGrbl,
    /// <summary>Not tried on purpose (for example a Bluetooth port).</summary>
    Skipped,
}

public sealed record GrblProbeResult(
    SerialPortCandidate Port,
    GrblProbeOutcome Outcome,
    int? BaudRate = null,
    string? FirmwareBanner = null,
    string? StateLabel = null)
{
    public bool IsGrbl => Outcome == GrblProbeOutcome.Grbl;
    public string PortName => Port.PortName;

    /// <summary>What the controller calls itself; plain GRBL when it never said.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FirmwareBanner) ? "GRBL" : FirmwareBanner.Trim();
}

public sealed record GrblPortScanProgress(string PortName, int BaudRate, int PortIndex, int PortCount);

public sealed record GrblPortScanResult(IReadOnlyList<GrblProbeResult> Grbl, IReadOnlyList<GrblProbeResult> Others)
{
    public int PortsExamined => Grbl.Count + Others.Count;
}

public interface IGrblPortScanner
{
    /// <summary>Probes serial ports for a GRBL controller. Only ever called after an explicit operator
    /// action; it must not run at start-up or in the background.</summary>
    Task<GrblPortScanResult> ScanAsync(IProgress<GrblPortScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds GRBL controllers without the operator naming a model, port or baud rate.
/// <para>
/// The probe is strictly read-only. The only bytes it may ever write are the realtime status query
/// <c>?</c> (single byte, no newline) and the build-info request <c>$I</c>. It never sends motion, homing,
/// unlock, spindle/laser words, a soft reset (0x18) or any <c>$n=v</c> setting write. <c>$I</c> is sent
/// only after the port has already answered like GRBL, so an unrelated device on a neighbouring COM
/// number only ever receives a single <c>?</c>.
/// </para>
/// <para>
/// Opening a port can still reset the board behind it (DTR toggles on most USB-serial adapters); that is
/// inherent to opening it and identical to a normal connect. The probe closes every port it opened.
/// </para>
/// </summary>
public sealed class GrblPortScanner : IGrblPortScanner
{
    private static readonly Regex BannerPattern = new(@"^Grbl\s+(?<v>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StatusPattern = new(@"^<(?<state>[A-Za-z]+)(:\d+)?[|,>]", RegexOptions.Compiled);

    private readonly ISerialPortEnumerator _ports;
    private readonly IGrblTransportFactory _transports;

    public GrblPortScanner(ISerialPortEnumerator ports, IGrblTransportFactory transports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
    }

    /// <summary>115200 first (stock GRBL 1.1), then 250000 (some 32-bit ports), 57600 and 9600 (old builds).</summary>
    public IReadOnlyList<int> BaudRates { get; init; } = [115200, 250000, 57600, 9600];

    /// <summary>How long Open may take. Anything slower is treated as a dead or virtual port.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Boards that reset on open print their banner after roughly one to two seconds.</summary>
    public TimeSpan BannerWait { get; init; } = TimeSpan.FromMilliseconds(1600);

    /// <summary>How long GRBL gets to answer <c>?</c> or <c>$I</c>. It answers within a few milliseconds when alive.</summary>
    public TimeSpan ResponseWait { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>USB adapters first, unknown origin next; within a group by COM number so the order is stable.</summary>
    public static IReadOnlyList<SerialPortCandidate> Order(IEnumerable<SerialPortCandidate> ports) => ports
        .Where(port => !string.IsNullOrWhiteSpace(port.PortName))
        .GroupBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(port => port.Kind switch
        {
            SerialPortKind.UsbSerialAdapter => 0,
            SerialPortKind.Unknown => 1,
            _ => 2,
        })
        .ThenBy(port => ComNumber(port.PortName))
        .ThenBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static int ComNumber(string name) =>
        name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    public async Task<GrblPortScanResult> ScanAsync(IProgress<GrblPortScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ordered = Order(_ports.GetPorts());
        var found = new List<GrblProbeResult>();
        var others = new List<GrblProbeResult>();

        for (var index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = ordered[index];
            if (port.Kind == SerialPortKind.Bluetooth)
            {
                others.Add(new GrblProbeResult(port, GrblProbeOutcome.Skipped));
                continue;
            }

            GrblProbeResult? last = null;
            var sawTraffic = false;
            foreach (var baud in BaudRates)
            {
                progress?.Report(new GrblPortScanProgress(port.PortName, baud, index, ordered.Count));
                var attempt = await ProbeAsync(port, baud, cancellationToken).ConfigureAwait(false);
                last = attempt.Result;
                sawTraffic |= attempt.SawTraffic;
                // Busy or unopenable ports would fail identically at every other baud rate.
                if (last.Outcome is GrblProbeOutcome.Grbl or GrblProbeOutcome.Busy or GrblProbeOutcome.OpenFailed) break;
            }

            if (last is null) continue;
            if (last.IsGrbl) found.Add(last);
            else if (last.Outcome == GrblProbeOutcome.NoResponse)
                others.Add(last with { Outcome = sawTraffic ? GrblProbeOutcome.NotGrbl : GrblProbeOutcome.NoResponse, BaudRate = null });
            else others.Add(last);
        }

        return new GrblPortScanResult(found, others);
    }

    private readonly record struct ProbeAttempt(GrblProbeResult Result, bool SawTraffic);

    private async Task<ProbeAttempt> ProbeAsync(SerialPortCandidate port, int baud, CancellationToken cancellationToken)
    {
        var lines = new LineBuffer();
        IGrblTransport? transport = null;
        try
        {
            transport = _transports.Create();
            transport.LineReceived += lines.Add;

            var opening = Task.Run(() => transport.Open(port.PortName, baud), CancellationToken.None);
            try
            {
                await opening.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Open is stuck inside the driver. Abandon it; whatever it eventually opens is closed below.
                var abandoned = transport;
                transport = null;
                _ = opening.ContinueWith(_ => SafeCloseAndDispose(abandoned), TaskScheduler.Default);
                return new ProbeAttempt(new GrblProbeResult(port, GrblProbeOutcome.OpenFailed), false);
            }
            catch (UnauthorizedAccessException)
            {
                return new ProbeAttempt(new GrblProbeResult(port, GrblProbeOutcome.Busy), false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ProbeAttempt(new GrblProbeResult(port, GrblProbeOutcome.OpenFailed), false);
            }

            // Boards that reset on open announce themselves. Waiting for that costs nothing extra on the
            // ones that do, and the status query below is what catches the ones that do not.
            var recognised = await lines.WaitForAsync(IsGrblLine, BannerWait, cancellationToken).ConfigureAwait(false);
            if (!recognised)
            {
                Write(() => transport.WriteRealtimeByte(GrblRealtimeCommand.StatusReportQuery));
                recognised = await lines.WaitForAsync(IsGrblLine, ResponseWait, cancellationToken).ConfigureAwait(false);
            }

            if (!recognised)
                return new ProbeAttempt(new GrblProbeResult(port, GrblProbeOutcome.NoResponse, baud), lines.Snapshot().Count > 0);

            var state = lines.Snapshot().Select(ParseState).FirstOrDefault(s => s is not null);
            // $I is refused (error:8) while a job runs; asking is pointless then, and this way a machine
            // that is busy is never sent anything but the status query.
            var mayAskBuildInfo = state is null || state is "Idle" or "Alarm";
            if (mayAskBuildInfo)
            {
                Write(() => transport.WriteLine("$I"));
                await lines.WaitForAsync(l => l.Any(x => x == "ok" || x.StartsWith("error:", StringComparison.Ordinal)), ResponseWait, cancellationToken)
                    .ConfigureAwait(false);
            }

            var snapshot = lines.Snapshot();
            return new ProbeAttempt(
                new GrblProbeResult(port, GrblProbeOutcome.Grbl, baud, ExtractFirmware(snapshot), state ?? ParseState(snapshot)),
                true);
        }
        finally
        {
            if (transport is not null) SafeCloseAndDispose(transport);
        }
    }

    private static void Write(Action write)
    {
        try { write(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            // A write failure just means no answer arrives; the caller reports NoResponse.
        }
    }

    private static void SafeCloseAndDispose(IGrblTransport transport)
    {
        try { transport.Close(); } catch (Exception) { /* abandoning the port */ }
        try { transport.Dispose(); } catch (Exception) { /* abandoning the port */ }
    }

    private static bool IsGrblLine(IReadOnlyList<string> lines) => lines.Any(line =>
        BannerPattern.IsMatch(line) || StatusPattern.IsMatch(line));

    private static string? ParseState(IReadOnlyList<string> lines) => lines.Select(ParseState).FirstOrDefault(s => s is not null);

    private static string? ParseState(string line)
    {
        var match = StatusPattern.Match(line);
        return match.Success ? match.Groups["state"].Value : null;
    }

    private static string? ExtractFirmware(IReadOnlyList<string> lines)
    {
        var banner = lines.FirstOrDefault(line => BannerPattern.IsMatch(line));
        if (banner is not null) return banner.Trim();
        // [VER:1.1f.20170801:] is what $I answers when the banner was missed.
        var ver = lines.FirstOrDefault(line => line.StartsWith("[VER:", StringComparison.OrdinalIgnoreCase));
        if (ver is null) return null;
        var parts = ver.Trim('[', ']').Split(':');
        return parts.Length >= 2 && parts[1].Length > 0 ? $"Grbl {parts[1]}" : null;
    }

    private sealed class LineBuffer
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _signal = new(0);

        public void Add(string line)
        {
            lock (_gate) _lines.Add(line.Trim());
            _signal.Release();
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return _lines.ToArray();
        }

        public async Task<bool> WaitForAsync(Func<IReadOnlyList<string>, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (predicate(Snapshot())) return true;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                if (!await _signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false)) return predicate(Snapshot());
            }
        }
    }
}

/// <summary>Production transport factory: a plain serial transport per probe.</summary>
public sealed class SerialGrblTransportFactory : IGrblTransportFactory
{
    public IGrblTransport Create() => new GrblSerialTransport();
}
