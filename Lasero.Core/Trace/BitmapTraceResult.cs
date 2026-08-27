using Lasero.Core.Import;

namespace Lasero.Core.Trace;

public sealed record BitmapTraceResult
{
    public required ImportedDocument Document { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required int ContourCount { get; init; }
    public required int PointCount { get; init; }

    public double WidthMm => Document.BoundingBox.MaxX - Document.BoundingBox.MinX;
    public double HeightMm => Document.BoundingBox.MaxY - Document.BoundingBox.MinY;
}
