using System.Globalization;
using System.Text.RegularExpressions;

namespace Lasero.Core.Machines;

/// <summary>
/// Explicit context for an S1 pointer measurement. Height must be supplied by the caller;
/// neither the published example nor the LightBurn configuration supplies calibration.
/// Re-measure after changing height, module, firmware or machine.
/// </summary>
public sealed record XToolS1CalibrationScope
{
    public string MachineId { get; }
    public string ModuleId { get; }
    public string Firmware { get; }
    public double LaserHeightMm { get; }

    public XToolS1CalibrationScope(string machineId, string moduleId, string firmware, double laserHeightMm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmware);
        if (!double.IsFinite(laserHeightMm) || laserHeightMm < 0)
            throw new ArgumentOutOfRangeException(nameof(laserHeightMm));
        MachineId = machineId;
        ModuleId = moduleId;
        Firmware = firmware;
        LaserHeightMm = laserHeightMm;
    }
}

/// <summary>
/// Offline interpretation of the M1111 response documented at
/// https://support.xtool.com/article/1036 (retrieved 2026-09-10).
/// Does not issue commands, establish machine identity, or enable S1 execution.
/// No coordinate transform is implied: its direction still requires validation.
/// </summary>
public sealed class XToolS1PointerOffset
{
    private static readonly Regex ResponsePattern = new(
        @"\AM1111[ \t]+X(?<x>[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+))[ \t]+Y(?<y>[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+))[ \t]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public double Xmm { get; }
    public double Ymm { get; }
    public XToolS1CalibrationScope Scope { get; }

    private XToolS1PointerOffset(double x, double y, XToolS1CalibrationScope scope) =>
        (Xmm, Ymm, Scope) = (x, y, scope);

    public bool IsApplicableTo(XToolS1CalibrationScope scope) => Scope == scope;

    public static bool TryParse(string? response, XToolS1CalibrationScope scope, out XToolS1PointerOffset? offset)
    {
        ArgumentNullException.ThrowIfNull(scope);
        offset = null;
        // One complete transport line only, not a fragment or combined messages.
        if (response is null || response.Length > 256) return false;
        var match = ResponsePattern.Match(response);
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!match.Success ||
            !double.TryParse(match.Groups["x"].Value, styles, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(match.Groups["y"].Value, styles, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) || !double.IsFinite(y)) return false;
        offset = new XToolS1PointerOffset(x, y, scope);
        return true;
    }
}
