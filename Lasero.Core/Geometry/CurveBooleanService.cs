using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;
using SkiaSharp;

namespace Lasero.Core.Geometry;

/// <summary>Boolean path operations with cubic control points retained in the editable result.
/// Skia resolves intersections on curve segments; flattening remains a render/toolpath cache only.</summary>
public static class CurveBooleanService
{
    public enum Operation { Union, Difference, Intersection, Xor }

    public static VectorPath FromObjects(IReadOnlyList<SceneObject> objects)
    {
        using var result = new SKPath { FillType = SKPathFillType.Winding };
        var hasGeometry = false;
        foreach (var source in objects)
        {
            var shapes = source.GetWorldShapes();
            var vector = source.GetWorldVectorPath();
            if (vector is not null && vector.Subpaths.Count != shapes.Count)
                throw new InvalidOperationException("Obrysy a uzly cesty si neodpovídají.");
            var setIds = RepairedGeometrySetIds(shapes);
            foreach (var set in shapes.Select((shape, index) => (shape, index))
                         .Where(entry => entry.shape.IsClosed)
                         .GroupBy(entry => (GeometrySetId: setIds[entry.index], entry.shape.LayerId, entry.shape.LayerColor)))
            {
                using var compound = new SKPath { FillType = SKPathFillType.Winding };
                foreach (var (shape, index) in set)
                {
                    if (vector is not null) Append(compound, vector.Subpaths[index]);
                    else AppendPolygon(compound, shape.Points);
                }
                if (compound.IsEmpty) continue;
                if (!hasGeometry) { result.AddPath(compound); hasGeometry = true; }
                else
                {
                    using var combined = result.Op(compound, SKPathOp.Union)
                        ?? throw new InvalidOperationException("Sjednocení křivek selhalo.");
                    result.Reset();
                    result.FillType = SKPathFillType.Winding;
                    result.AddPath(combined);
                }
            }
        }
        if (!hasGeometry) return VectorPath.Empty;
        using var empty = new SKPath();
        using var normalized = result.Op(empty, SKPathOp.Union)
            ?? throw new InvalidOperationException("Vyplněná cesta se nepodařila vyhodnotit.");
        return Read(normalized);
    }

    /// <summary>Repair fallback for legacy geometry (old text) whose hole and parent carry separate
    /// GeometrySetIds: an opposite-wound contour lying inside a larger contour of another set is linked
    /// to that set, so the boolean keeps the hole instead of unioning it away. Same-wound nesting is two
    /// solid regions and is left alone (docs/fill-winding-contract.md). Never touches shapes already in
    /// one set; returns the effective set id per shape, in shape order.</summary>
    private static Guid[] RepairedGeometrySetIds(IReadOnlyList<ImportedShape> shapes)
    {
        const double minimumArea = 1e-8;
        var ids = shapes.Select(shape => shape.GeometrySetId).ToArray();
        if (shapes.Count < 2) return ids;
        var areas = shapes.Select(shape => SignedArea(shape.Points)).ToArray();
        var bounds = shapes.Select(shape => shape.Points.Count == 0 ? (MinX: 0.0, MinY: 0.0, MaxX: 0.0, MaxY: 0.0) : (
            MinX: shape.Points.Min(p => p.X), MinY: shape.Points.Min(p => p.Y),
            MaxX: shape.Points.Max(p => p.X), MaxY: shape.Points.Max(p => p.Y))).ToArray();
        const double margin = 0.004;
        for (var child = 0; child < shapes.Count; child++)
        {
            if (!shapes[child].IsClosed || shapes[child].Points.Count < 3 ||
                !double.IsFinite(areas[child]) || Math.Abs(areas[child]) <= minimumArea) continue;
            var parent = -1;
            for (var candidate = 0; candidate < shapes.Count; candidate++)
            {
                if (ids[candidate] == ids[child] || !shapes[candidate].IsClosed || shapes[candidate].Points.Count < 3 ||
                    !double.IsFinite(areas[candidate]) ||
                    Math.Sign(areas[candidate]) == Math.Sign(areas[child]) ||
                    Math.Abs(areas[candidate]) <= Math.Abs(areas[child])) continue;
                var outer = bounds[candidate]; var inner = bounds[child];
                if (inner.MinX < outer.MinX - margin || inner.MinY < outer.MinY - margin ||
                    inner.MaxX > outer.MaxX + margin || inner.MaxY > outer.MaxY + margin) continue;
                if (!PointInPolygon(shapes[child].Points[0], shapes[candidate].Points)) continue;
                if (parent < 0 || Math.Abs(areas[candidate]) < Math.Abs(areas[parent])) parent = candidate;
            }
            if (parent < 0) continue;
            var previous = ids[child];
            var target = ids[parent];
            for (var index = 0; index < ids.Length; index++)
                if (ids[index] == previous) ids[index] = target;
        }
        return ids;
    }

    private static bool PointInPolygon(Position point, IReadOnlyList<Position> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var c = polygon[current]; var p = polygon[previous];
            if ((c.Y > point.Y) == (p.Y > point.Y)) continue;
            if (point.X < (p.X - c.X) * (point.Y - c.Y) / (p.Y - c.Y) + c.X) inside = !inside;
        }
        return inside;
    }

    public static VectorPath Apply(VectorPath first, VectorPath second, Operation operation)
    {
        using var a = Write(first);
        using var b = Write(second);
        using var result = a.Op(b, operation switch
        {
            Operation.Union => SKPathOp.Union,
            Operation.Difference => SKPathOp.Difference,
            Operation.Intersection => SKPathOp.Intersect,
            Operation.Xor => SKPathOp.Xor,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        }) ?? throw new InvalidOperationException("Boolean operace křivek selhala.");
        return Read(result);
    }

    public static VectorPath Resolve(VectorPath source)
    {
        using var path = Write(source);
        using var empty = new SKPath();
        using var result = path.Op(empty, SKPathOp.Union)
            ?? throw new InvalidOperationException("Vyplněná cesta se nepodařila vyhodnotit.");
        return Read(result);
    }

    /// <summary>One object for every filled island; holes remain compound contours of that island.</summary>
    public static IReadOnlyList<VectorPath> SplitRegions(VectorPath path)
    {
        var contours = path.Subpaths.Where(subpath => subpath.IsClosed && subpath.Nodes.Count >= 2).ToList();
        if (contours.Count == 0) return [];
        var polygons = contours.Select(subpath => subpath.Flatten(0.01).ToList()).ToList();
        var signedAreas = polygons.Select(SignedArea).ToArray();
        if (signedAreas.Any(area => !double.IsFinite(area)))
            throw new InvalidOperationException("Výsledek obsahuje obrys s neplatnou plochou.");
        // Zero-area slivers are rounding debris of the boolean engine, not geometry: drop them.
        var keep = Enumerable.Range(0, contours.Count).Where(index => Math.Abs(signedAreas[index]) > 1e-8).ToList();
        if (keep.Count == 0) return [];
        if (keep.Count != contours.Count)
        {
            contours = keep.Select(index => contours[index]).ToList();
            polygons = keep.Select(index => polygons[index]).ToList();
            signedAreas = keep.Select(index => signedAreas[index]).ToArray();
        }
        var areas = signedAreas.Select(Math.Abs).ToArray();
        var bounds = polygons.Select(ring => (
            MinX: ring.Min(point => point.X), MinY: ring.Min(point => point.Y),
            MaxX: ring.Max(point => point.X), MaxY: ring.Max(point => point.Y))).ToArray();
        var parent = new int[contours.Count];
        Array.Fill(parent, -1);
        for (var child = 0; child < contours.Count; child++)
        {
            for (var candidate = 0; candidate < contours.Count; candidate++)
            {
                if (candidate == child || areas[candidate] <= areas[child]) continue;
                var outerBounds = bounds[candidate];
                var innerBounds = bounds[child];
                if (outerBounds.MinX > innerBounds.MinX || outerBounds.MinY > innerBounds.MinY ||
                    outerBounds.MaxX < innerBounds.MaxX || outerBounds.MaxY < innerBounds.MaxY) continue;
                if (!ContainsContour(polygons[candidate], polygons[child])) continue;
                if (parent[child] < 0 || areas[candidate] < areas[parent[child]]) parent[child] = candidate;
            }
        }
        var windingInside = Enumerable.Repeat(int.MinValue, contours.Count).ToArray();
        int Inside(int index)
        {
            if (windingInside[index] != int.MinValue) return windingInside[index];
            var outside = parent[index] < 0 ? 0 : Inside(parent[index]);
            var inside = outside + Math.Sign(signedAreas[index]);
            // Boolean result contours should mark a transition between empty and filled.
            // Same-wound nesting changes winding magnitude but not fill; do not mistake it for a hole.
            if ((outside == 0) == (inside == 0))
                throw new InvalidOperationException("Výsledek obsahuje nejednoznačně vnořené obrysy.");
            return windingInside[index] = inside;
        }
        var result = new List<VectorPath>();
        for (var i = 0; i < contours.Count; i++)
        {
            var outside = parent[i] < 0 ? 0 : Inside(parent[i]);
            if (outside != 0 || Inside(i) == 0) continue;
            var pieces = new List<VectorSubpath> { contours[i] };
            for (var j = 0; j < contours.Count; j++)
                if (parent[j] == i && Inside(j) == 0) pieces.Add(contours[j]);
            result.Add(new VectorPath { Subpaths = pieces });
        }
        return result;
    }

    private static SKPath Write(VectorPath path)
    {
        var result = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var subpath in path.Subpaths) Append(result, subpath);
        return result;
    }

    private static void Append(SKPath target, VectorSubpath subpath)
    {
        if (!subpath.IsClosed || subpath.Nodes.Count < 2) return;
        var first = subpath.Nodes[0].Anchor;
        target.MoveTo(Point(first));
        for (var i = 0; i < subpath.SegmentCount; i++)
        {
            var (a, b) = subpath.Segment(i);
            if (VectorSubpath.IsStraightSegment(a, b)) target.LineTo(Point(b.Anchor));
            else target.CubicTo(Point(a.HandleOut ?? a.Anchor), Point(b.HandleIn ?? b.Anchor), Point(b.Anchor));
        }
        target.Close();
    }

    private static void AppendPolygon(SKPath target, IReadOnlyList<Position> points)
    {
        if (points.Count < 3) return;
        target.MoveTo(Point(points[0]));
        foreach (var point in points.Skip(1)) target.LineTo(Point(point));
        target.Close();
    }

    private static SKPoint Point(Position p)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            Math.Abs(p.X) > 1_000_000 || Math.Abs(p.Y) > 1_000_000)
            throw new InvalidOperationException("Souřadnice křivky jsou mimo bezpečný rozsah.");
        return new SKPoint((float)p.X, (float)p.Y);
    }

    private static Position PositionOf(SKPoint point) => new(point.X, point.Y, 0);

    private static VectorPath Read(SKPath path)
    {
        var read = ReadContours(path);
        return path.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
            ? WindNestedContoursAlternately(read)
            : read;
    }

    /// <summary>Skia can return a boolean result as an even-odd path whose holes are wound the same way
    /// as their parent. The rest of Lasero (SplitRegions, toolpath fill, the fill contract) defines a hole
    /// as an opposite-wound nested contour, so rewind contours by nesting depth: even depth keeps the
    /// first outer contour's direction, odd depth gets the opposite one. Geometry is unchanged.</summary>
    private static VectorPath WindNestedContoursAlternately(VectorPath path)
    {
        if (path.Subpaths.Count < 2 || path.Subpaths.Any(subpath => !subpath.IsClosed)) return path;
        var rings = path.Subpaths.Select(subpath => subpath.Flatten(0.01).ToList()).ToList();
        var signs = rings.Select(ring => Math.Sign(SignedArea(ring))).ToArray();
        if (signs.Any(sign => sign == 0)) return path;
        var depth = new int[rings.Count];
        for (var child = 0; child < rings.Count; child++)
            for (var candidate = 0; candidate < rings.Count; candidate++)
                if (candidate != child && Math.Abs(SignedArea(rings[candidate])) > Math.Abs(SignedArea(rings[child])) &&
                    ContainsContour(rings[candidate], rings[child]))
                    depth[child]++;
        var outerSign = Enumerable.Range(0, rings.Count).Where(i => depth[i] == 0).Select(i => signs[i]).FirstOrDefault();
        if (outerSign == 0) return path;
        var subpaths = path.Subpaths.Select((subpath, i) =>
            signs[i] == (depth[i] % 2 == 0 ? outerSign : -outerSign) ? subpath : VectorPathEditor.ReverseSubpath(subpath)).ToList();
        return new VectorPath { Subpaths = subpaths };
    }

    private static VectorPath ReadContours(SKPath path)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        var subpaths = new List<VectorSubpath>();
        var nodes = new List<VectorNode>();
        void Finish(bool closed)
        {
            if (nodes.Count > 1 && nodes[0].Anchor == nodes[^1].Anchor)
            {
                var last = nodes[^1];
                nodes[0] = nodes[0] with { HandleIn = last.HandleIn };
                nodes.RemoveAt(nodes.Count - 1);
            }
            if (nodes.Count >= 2)
                subpaths.Add(new VectorSubpath
                {
                    Nodes = nodes.Select(node => node with { Type = IsSmooth(node) ? VectorNodeType.Smooth : VectorNodeType.Corner }).ToArray(),
                    IsClosed = closed,
                });
            nodes = [];
        }
        while (true)
        {
            var verb = iterator.Next(points);
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (nodes.Count > 0) Finish(false);
                    nodes.Add(VectorNode.CornerAt(PositionOf(points[0])));
                    break;
                case SKPathVerb.Line:
                    nodes.Add(VectorNode.CornerAt(PositionOf(points[1])));
                    break;
                case SKPathVerb.Quad:
                    if (nodes.Count == 0) throw new InvalidOperationException("Neplatná kvadratická cesta.");
                    var q0 = nodes[^1].Anchor;
                    var q1 = PositionOf(points[1]);
                    var q2 = PositionOf(points[2]);
                    AddCubic(CubicBezier.Lerp(q0, q1, 2.0 / 3),
                        CubicBezier.Lerp(q2, q1, 2.0 / 3), q2);
                    break;
                case SKPathVerb.Cubic:
                    AddCubic(PositionOf(points[1]), PositionOf(points[2]), PositionOf(points[3]));
                    break;
                case SKPathVerb.Conic:
                    throw new InvalidOperationException("Racionální kuželosečku nelze beze ztráty převést na Bézierovu kubiku.");
                case SKPathVerb.Close:
                    Finish(true);
                    break;
                case SKPathVerb.Done:
                    if (nodes.Count > 0) Finish(false);
                    return new VectorPath { Subpaths = subpaths };
                default:
                    throw new InvalidOperationException("Nepodporovaný segment výsledné cesty.");
            }
        }
        void AddCubic(Position control1, Position control2, Position end)
        {
            if (nodes.Count == 0) throw new InvalidOperationException("Neplatná Bézierova cesta.");
            nodes[^1] = nodes[^1] with { HandleOut = control1 };
            nodes.Add(new VectorNode(end, control2, null, VectorNodeType.Corner));
        }
    }

    private static double SignedArea(IReadOnlyList<Position> ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static bool IsSmooth(VectorNode node)
    {
        if (node.HandleIn is not { } incoming || node.HandleOut is not { } outgoing) return false;
        var ax = incoming.X - node.Anchor.X;
        var ay = incoming.Y - node.Anchor.Y;
        var bx = outgoing.X - node.Anchor.X;
        var by = outgoing.Y - node.Anchor.Y;
        var lengths = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
        return lengths > 1e-12 && ax * bx + ay * by < 0 &&
               Math.Abs(ax * by - ay * bx) <= lengths * 1e-3;
    }

    private static bool ContainsContour(IReadOnlyList<Position> outer, IReadOnlyList<Position> inner)
    {
        // Inspect vertices and edge midpoints. A single first vertex can sit on a tangent,
        // making point-in-polygon silently choose the wrong parent. Mixed or boundary
        // classifications are rejected; the caller leaves original objects untouched.
        bool? inside = null;
        for (var i = 0; i < inner.Count; i++)
        {
            var a = inner[i];
            var b = inner[(i + 1) % inner.Count];
            foreach (var point in new[] { a, CubicBezier.Lerp(a, b, 0.5) })
            {
                var classification = ClassifyPoint(outer, point);
                // A vertex lying on the outer boundary is a touch, not a verdict: decide from the
                // points that are clearly inside or outside. Only a real mix is an inconsistency.
                if (classification == 0) continue;
                var current = classification > 0;
                if (inside is not null && inside != current)
                    throw new InvalidOperationException("Obrysy výsledku se kříží; rozdělení by změnilo vyplněnou plochu.");
                inside = current;
            }
        }
        return inside == true;
    }

    // -1 outside, 0 on/near boundary, +1 inside. Flattening is only used for
    // topology classification; returned VectorSubpaths retain their cubic controls.
    private static int ClassifyPoint(IReadOnlyList<Position> ring, Position point)
    {
        const double boundaryToleranceSquared = 0.000025; // 0.005 mm
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[j]; var b = ring[i];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared > 0
                ? Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1)
                : 0;
            var nearX = a.X + t * dx - point.X;
            var nearY = a.Y + t * dy - point.Y;
            if (nearX * nearX + nearY * nearY <= boundaryToleranceSquared) return 0;
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < dx * (point.Y - a.Y) / dy + a.X) inside = !inside;
        }
        return inside ? 1 : -1;
    }
}
