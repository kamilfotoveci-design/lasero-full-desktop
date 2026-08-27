namespace Lasero.App.Controls;

/// <summary>Shared step-picking math for the grid/ruler drawn by SceneCanvas and WorkspaceCanvas — both
/// work in the same mm world space and pick the smallest step from a fixed ladder that still keeps ticks
/// at least MinTickSpacingPx apart on screen, so grid lines and ruler labels never crowd together.</summary>
internal static class RulerMath
{
    public static readonly double[] StepsMm = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
    public const double MinTickSpacingPx = 24;

    public static double PickStep(double scalePxPerMm)
    {
        var step = StepsMm[0];
        foreach (var candidate in StepsMm)
        {
            step = candidate;
            if (candidate * scalePxPerMm >= MinTickSpacingPx) break;
        }
        return step;
    }

    public static double FirstTick(double offsetMm, double step) => Math.Floor(offsetMm / step) * step;
}
