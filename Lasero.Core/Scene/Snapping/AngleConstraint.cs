namespace Lasero.Core.Scene.Snapping;

/// <summary>Shift-constraint of a direction to the nearest multiple of an angle step, keeping the
/// distance. The pen uses 15° steps (0/15/30/45/…°, so 45° and 90° are always reachable), the node
/// drag 45°. Pure so the angle rule is testable without a pointer.</summary>
public static class AngleConstraint
{
    public const double PenStepDegrees = 15;

    public static (double X, double Y) Constrain(double dx, double dy, double stepDegrees)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return (0, 0);
        var magnitude = Math.Sqrt(dx * dx + dy * dy);
        if (magnitude < 1e-12 || !(stepDegrees > 0)) return (dx, dy);
        var step = stepDegrees * Math.PI / 180;
        var snapped = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return (magnitude * Math.Cos(snapped), magnitude * Math.Sin(snapped));
    }
}
