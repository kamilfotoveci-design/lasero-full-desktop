using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>
/// Recognises a closed ring that is an axis-aligned rectangle, plain or with rounded corners, and
/// returns it as editable Bezier nodes: a plain rectangle becomes four corner nodes, a rounded one
/// becomes two nodes per rounded corner (the tangent points) with the standard kappa handles. Corner
/// radii may differ and a corner may be sharp. Anything else (chamfers, ellipse-like corners, rotated
/// rectangles) is rejected so the caller keeps its one-corner-node-per-vertex fallback.
/// </summary>
public static class RectangleCurveFit
{
    /// <summary>How far (mm) a ring point may sit from the ideal arc and still count as on it.</summary>
    public const double Tolerance = 0.05;

    private const double EdgeEpsilon = 1e-4;
    private const double Kappa = 0.5522847498307936;

    [Flags]
    private enum Side { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>The trailing duplicate of the first point must not be included in <paramref name="count"/>.</summary>
    public static bool TryFit(IReadOnlyList<Position> points, int count, out VectorSubpath subpath)
    {
        subpath = VectorSubpath.Empty;
        if (count < 4 || count > points.Count) return false;

        // Drop consecutive duplicates so a repeated vertex is not mistaken for an arc point.
        var ring = new List<Position>(count);
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return false;
            if (ring.Count > 0 && Same(ring[^1], p)) continue;
            ring.Add(p with { Z = 0 });
        }
        if (ring.Count > 1 && Same(ring[0], ring[^1])) ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 4) return false;

        var bounds = BoundingBox2D.Empty;
        foreach (var p in ring) bounds = bounds.Include(p.X, p.Y);
        var width = bounds.MaxX - bounds.MinX;
        var height = bounds.MaxY - bounds.MinY;
        if (width < 1e-3 || height < 1e-3) return false;

        var sides = new Side[ring.Count];
        var boundary = new List<int>();
        for (var i = 0; i < ring.Count; i++)
        {
            var s = Side.None;
            if (Math.Abs(ring[i].X - bounds.MinX) < EdgeEpsilon) s |= Side.Left;
            if (Math.Abs(ring[i].X - bounds.MaxX) < EdgeEpsilon) s |= Side.Right;
            if (Math.Abs(ring[i].Y - bounds.MinY) < EdgeEpsilon) s |= Side.Top;
            if (Math.Abs(ring[i].Y - bounds.MaxY) < EdgeEpsilon) s |= Side.Bottom;
            sides[i] = s;
            if (s != Side.None) boundary.Add(i);
        }
        if (boundary.Count < 4) return false;

        var m = boundary.Count;
        var handleIn = new Position?[m];
        var handleOut = new Position?[m];
        var corners = new HashSet<(bool Right, bool Bottom)>();
        for (var j = 0; j < m; j++)
        {
            var aIndex = boundary[j];
            var bIndex = boundary[(j + 1) % m];
            var aSides = sides[aIndex];
            var bSides = sides[bIndex];
            var arcCount = ((bIndex - aIndex - 1) % ring.Count + ring.Count) % ring.Count;
            if (m == 1) return false;

            if (IsSharpCorner(aSides) && !corners.Add((aSides.HasFlag(Side.Right), aSides.HasFlag(Side.Bottom))))
                return false;

            if (arcCount == 0)
            {
                // Straight run: the two points must lie on one common edge line.
                if ((aSides & bSides) == Side.None) return false;
                continue;
            }

            if (!IsEdgePoint(aSides) || !IsEdgePoint(bSides)) return false;
            var aHorizontal = aSides is Side.Top or Side.Bottom;
            var bHorizontal = bSides is Side.Top or Side.Bottom;
            if (aHorizontal == bHorizontal) return false;

            var right = (aHorizontal ? bSides : aSides) == Side.Right;
            var bottom = (aHorizontal ? aSides : bSides) == Side.Bottom;
            var corner = new Position(right ? bounds.MaxX : bounds.MinX, bottom ? bounds.MaxY : bounds.MinY, 0);
            var a = ring[aIndex];
            var b = ring[bIndex];
            var radiusA = Distance(a, corner);
            var radiusB = Distance(b, corner);
            if (radiusA < Tolerance || Math.Abs(radiusA - radiusB) > Tolerance) return false;
            var radius = (radiusA + radiusB) / 2;
            if (radius > Math.Min(width, height) / 2 + Tolerance) return false;

            var center = new Position(corner.X + (right ? -radius : radius), corner.Y + (bottom ? -radius : radius), 0);
            for (var k = 1; k <= arcCount; k++)
            {
                var p = ring[(aIndex + k) % ring.Count];
                if (Math.Abs(Distance(p, center) - radius) > Tolerance) return false;
                // Arc points bulge toward the corner, i.e. stay in its quadrant around the centre.
                if ((right ? p.X < center.X - EdgeEpsilon : p.X > center.X + EdgeEpsilon)
                    || (bottom ? p.Y < center.Y - EdgeEpsilon : p.Y > center.Y + EdgeEpsilon))
                    return false;
            }

            if (!corners.Add((right, bottom))) return false;
            handleOut[j] = Toward(a, corner);
            handleIn[(j + 1) % m] = Toward(b, corner);
        }
        if (corners.Count != 4) return false;

        var nodes = new List<VectorNode>(8);
        var hasCurve = false;
        for (var j = 0; j < m; j++)
        {
            var anchor = ring[boundary[j]];
            if (IsSharpCorner(sides[boundary[j]]))
            {
                nodes.Add(VectorNode.CornerAt(anchor));
            }
            else if (handleIn[j] is not null || handleOut[j] is not null)
            {
                hasCurve = true;
                var smooth = handleIn[j] is not null && handleOut[j] is not null;
                nodes.Add(new VectorNode(anchor, handleIn[j], handleOut[j],
                    smooth ? VectorNodeType.Smooth : VectorNodeType.Corner));
            }
            // Otherwise a collinear point in the middle of a straight edge: no node needed.
        }
        if (nodes.Count < 4 || (!hasCurve && nodes.Count != 4)) return false;

        subpath = new VectorSubpath { Nodes = nodes, IsClosed = true };
        return true;
    }

    private static bool Same(Position a, Position b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    private static bool IsSharpCorner(Side s) => s is (Side.Left | Side.Top) or (Side.Right | Side.Top)
        or (Side.Left | Side.Bottom) or (Side.Right | Side.Bottom);

    private static bool IsEdgePoint(Side s) => s is Side.Left or Side.Right or Side.Top or Side.Bottom;

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Position Toward(Position from, Position corner) =>
        new(from.X + (corner.X - from.X) * Kappa, from.Y + (corner.Y - from.Y) * Kappa, 0);
}
