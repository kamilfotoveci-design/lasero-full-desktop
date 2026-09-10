using Lasero.Core.Grbl;

namespace Lasero.Core.Machines;

/// <summary>Legacy catalog defaults, retained for source compatibility; not verified executable profiles. The PIXI entry
/// covers the 3W, 5W and 10W laser-module variants. The controller's
/// own $130/$131 values require validation; these entries never establish identity or provide a fallback
/// when an older firmware does not report travel limits.</summary>
public sealed record KnownMachineProfile(
    string Id,
    string DisplayName,
    double WorkAreaWidthMm,
    double WorkAreaHeightMm,
    double MaxFeedRateMmPerMinute,
    int DefaultBaudRate);

public static class KnownMachineProfiles
{
    public static readonly KnownMachineProfile AlgoLaserPixi = new(
        "algolaser-pixi",
        "AlgoLaser PIXI (3W / 5W / 10W)",
        100,
        100,
        6000,
        115200);

    public static readonly KnownMachineProfile AlgoLaserAlphaMk2_20W = new(
        "algolaser-alpha-mk2-20w",
        "AlgoLaser Alpha MK2 20W",
        400,
        410,
        20000,
        115200);

    public static KnownMachineProfile? Match(GrblDeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Travel limits cannot establish manufacturer, model, module or firmware identity.
        return null;
    }

    private static bool Matches(GrblDeviceProfile profile, KnownMachineProfile machine) =>
        profile.MaxTravelXmm is { } width && profile.MaxTravelYmm is { } height &&
        Math.Abs(width - machine.WorkAreaWidthMm) < 0.5 &&
        Math.Abs(height - machine.WorkAreaHeightMm) < 0.5;
}
