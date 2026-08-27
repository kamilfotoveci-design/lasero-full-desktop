namespace Lasero.Core.GCode;

public readonly record struct BoundingBox2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public static readonly BoundingBox2D Empty = new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public BoundingBox2D Include(double x, double y) => new(
        Math.Min(MinX, x), Math.Min(MinY, y),
        Math.Max(MaxX, x), Math.Max(MaxY, y));
}
