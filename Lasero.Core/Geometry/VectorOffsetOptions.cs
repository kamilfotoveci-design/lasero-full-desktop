namespace Lasero.Core.Geometry;

/// <summary>Which side of the selection's outline the offset is created on.</summary>
public enum VectorOffsetDirection
{
    /// <summary>Grows the union of the selected shapes.</summary>
    Outward,
    /// <summary>Shrinks the union of the selected shapes.</summary>
    Inward,
    /// <summary>Both results: the outward outline and the inward outline, as two separate objects.</summary>
    Both,
}

/// <summary>Settings of the Offset dialog. Distance is a magnitude: the side comes from
/// <see cref="Direction"/>, so a negative distance is treated as its absolute value.</summary>
public sealed record VectorOffsetOptions
{
    public const double DefaultDistanceMm = 5.0;

    /// <summary>Ratio of the longest allowed miter spike to the offset distance. Corners sharper
    /// than this are cut flat instead of spiking out (a right angle needs about 1.41).</summary>
    public const double DefaultMiterLimit = 3.0;

    /// <summary>Largest distance an optimized outline may move from the exact result. Far below the
    /// laser's kerf, so optimizing only removes nodes that cannot be seen or burned.</summary>
    public const double OptimizeToleranceMm = 0.01;

    public double DistanceMm { get; init; } = DefaultDistanceMm;
    public VectorOffsetDirection Direction { get; init; } = VectorOffsetDirection.Outward;
    public VectorJoinType JoinType { get; init; } = VectorJoinType.Round;
    public double MiterLimit { get; init; } = DefaultMiterLimit;

    /// <summary>True: only the outer boundary of every shape is offset. Counters (holes) and shapes
    /// nested inside them are ignored, so a letter "B" yields one outline, not one around each
    /// counter. False: holes are kept and offset with the opposite winding.</summary>
    public bool OnlyOuterShapes { get; init; } = true;

    /// <summary>Merge repeated and near-collinear points so the result has fewer nodes.</summary>
    public bool Optimize { get; init; } = true;
}
