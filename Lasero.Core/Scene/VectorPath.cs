using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

// -----------------------------------------------------------------------------------------------
// VectorPath / VectorSubpath / VectorNode — curve-preserving vector geometry.
//
// Before this file existed there was no Bezier/curve data model anywhere in Lasero.Core: every
// shape (rectangle, ellipse, imported SVG, text) is ultimately stored as a flat polyline —
// ImportedShape.Points (IReadOnlyList<Position>) plus IsClosed. Even ScenePrimitiveFactory's
// "ellipse" is a 64-point polygon baked in at creation time. That flattened representation is the
// right thing for rendering and toolpath/G-code generation (both just want line segments), but it
// cannot represent an editable curve — once flattened you cannot recover the control points.
//
// VectorPath is a NEW, parallel representation introduced for the Node Edit tool. It is the single
// source of truth for objects created/edited by the vector path tool (see VectorPathSceneFactory,
// SceneObject.VectorPath). Everything downstream of SceneObject — rendering, hit-testing, toolpath
// building, SVG export — keeps consuming ImportedShape.Points exactly as before. The boundary is
// deliberate and one-directional:
//
//     VectorPath (nodes + Bezier handles, edited by the user)
//            │  Flatten() — adaptive cubic-Bezier subdivision (De Casteljau) for curved
//            │  segments, a plain line for segments with no handles on either end
//            ▼
//     ImportedShape.Points (flat polyline, unchanged consumer contract)
//
// A SceneObject that came from the vector path tool caches this model (SceneObject.VectorPath)
// alongside its already-flattened LocalShapes, the same pattern TextSource already established for
// editable text (see TextSource.cs / SceneObject.Text): the flattened points are a cached render of
// the source, not the object's only truth, so editing re-flattens instead of trying to reconstruct
// curves from a polyline.
//
// This pass deliberately does NOT attempt to make ImportedShape itself curve-aware, and does NOT
// change SvgImporter to preserve incoming curve commands — imported SVGs keep flattening to
// polylines exactly as they do today; they are just not node-editable yet. See the desktop project
// notes for the full scope decision.
// -----------------------------------------------------------------------------------------------

/// <summary>Whether a node's two Bezier handles are edited independently (CORNER) or forced to stay
/// collinear through the anchor so the curve stays tangent-continuous across the node (SMOOTH).</summary>
public enum VectorNodeType
{
    Corner,
    Smooth,
}

/// <summary>
/// One anchor point of a vector path, with optional incoming/outgoing Bezier handles. A null handle
/// means "no curve on that side" — the adjacent segment is drawn straight when both the sending
/// node's HandleOut and the receiving node's HandleIn are null, and as a cubic Bezier otherwise
/// (missing handle defaults to the anchor itself, i.e. a zero-length handle, matching standard
/// Bezier-editor behaviour when only one side of a segment has been dragged into a curve).
/// </summary>
public readonly record struct VectorNode(
    Position Anchor,
    Position? HandleIn,
    Position? HandleOut,
    VectorNodeType Type)
{
    public static VectorNode CornerAt(Position anchor) => new(anchor, null, null, VectorNodeType.Corner);

    public bool HasAnyHandle => HandleIn is not null || HandleOut is not null;

    /// <summary>Anchor and both handles (if any) translated by the same delta — a rigid move.</summary>
    public VectorNode Translated(double dx, double dy) => new(
        Anchor with { X = Anchor.X + dx, Y = Anchor.Y + dy },
        HandleIn is { } hi ? hi with { X = hi.X + dx, Y = hi.Y + dy } : null,
        HandleOut is { } ho ? ho with { X = ho.X + dx, Y = ho.Y + dy } : null,
        Type);
}

/// <summary>One open or closed contour made of VectorNodes. A VectorPath (below) is one or more of
/// these — mirroring how ImportedShape already groups multiple contours under one GeometrySetId.</summary>
public sealed record VectorSubpath
{
    public required IReadOnlyList<VectorNode> Nodes { get; init; }
    public bool IsClosed { get; init; }

    public static VectorSubpath Empty { get; } = new() { Nodes = [], IsClosed = false };

    public int SegmentCount => Nodes.Count == 0 ? 0 : (IsClosed ? Nodes.Count : Nodes.Count - 1);

    /// <summary>The two nodes bounding segment <paramref name="segmentIndex"/> (0-based, segment i
    /// runs from node i to node i+1, wrapping to node 0 for the closing segment of a closed path).</summary>
    public (VectorNode A, VectorNode B) Segment(int segmentIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= SegmentCount)
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        var a = Nodes[segmentIndex];
        var b = Nodes[(segmentIndex + 1) % Nodes.Count];
        return (a, b);
    }

    /// <summary>True when the segment between these two nodes should render as a straight line
    /// (neither endpoint has a handle facing the other), false when it needs cubic flattening.</summary>
    public static bool IsStraightSegment(VectorNode a, VectorNode b) => a.HandleOut is null && b.HandleIn is null;

    /// <summary>Flattens this subpath to a polyline in the same units as the node anchors. The first
    /// point is always Nodes[0].Anchor; every following point is either a straight neighbour or the
    /// result of adaptive cubic-Bezier subdivision. Because SegmentCount already wraps the closing
    /// segment back to Nodes[0] for a closed subpath, the returned polyline for a closed subpath
    /// already ends by revisiting Nodes[0].Anchor — the same "first point repeated as the last point"
    /// convention every other closed shape in this codebase uses (see ScenePrimitiveFactory
    /// .CreateRectangle/.CreateEllipse), so callers never need to append it themselves.</summary>
    public IReadOnlyList<Position> Flatten(double toleranceMm = VectorPath.DefaultFlattenToleranceMm)
    {
        if (Nodes.Count == 0) return [];
        var result = new List<Position> { Nodes[0].Anchor };
        if (Nodes.Count == 1) return result;

        for (var i = 0; i < SegmentCount; i++)
        {
            var (a, b) = Segment(i);
            AppendSegment(a, b, result, toleranceMm);
        }
        return result;
    }

    private static void AppendSegment(VectorNode a, VectorNode b, List<Position> result, double toleranceMm)
    {
        if (IsStraightSegment(a, b))
        {
            result.Add(b.Anchor);
            return;
        }

        var c1 = a.HandleOut ?? a.Anchor;
        var c2 = b.HandleIn ?? b.Anchor;
        CubicBezier.Flatten(a.Anchor, c1, c2, b.Anchor, toleranceMm, result);
    }
}

/// <summary>One editable vector object: one or more subpaths sharing the same drawing session (in
/// practice, today's path tool only ever produces a single subpath — the multi-subpath shape exists
/// so this model does not need to change if/when "join path" or multi-contour editing lands).</summary>
public sealed record VectorPath
{
    public required IReadOnlyList<VectorSubpath> Subpaths { get; init; }

    public const double DefaultFlattenToleranceMm = 0.05;

    public static VectorPath Empty { get; } = new() { Subpaths = [] };

    public static VectorPath SingleOpen(IReadOnlyList<VectorNode> nodes) =>
        new() { Subpaths = [new VectorSubpath { Nodes = nodes, IsClosed = false }] };

    /// <summary>Flattens every subpath — see VectorSubpath.Flatten for the closed-subpath convention
    /// (it already repeats the first point at the end, so this does not need to do it again).</summary>
    public IReadOnlyList<IReadOnlyList<Position>> FlattenAll(double toleranceMm = DefaultFlattenToleranceMm) =>
        Subpaths.Select(subpath => subpath.Flatten(toleranceMm)).ToList();

    public VectorPath ReplaceSubpath(int index, VectorSubpath replacement)
    {
        if (index < 0 || index >= Subpaths.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var updated = Subpaths.ToList();
        updated[index] = replacement;
        return this with { Subpaths = updated };
    }

    /// <summary>Replaces one subpath with zero or more subpaths in its place — the "split into two
    /// contours" shape VectorPathEditor.BreakAtNode/DeleteSegment need for an internal-node break or
    /// segment deletion on an open subpath, where the operation can change the object's subpath count
    /// without turning each result into a separate SceneObject (see LIGHTBURN_VECTOR_PARITY.md §40:
    /// a compound object may hold multiple subpaths).</summary>
    public VectorPath ReplaceSubpathWithMany(int index, IReadOnlyList<VectorSubpath> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (index < 0 || index >= Subpaths.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var updated = Subpaths.ToList();
        updated.RemoveAt(index);
        updated.InsertRange(index, replacements);
        return this with { Subpaths = updated };
    }
}

/// <summary>Cubic Bezier math shared by flattening and node-editing (De Casteljau subdivision).</summary>
public static class CubicBezier
{
    public static Position Lerp(Position a, Position b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    public static Position Evaluate(Position p0, Position c1, Position c2, Position p1, double t)
    {
        var a = Lerp(p0, c1, t);
        var b = Lerp(c1, c2, t);
        var c = Lerp(c2, p1, t);
        var d = Lerp(a, b, t);
        var e = Lerp(b, c, t);
        return Lerp(d, e, t);
    }

    /// <summary>De Casteljau split of one cubic Bezier at parameter t into two cubic Beziers whose
    /// concatenation reproduces the original curve exactly (this is what makes "insert node on a
    /// curved segment" shape-preserving instead of an approximation). P1 == Q0 is the point at t.
    /// Left curve control points, in order from the original p0: (P0, C1, C2, P1). Right curve
    /// control points, in order up to the original p1: (Q0, D1, D2, Q1).</summary>
    public static (Position P0, Position C1, Position C2, Position P1,
        Position Q0, Position D1, Position D2, Position Q1) Split(
        Position p0, Position c1, Position c2, Position p1, double t)
    {
        var a = Lerp(p0, c1, t);
        var b = Lerp(c1, c2, t);
        var c = Lerp(c2, p1, t);
        var d = Lerp(a, b, t);
        var e = Lerp(b, c, t);
        var f = Lerp(d, e, t);
        // Left curve: p0, a, d, f. Right curve: f, e, c, p1.
        return (p0, a, d, f, f, e, c, p1);
    }

    /// <summary>Appends an adaptive-subdivision flattening of one cubic Bezier segment to
    /// <paramref name="output"/> (the start point p0 is assumed already present as the previous
    /// point and is not re-added). Recursion stops once the segment is flat within
    /// <paramref name="toleranceMm"/> of a straight chord, or at a hard depth cap so a degenerate
    /// curve cannot recurse forever.</summary>
    public static void Flatten(Position p0, Position c1, Position c2, Position p1, double toleranceMm, List<Position> output, int depth = 0)
    {
        if (depth >= 20 || IsFlatEnough(p0, c1, c2, p1, toleranceMm))
        {
            output.Add(p1);
            return;
        }

        var (lp0, lc1, lc2, lp1, rp0, rc1, rc2, rp1) = Split(p0, c1, c2, p1, 0.5);
        Flatten(lp0, lc1, lc2, lp1, toleranceMm, output, depth + 1);
        Flatten(rp0, rc1, rc2, rp1, toleranceMm, output, depth + 1);
    }

    /// <summary>Standard flatness test: both control points must lie within tolerance of the p0-p1
    /// chord (perpendicular distance). Cheap, does not need the chord to be axis-aligned.</summary>
    private static bool IsFlatEnough(Position p0, Position c1, Position c2, Position p1, double toleranceMm)
    {
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var chordLenSq = dx * dx + dy * dy;
        if (chordLenSq < 1e-12)
        {
            // Degenerate (near-zero-length) chord: fall back to distance from p0 for both controls.
            var d1 = Math.Sqrt((c1.X - p0.X) * (c1.X - p0.X) + (c1.Y - p0.Y) * (c1.Y - p0.Y));
            var d2 = Math.Sqrt((c2.X - p0.X) * (c2.X - p0.X) + (c2.Y - p0.Y) * (c2.Y - p0.Y));
            return d1 <= toleranceMm && d2 <= toleranceMm;
        }

        var dist1 = PerpendicularDistance(c1, p0, dx, dy, chordLenSq);
        var dist2 = PerpendicularDistance(c2, p0, dx, dy, chordLenSq);
        return dist1 <= toleranceMm && dist2 <= toleranceMm;
    }

    private static double PerpendicularDistance(Position point, Position lineStart, double dx, double dy, double chordLenSq)
    {
        // Cross-product magnitude / chord length = perpendicular distance from point to the
        // infinite line through lineStart with direction (dx, dy).
        var cross = (point.X - lineStart.X) * dy - (point.Y - lineStart.Y) * dx;
        return Math.Abs(cross) / Math.Sqrt(chordLenSq);
    }
}
