namespace Lasero.Core.Import.Svg;

internal sealed class SvgSubpath
{
    public List<(double X, double Y)> Points { get; } = new();
    public bool Closed { get; set; }
}
