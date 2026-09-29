using System.IO.Ports;
using Lasero.Core.Machines;
using Microsoft.Win32;

namespace Lasero.App;

/// <summary>
/// Lists COM ports and, from the Windows device registry, where each one comes from (USB adapter or
/// Bluetooth) so the automatic connect can try the likely laser ports first. Read-only: it reads
/// registry values and never opens a port. When the registry has nothing to say about a port it is
/// simply reported without a description.
/// </summary>
public sealed class WindowsSerialPortEnumerator : ISerialPortEnumerator
{
    private static readonly (string Root, SerialPortKind Kind)[] EnumRoots =
    [
        ("USB", SerialPortKind.UsbSerialAdapter),
        ("FTDIBUS", SerialPortKind.UsbSerialAdapter),
        ("BTHENUM", SerialPortKind.Bluetooth),
    ];

    public IReadOnlyList<SerialPortCandidate> GetPorts()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch (Exception) { return []; }

        var known = ReadRegistry();
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => known.TryGetValue(name, out var info) ? new SerialPortCandidate(name, info.Description, info.Kind) : new SerialPortCandidate(name))
            .ToArray();
    }

    private static Dictionary<string, (string? Description, SerialPortKind Kind)> ReadRegistry()
    {
        var result = new Dictionary<string, (string?, SerialPortKind)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumKey is null) return result;
            foreach (var (root, rootKind) in EnumRoots)
            {
                using var rootKey = enumKey.OpenSubKey(root);
                if (rootKey is null) continue;
                foreach (var deviceName in rootKey.GetSubKeyNames())
                {
                    using var deviceKey = rootKey.OpenSubKey(deviceName);
                    if (deviceKey is null) continue;
                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instanceName);
                        using var parameters = instanceKey?.OpenSubKey("Device Parameters");
                        if (parameters?.GetValue("PortName") is not string port || string.IsNullOrWhiteSpace(port)) continue;
                        var description = instanceKey!.GetValue("FriendlyName") as string;
                        var kind = SerialPortCandidate.Classify(description);
                        result[port] = (description, kind == SerialPortKind.Unknown ? rootKind : kind);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Registry access is a hint only; ordering falls back to COM number.
        }
        return result;
    }
}
