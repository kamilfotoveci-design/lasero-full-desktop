namespace Lasero.App.Controls;

internal static class RulerLabelLayout
{
    public static bool FitsVertically(double centerY, double labelHeight, double rulerHeight) =>
        double.IsFinite(centerY) && double.IsFinite(labelHeight) && double.IsFinite(rulerHeight) &&
        labelHeight > 0 && centerY - labelHeight / 2 >= 0 &&
        centerY + labelHeight / 2 <= rulerHeight;
}
