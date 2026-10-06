using System.Text.RegularExpressions;

namespace Lasero.Core.Grbl;

/// <summary>
/// Friendly names for serial ports ("COM3 - USB-SERIAL CH340"). Windows keeps the device description in
/// the registry under Enum\{bus}\{hardware id}\{instance}\FriendlyName as "USB-SERIAL CH340 (COM3)";
/// reading it needs no extra package and no device access. Anything that cannot be read falls back to the
/// bare port name, so the list is never shorter than what <see cref="GrblConnection.GetAvailablePortNames"/> returns.
/// </summary>
public static partial class SerialPortDescriptions
{
    [GeneratedRegex(@"^(?<name>.*?)\s*\((?<port>COM\d+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FriendlyNamePattern();

    /// <summary>Port name to device description, for the ports Windows describes.</summary>
    public static IReadOnlyDictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            using var enumRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumRoot is null) return result;
            foreach (var bus in new[] { "USB", "FTDIBUS", "ACPI", "PCI", "ROOT" })
            {
                using var busKey = enumRoot.OpenSubKey(bus);
                if (busKey is null) continue;
                foreach (var hardware in busKey.GetSubKeyNames())
                {
                    using var hardwareKey = busKey.OpenSubKey(hardware);
                    if (hardwareKey is null) continue;
                    foreach (var instance in hardwareKey.GetSubKeyNames())
                    {
                        using var instanceKey = hardwareKey.OpenSubKey(instance);
                        if (instanceKey?.GetValue("FriendlyName") is not string friendly) continue;
                        var match = FriendlyNamePattern().Match(friendly);
                        if (match.Success) result[match.Groups["port"].Value] = match.Groups["name"].Value.Trim();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Registry access can be denied or the layout can differ; the bare port names still work.
        }

        return result;
    }

    /// <summary>"COM3 - USB-SERIAL CH340" when a description is known, otherwise "COM3".</summary>
    public static string Label(string portName, IReadOnlyDictionary<string, string> descriptions) =>
        descriptions.TryGetValue(portName, out var description) && !string.IsNullOrWhiteSpace(description)
            ? $"{portName} - {description}"
            : portName;
}
