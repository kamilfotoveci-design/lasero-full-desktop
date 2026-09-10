using System.IO;
using Lasero.Core.Grbl;

namespace Lasero.Core.Machines;

/// <summary>Creates a machine connection that is independent of the one the app is using. Scanning
/// has to open and close ports that may hold something other than a laser, so it must never borrow
/// the live connection.</summary>
public interface ILaserMachineFactory
{
    ILaserMachine Create();
}

/// <summary>A controller that answered a settings query on some port at some baud rate.</summary>
public sealed record DiscoveredMachine(
    string PortName,
    int BaudRate,
    string? FirmwareBanner,
    GrblDeviceProfile Profile)
{
    public bool IsSimulator => VirtualGrblTransport.IsVirtualPort(PortName);

    /// <summary>What the controller calls itself, falling back to the port when it stayed quiet.
    /// Some boards send no banner at all until they are reset, so a missing one is not a failure.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FirmwareBanner) ? PortName : FirmwareBanner.Trim();

    public bool ReportsWorkArea => Profile.MaxTravelXmm is > 0 && Profile.MaxTravelYmm is > 0;
    public bool LaserModeIsOff => Profile.LaserModeEnabled == false;
}

public sealed record DeviceScanProgress(string PortName, int BaudRate, int PortIndex, int PortCount);

/// <summary>
/// Passive discovery offers the simulator without opening physical ports. A physical probe is
/// available only through an explicitly selected port and baud rate, never from ScanAsync.
/// </summary>
public sealed class DeviceScanner
{
    private readonly ILaserMachineFactory _factory;
    private readonly Func<IReadOnlyList<string>> _portNames;

    public DeviceScanner(ILaserMachineFactory factory, Func<IReadOnlyList<string>> portNames)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _portNames = portNames ?? throw new ArgumentNullException(nameof(portNames));
    }

    /// <summary>115200 first because that is what stock GRBL ships with; the rest are the rates the
    /// cheaper boards are flashed at. Ordered by how likely they are, so a normal machine is found
    /// on the first attempt and the slow ones only cost time when nothing else answered.</summary>
    public IReadOnlyList<int> BaudRates { get; init; } = [115200, 57600, 38400, 9600];

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Include the built-in simulator in the results. It always answers, so it is reported
    /// last and marked, never mixed in as if it were hardware.</summary>
    public bool IncludeSimulator { get; init; } = true;

    public async Task<IReadOnlyList<DiscoveredMachine>> ScanAsync(
        IProgress<DeviceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Discovery is passive: even a settings query can reset an unrelated USB device on open.
        // Physical connections belong to the explicit selected-port workflow.
        cancellationToken.ThrowIfCancellationRequested();
        var found = new List<DiscoveredMachine>();
        if (IncludeSimulator)
        {
            var simulator = await ProbeAsync(VirtualGrblTransport.PortName, BaudRates[0], cancellationToken)
                .ConfigureAwait(false);
            if (simulator is not null) found.Add(simulator);
        }

        return found;
    }

    /// <summary>Only after the operator explicitly selects and authorizes this port and baud.
    /// Never called by discovery or startup; no baud-rate guessing.</summary>
    public Task<DiscoveredMachine?> ProbeSelectedPortAsync(string port, int baudRate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
        cancellationToken.ThrowIfCancellationRequested();
        return ProbeAsync(port, baudRate, cancellationToken);
    }
    private async Task<DiscoveredMachine?> ProbeAsync(string port, int baudRate, CancellationToken cancellationToken)
    {
        ILaserMachine? machine = null;
        try
        {
            machine = _factory.Create();
            machine.Connect(port, baudRate);
            var settings = await machine.QuerySettingsAsync()
                .WaitAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            // A board at the wrong baud rate can still return a line or two of garbage. Requiring a
            // handful of parsed $n=v settings is what separates "this is GRBL" from "this is noise".
            if (settings.Count < 4) return null;

            var profile = GrblDeviceProfileParser.Parse(settings, machine.FirmwareBanner);
            return profile.NumericSettings.Count < 4
                ? null
                : new DiscoveredMachine(port, baudRate, machine.FirmwareBanner, profile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception
            is TimeoutException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (machine is not null)
            {
                try { machine.Disconnect(); } catch (Exception) { /* the port is being abandoned anyway */ }
                machine.Dispose();
            }
        }
    }
}
