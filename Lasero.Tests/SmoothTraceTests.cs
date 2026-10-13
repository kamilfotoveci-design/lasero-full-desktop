using Lasero.Core.Grbl;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.Tests;

/// <summary>
/// Smoothness of the trace on hard-edged (not anti-aliased) artwork, whose pixel staircases must not
/// reach the vector, and on outlined letters, where a wrongly snapped corner or a chord across the
/// letter would show as a straight spike. One source pixel is one millimetre; mm y is image y flipped.
/// </summary>
public sealed class SmoothTraceTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(15)]
    public void HardEdgedCircle_IsAFewSmoothCubicsOnTheIdealCircle(int radius)
    {
        const int size = 120;
        var image = Render(size, size, (x, y) => Sq(x - 60) + Sq(y - 60) <= radius * radius, samples: 1);

        var result = BitmapTracer.Trace(image, Options(size));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.True(subpath.Nodes.Count <= 16, $"expected at most 16 cubics, got {subpath.Nodes.Count}");
        var worst = subpath.Flatten(0.005).Max(p => Math.Abs(Math.Sqrt(Sq(p.X - 60) + Sq(p.Y - (size - 60))) - radius));
        Assert.True(worst <= 1.0, $"circle deviates {worst:0.00} px from the ideal");
    }

    [Fact]
    public void LargeAntiAliasedCircle_IsAccurateAndSmoothAtHighZoom()
    {
        const int size = 120;
        const double cx = 60.3, cy = 59.7, radius = 40;
        var image = Render(size, size, (x, y) => Sq(x - cx) + Sq(y - cy) <= radius * radius, samples: 8);

        var result = BitmapTracer.Trace(image, Options(size));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        var flattened = subpath.Flatten(0.01);
        var centreY = size - cy;
        Assert.True(flattened.Max(p => Math.Abs(Math.Sqrt(Sq(p.X - cx) + Sq(p.Y - centreY)) - radius)) <= 0.25,
            "radial deviation exceeds 0.25 px");

        // No visible facet: consecutive flattened edges turn by a few degrees at most. (No real corners here.)
        var worstTurn = 0d;
        for (var i = 0; i < flattened.Count; i++)
        {
            var a = flattened[i];
            var b = flattened[(i + 1) % flattened.Count];
            var c = flattened[(i + 2) % flattened.Count];
            var v1x = b.X - a.X;
            var v1y = b.Y - a.Y;
            var v2x = c.X - b.X;
            var v2y = c.Y - b.Y;
            if (v1x * v1x + v1y * v1y < 1e-12 || v2x * v2x + v2y * v2y < 1e-12) continue;
            var turn = Math.Abs(Math.Atan2(v1x * v2y - v1y * v2x, v1x * v2x + v1y * v2y)) * 180 / Math.PI;
            worstTurn = Math.Max(worstTurn, turn);
        }
        Assert.True(worstTurn <= 4, $"edges turn by {worstTurn:0.0} degrees somewhere on a smooth circle");

        // Junction nodes are smooth: the two handles lie on one line through the anchor.
        foreach (var node in subpath.Nodes.Where(n => n.HandleIn is not null && n.HandleOut is not null))
        {
            var inX = node.Anchor.X - node.HandleIn!.Value.X;
            var inY = node.Anchor.Y - node.HandleIn!.Value.Y;
            var outX = node.HandleOut!.Value.X - node.Anchor.X;
            var outY = node.HandleOut!.Value.Y - node.Anchor.Y;
            var angle = Math.Abs(Math.Atan2(inX * outY - inY * outX, inX * outX + inY * outY)) * 180 / Math.PI;
            Assert.True(angle <= 1, $"handles at a junction differ by {angle:0.0} degrees");
        }
    }

    [Fact]
    public void HardEdgedSlantedBar_KeepsFourCornersAndStraightEdges()
    {
        var bar = RotatedRectangle(70, 50, 60, 8, 20);
        var image = Render(140, 100, (x, y) => Contains(bar, x, y), samples: 1);

        var result = BitmapTracer.Trace(image, Options(140));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.All(subpath.Flatten(0.005), p => Assert.True(DistanceToPolygon(bar, p.X, 100 - p.Y) <= 1.0));
    }

    [Fact]
    public void NotchedVerticalEdge_BecomesOneStraightSegment()
    {
        var random = new Random(7);
        var notch = new int[100];
        for (var y = 26; y < 75; y++) notch[y] = random.NextDouble() < 0.35 ? (random.Next(2) * 2 - 1) : 0;

        var image = Render(100, 100, (x, y) =>
        {
            var row = (int)Math.Floor(y);
            return x >= 20 + notch[row] && x <= 60 && y >= 20 && y <= 80;
        }, samples: 1);

        var result = BitmapTracer.Trace(image, Options(100));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.True(subpath.Nodes.Count <= 8, $"expected a rectangle, got {subpath.Nodes.Count} nodes");
        Assert.True(subpath.Nodes.Count(n => n.Anchor.X < 22) <= 4, "the notched edge must stay a few long segments");
        Assert.All(subpath.Flatten(0.005), p =>
        {
            Assert.InRange(p.X, 20 - 1.0, 60 + 1.0);
            Assert.InRange(100 - p.Y, 20 - 1.0, 80 + 1.0);
        });
    }

    [Fact]
    public void HardEdgedSquare_KeepsItsCornersExact()
    {
        var image = Render(100, 100, (x, y) => x >= 30 && x <= 70 && y >= 30 && y <= 70, samples: 1);

        var result = BitmapTracer.Trace(image, Options(100));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count);
        foreach (var (x, y) in new[] { (30.0, 30.0), (70.0, 30.0), (70.0, 70.0), (30.0, 70.0) })
            Assert.Contains(subpath.Nodes, n => Math.Sqrt(Sq(n.Anchor.X - x) + Sq(n.Anchor.Y - (100 - y))) <= 0.3);
    }

    // ------------------------------------------------------------------ outlined letters

    [Theory]
    [InlineData(1, 0.05)]
    [InlineData(8, 0.03)]
    public void OutlinedO_TwoSmoothContoursWithNoStraySegments(int samples, double areaTolerance)
    {
        var boundary = new List<(Pt, Pt)>();
        boundary.AddRange(Circle(60, 60, 30, 0, 360));
        boundary.AddRange(Circle(60, 60, 27, 0, 360));
        var idealArea = Math.PI * (30 * 30 - 27 * 27);

        var result = Trace(120, 120, (x, y) =>
        {
            var d2 = Sq(x - 60) + Sq(y - 60);
            return d2 <= 900 && d2 >= 729;
        }, samples);

        AssertHugsOutline(result, boundary, 120, expectedSubpaths: 2, idealArea, areaTolerance);
    }

    [Theory]
    [InlineData(1, 0.05)]
    [InlineData(8, 0.03)]
    public void OutlinedC_OneSmoothContourWithNoStraySegments(int samples, double areaTolerance)
    {
        const double gap = 35;
        var boundary = new List<(Pt, Pt)>();
        boundary.AddRange(Circle(60, 60, 30, gap, 360 - gap));
        boundary.AddRange(Circle(60, 60, 27, gap, 360 - gap));
        foreach (var degrees in new[] { gap, 360 - gap })
        {
            var cos = Math.Cos(degrees * Math.PI / 180);
            var sin = Math.Sin(degrees * Math.PI / 180);
            boundary.Add((new Pt(60 + 27 * cos, 60 + 27 * sin), new Pt(60 + 30 * cos, 60 + 30 * sin)));
        }
        var idealArea = (1 - 2 * gap / 360) * Math.PI * (30 * 30 - 27 * 27);

        var result = Trace(120, 120, (x, y) =>
        {
            var d2 = Sq(x - 60) + Sq(y - 60);
            var angle = Math.Abs(Math.Atan2(y - 60, x - 60)) * 180 / Math.PI;
            return d2 <= 900 && d2 >= 729 && angle > gap;
        }, samples);

        AssertHugsOutline(result, boundary, 120, expectedSubpaths: 1, idealArea, areaTolerance);
    }

    [Theory]
    [InlineData(1, 0.06)]
    [InlineData(8, 0.04)]
    public void OutlinedA_TwoContoursWithSharpApexAndNoStraySegments(int samples, double areaTolerance)
    {
        Pt[] outer = [new(60, 10), new(105, 100), new(15, 100)];
        var centroid = new Pt((60 + 105 + 15) / 3.0, (10 + 100 + 100) / 3.0);
        var inner = outer.Select(p => new Pt(centroid.X + (p.X - centroid.X) * 0.82, centroid.Y + (p.Y - centroid.Y) * 0.82)).ToArray();
        var boundary = Edges(outer).Concat(Edges(inner)).ToList();
        var idealArea = PolygonArea(outer) - PolygonArea(inner);

        var result = Trace(120, 120, (x, y) => Contains(outer, x, y) && !Contains(inner, x, y), samples);

        AssertHugsOutline(result, boundary, 120, expectedSubpaths: 2, idealArea, areaTolerance);
    }

    /// <summary>Every flattened point and every segment midpoint, every anchor and every handle must be
    /// within 1.5 px of the ideal outline (a chord across the letter or a far corner snap fails this), and
    /// the filled area must match the ideal.</summary>
    private static void AssertHugsOutline(
        BitmapTraceResult result, List<(Pt A, Pt B)> boundary, int height, int expectedSubpaths, double idealArea, double areaTolerance)
    {
        const double reach = 1.5;
        var path = Assert.Single(result.VectorPaths);
        Assert.Equal(expectedSubpaths, path.Path.Subpaths.Count);

        var minX = boundary.Min(e => Math.Min(e.A.X, e.B.X)) - reach;
        var maxX = boundary.Max(e => Math.Max(e.A.X, e.B.X)) + reach;
        var minY = boundary.Min(e => Math.Min(e.A.Y, e.B.Y)) - reach;
        var maxY = boundary.Max(e => Math.Max(e.A.Y, e.B.Y)) + reach;

        var signedAreaSum = 0d;
        foreach (var subpath in path.Path.Subpaths)
        {
            foreach (var node in subpath.Nodes)
            {
                foreach (var position in new[] { node.Anchor, node.HandleIn, node.HandleOut }.Where(p => p is not null).Select(p => p!.Value))
                {
                    Assert.InRange(position.X, minX, maxX);
                    Assert.InRange(height - position.Y, minY, maxY);
                }
            }

            var points = subpath.Flatten(0.005);
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                foreach (var t in new[] { 0.0, 0.5 })
                {
                    var x = a.X + (b.X - a.X) * t;
                    var y = height - (a.Y + (b.Y - a.Y) * t);
                    var distance = boundary.Min(e => DistanceToSegment(e.A, e.B, x, y));
                    Assert.True(distance <= reach, $"output passes ({x:0.0}, {y:0.0}), {distance:0.0} px from the outline");
                }
            }

            var area = 0d;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            signedAreaSum += area / 2;
        }

        Assert.InRange(Math.Abs(signedAreaSum), idealArea * (1 - areaTolerance), idealArea * (1 + areaTolerance));
    }

    // ------------------------------------------------------------------ end-of-pipeline guard: 0, 1, 2 corners

    [Theory]
    [InlineData("circle", 1)]
    [InlineData("teardrop", 1)]
    [InlineData("lens", 1)]
    [InlineData("circle", 8)]
    [InlineData("teardrop", 8)]
    [InlineData("lens", 8)]
    public void FilledShapesWithZeroOneAndTwoCorners_NoOutputPointStrayBeyondTwoAndAHalfPixels(string shape, int samples)
    {
        Pt[] polygon = shape switch
        {
            "circle" => Arc(60, 60, 40, 0, 360, 2).ToArray(),
            "teardrop" => new[] { new Pt(60, 10) }.Concat(Arc(60, 70, 30, -30, 210, 1)).ToArray(),
            _ => LensPolygon(),
        };

        var result = BitmapTracer.Trace(Render(120, 120, (x, y) => Contains(polygon, x, y), samples), Options(120));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        var flattened = subpath.Flatten(0.005);
        var area = 0d;
        for (var i = 0; i < flattened.Count; i++)
        {
            var a = flattened[i];
            var b = flattened[(i + 1) % flattened.Count];
            foreach (var t in new[] { 0.0, 0.5 })
            {
                var x = a.X + (b.X - a.X) * t;
                var y = 120 - (a.Y + (b.Y - a.Y) * t);
                var distance = DistanceToPolygon(polygon, x, y);
                Assert.True(distance <= 2.5, $"{shape}: output passes ({x:0.0}, {y:0.0}), {distance:0.0} px from the artwork");
            }
            area += a.X * b.Y - b.X * a.Y;
        }
        Assert.InRange(Math.Abs(area / 2), PolygonArea(polygon) * 0.97, PolygonArea(polygon) * 1.03);
    }

    private static IEnumerable<Pt> Arc(double cx, double cy, double radius, double fromDegrees, double toDegrees, double stepDegrees)
    {
        for (var d = fromDegrees; d < toDegrees + 1e-9; d += stepDegrees)
            yield return new Pt(cx + radius * Math.Cos(d * Math.PI / 180), cy + radius * Math.Sin(d * Math.PI / 180));
    }

    private static Pt[] LensPolygon()
    {
        var phi = Math.Acos(20.0 / 30.0) * 180 / Math.PI;
        return Arc(40, 60, 30, -phi, phi, 1).Concat(Arc(80, 60, 30, 180 - phi, 180 + phi, 1)).ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private readonly record struct Pt(double X, double Y);

    private static BitmapTraceResult Trace(int size, int height, Func<double, double, bool> inside, int samples) =>
        BitmapTracer.Trace(Render(size, height, inside, samples), Options(size));

    private static BitmapTraceOptions Options(int widthPx) => new() { TargetWidthMm = widthPx };

    private static double Sq(double v) => v * v;

    private static GrayscaleImage Render(int width, int height, Func<double, double, bool> inside, int samples)
    {
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                        if (inside(x + (sx + 0.5) / samples, y + (sy + 0.5) / samples)) hits++;
                data[y * width + x] = (byte)Math.Round(255 - 255.0 * hits / (samples * samples));
            }
        }
        return new GrayscaleImage { Width = width, Height = height, Luminance = data };
    }

    private static IEnumerable<(Pt, Pt)> Circle(double cx, double cy, double radius, double fromDegrees, double toDegrees)
    {
        const int steps = 180;
        for (var i = 0; i < steps; i++)
        {
            var a0 = (fromDegrees + (toDegrees - fromDegrees) * i / steps) * Math.PI / 180;
            var a1 = (fromDegrees + (toDegrees - fromDegrees) * (i + 1) / steps) * Math.PI / 180;
            yield return (new Pt(cx + radius * Math.Cos(a0), cy + radius * Math.Sin(a0)),
                          new Pt(cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1)));
        }
    }

    private static IEnumerable<(Pt, Pt)> Edges(Pt[] polygon)
    {
        for (var i = 0; i < polygon.Length; i++) yield return (polygon[i], polygon[(i + 1) % polygon.Length]);
    }

    private static double PolygonArea(Pt[] polygon)
    {
        var area = 0d;
        foreach (var (a, b) in Edges(polygon)) area += a.X * b.Y - b.X * a.Y;
        return Math.Abs(area) / 2;
    }

    private static Pt[] RotatedRectangle(double cx, double cy, double length, double width, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        (double X, double Y)[] local = [(-length / 2, -width / 2), (length / 2, -width / 2), (length / 2, width / 2), (-length / 2, width / 2)];
        return local.Select(p => new Pt(cx + p.X * cos - p.Y * sin, cy + p.X * sin + p.Y * cos)).ToArray();
    }

    private static bool Contains(Pt[] polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].Y > y) != (polygon[j].Y > y) &&
                x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToPolygon(Pt[] polygon, double x, double y) =>
        Edges(polygon).Min(e => DistanceToSegment(e.Item1, e.Item2, x, y));

    private static double DistanceToSegment(Pt a, Pt b, double x, double y)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSquared = abx * abx + aby * aby;
        var t = lengthSquared < 1e-12 ? 0 : Math.Clamp(((x - a.X) * abx + (y - a.Y) * aby) / lengthSquared, 0, 1);
        return Math.Sqrt(Sq(x - (a.X + abx * t)) + Sq(y - (a.Y + aby * t)));
    }
}
