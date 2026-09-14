using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>One traced object: a curve-preserving compound path (an outer contour plus any nested
/// holes, correctly wound — see CompoundPathBuilder) and the color it should be placed on. One entry
/// per disconnected top-level region/edge-group in the source bitmap, so a bitmap with several
/// separate shapes comes back as several entries rather than one merged blob.</summary>
public sealed record TracedVectorObject(VectorPath Path, RgbColor Color);

public sealed record BitmapTraceResult
{
    /// <summary>Flattened view of every traced object, in the same flat/legacy shape the tracer has
    /// always returned — still populated (from VectorPaths, see BitmapTracer) so existing consumers
    /// that only read Document.Shapes/Layers (Lasero.Avalonia's EditorViewModel/BitmapTraceViewModel,
    /// this project's own BitmapTraceViewModel preview) keep working unchanged.</summary>
    public required ImportedDocument Document { get; init; }

    /// <summary>One entry per traced object/region, carrying real curve geometry (Bezier handles,
    /// correctly wound holes) instead of the flattened polylines in Document.Shapes. This is what
    /// SceneViewModel.ReplaceRasterWithTrace now builds node-editable SceneObjects from.</summary>
    public required IReadOnlyList<TracedVectorObject> VectorPaths { get; init; }

    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required int ContourCount { get; init; }
    public required int PointCount { get; init; }

    public double WidthMm => Document.BoundingBox.MaxX - Document.BoundingBox.MinX;
    public double HeightMm => Document.BoundingBox.MaxY - Document.BoundingBox.MinY;
}
