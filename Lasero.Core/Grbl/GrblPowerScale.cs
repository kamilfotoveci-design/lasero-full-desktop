namespace Lasero.Core.Grbl;

/// <summary>Converts Lasero's user-facing percentage power to the controller's configured GRBL S
/// range. The controller maximum must come from its current reported $30 setting.</summary>
public static class GrblPowerScale
{
    public static double PercentToSValue(double powerPercent, double maximumSValue)
    {
        if (!double.IsFinite(powerPercent) || powerPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(powerPercent), "Laser power must be a finite percentage from 0 to 100.");
        if (!double.IsFinite(maximumSValue) || maximumSValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSValue), "The controller's $30 maximum S value must be finite and positive.");

        return powerPercent / 100.0 * maximumSValue;
    }
}
