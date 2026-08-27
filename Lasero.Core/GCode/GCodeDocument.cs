namespace Lasero.Core.GCode;

/// <summary>
/// A parsed G-code job: the original lines (streamed to GRBL as-is) plus the
/// resolved motion segments and bounding box (used for the workspace preview
/// and the framing feature).
/// </summary>
public sealed class GCodeDocument
{
    public required IReadOnlyList<string> RawLines { get; init; }
    public required IReadOnlyList<GCodeSegment> Segments { get; init; }
    public required BoundingBox2D BoundingBox { get; init; }
    public string? SourceFileName { get; init; }
}
