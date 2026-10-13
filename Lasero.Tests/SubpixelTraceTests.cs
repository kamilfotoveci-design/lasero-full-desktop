using Lasero.Core.Grbl;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.Tests;

/// <summary>
/// The grayscale threshold trace (sub-pixel marching squares, corner snapping, arc smoothing, Bezier fit)
/// on synthetic images with known geometry, so edge accuracy can be asserted against the true edge.
/// Images are rendered with 8x8 supersampled coverage (anti-aliased) or with a single centre sample
/// (hard edged). One source pixel is one millimetre (TargetWidthMm = width); mm y is image y flipped.
/// </summary>
public sealed class SubpixelTraceTests
{
    [Fact]
    public void FilledCircle_FollowsTheTrueEdgeWithAFewSmoothNodes()
    {
        const int size = 200;
        const double cx = 100.3, cy = 99.7, radius = 60.4;
        var image = Render(size, size, (x, y) => Sq(x - cx) + Sq(y - cy) <= radius * radius);

        var result = BitmapTracer.Trace(image, Options(size));

        var path = Assert.Single(result.VectorPaths);
        var subpath = Assert.Single(path.Path.Subpaths);
        Assert.True(subpath.Nodes.Count < 16, $"expected a handful of nodes, got {subpath.Nodes.Count}");
        Assert.All(subpath.Nodes, node => Assert.True(node.HasAnyHandle));

        var points = subpath.Flatten(0.005);
        var centreMmY = size - cy;
        Assert.True(points.Max(p => Math.Abs(Math.Sqrt(Sq(p.X - cx) + Sq(p.Y - centreMmY)) - radius)) < 0.25,
            "radial error against the true circle exceeds 0.25 px");
        Assert.InRange(points.Min(p => p.X), cx - radius - 0.25, cx - radius + 0.25);
        Assert.InRange(points.Max(p => p.X), cx + radius - 0.25, cx + radius + 0.25);
        Assert.InRange(points.Min(p => p.Y), centreMmY - radius - 0.25, centreMmY - radius + 0.25);
        Assert.InRange(points.Max(p => p.Y), centreMmY + radius - 0.25, centreMmY + radius + 0.25);
    }

    [Fact]
    public void Ring_KeepsItsHoleAndBothEdgesAreExact()
    {
        const int size = 200;
        const double centre = 100, outer = 70, inner = 35.5;
        var image = Render(size, size, (x, y) =>
        {
            var d2 = Sq(x - centre) + Sq(y - centre);
            return d2 <= outer * outer && d2 >= inner * inner;
        });

        var result = BitmapTracer.Trace(image, Options(size));

        var path = Assert.Single(result.VectorPaths);
        var subpaths = path.Path.Subpaths;
        Assert.Equal(2, subpaths.Count);
        Assert.All(subpaths, subpath => Assert.True(subpath.Nodes.Count < 16));

        var areas = subpaths.Select(SignedArea).ToList();
        Assert.NotEqual(Math.Sign(areas[0]), Math.Sign(areas[1]));

        var polygons = subpaths.Select(subpath => subpath.Flatten(0.005)).ToList();
        var centreMm = new Position(centre, size - centre, 0);
        Assert.NotEqual(0, TotalWinding(polygons, new Position(centre + 52, centreMm.Y, 0)));
        Assert.Equal(0, TotalWinding(polygons, centreMm));
        Assert.Equal(0, TotalWinding(polygons, new Position(centre + 90, centreMm.Y, 0)));

        var radii = polygons.Select(polygon => polygon.Average(p => Math.Sqrt(Sq(p.X - centre) + Sq(p.Y - centreMm.Y)))).OrderBy(r => r).ToList();
        Assert.InRange(radii[0], inner - 0.25, inner + 0.25);
        Assert.InRange(radii[1], outer - 0.25, outer + 0.25);
    }

    [Fact]
    public void Rectangle_KeepsFourSharpCornersOnTheTrueEdges()
    {
        const int width = 120, height = 80;
        const double left = 20.25, right = 60.75, top = 12.5, bottom = 41.5;
        var image = Render(width, height, (x, y) => x >= left && x <= right && y >= top && y <= bottom);

        var result = BitmapTracer.Trace(image, Options(width));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.All(subpath.Nodes, node =>
        {
            Assert.Equal(VectorNodeType.Corner, node.Type);
            Assert.Null(node.HandleIn);
            Assert.Null(node.HandleOut);
        });
        foreach (var (x, y) in new[] { (left, top), (right, top), (right, bottom), (left, bottom) })
        {
            var expected = new Position(x, height - y, 0);
            Assert.Contains(subpath.Nodes, node => Distance(node.Anchor, expected) < 0.15);
        }
    }

    [Fact]
    public void HardEdgedRectangle_HasNoHalfPixelOffset()
    {
        const int width = 80, height = 60;
        var image = Render(width, height, (x, y) => x >= 10 && x <= 50 && y >= 12 && y <= 40, samples: 1);

        var result = BitmapTracer.Trace(image, Options(width));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.InRange(subpath.Nodes.Min(n => n.Anchor.X), 10 - 0.02, 10 + 0.02);
        Assert.InRange(subpath.Nodes.Max(n => n.Anchor.X), 50 - 0.02, 50 + 0.02);
        Assert.InRange(subpath.Nodes.Min(n => n.Anchor.Y), height - 40 - 0.02, height - 40 + 0.02);
        Assert.InRange(subpath.Nodes.Max(n => n.Anchor.Y), height - 12 - 0.02, height - 12 + 0.02);
    }

    [Fact]
    public void Cutoff_IgnoresPixelsBrighterThanItWhileThresholdWouldKeepThem()
    {
        const int size = 100;
        var image = Render(size, size, (x, y) => x is >= 10 and <= 40 && y is >= 10 and <= 40, samples: 1, ink: 0);
        var gray = Render(size, size, (x, y) => x is >= 60 and <= 90 && y is >= 60 and <= 90, samples: 1, ink: 100);
        var combined = new byte[size * size];
        for (var i = 0; i < combined.Length; i++) combined[i] = Math.Min(image.Luminance[i], gray.Luminance[i]);
        var both = new GrayscaleImage { Width = size, Height = size, Luminance = combined };

        Assert.Equal(2, BitmapTracer.Trace(both, Options(size)).VectorPaths.Count);
        Assert.Single(BitmapTracer.Trace(both, Options(size) with { Cutoff = 50 }).VectorPaths);
    }

    [Fact]
    public void IgnoreLessThan_DropsSmallShapesOnly()
    {
        const int size = 100;
        var image = Render(size, size, (x, y) =>
            (x is >= 10 and <= 40 && y is >= 10 and <= 40) || (x is >= 70 and < 72 && y is >= 70 and < 72), samples: 1);

        Assert.Equal(2, BitmapTracer.Trace(image, Options(size) with { IgnoreLessThanPixels = 2 }).VectorPaths.Count);
        Assert.Single(BitmapTracer.Trace(image, Options(size) with { IgnoreLessThanPixels = 10 }).VectorPaths);
    }

    [Fact]
    public void DeleteBoundary_DropsTheFrameAlongTheImageBorderAndKeepsWhatIsInsideIt()
    {
        const int size = 100;
        var image = Render(size, size, (x, y) =>
            x < 4 || x >= size - 4 || y < 4 || y >= size - 4 || (x is >= 40 and < 60 && y is >= 40 and < 60), samples: 1);

        var kept = BitmapTracer.Trace(image, Options(size));
        Assert.Equal(3, Assert.Single(kept.VectorPaths).Path.Subpaths.Count);

        var withoutFrame = BitmapTracer.Trace(image, Options(size) with { DeleteBoundary = true });
        Assert.Single(Assert.Single(withoutFrame.VectorPaths).Path.Subpaths);
    }

    [Fact]
    public void ShapeTouchingTheImageBorderClosesExactlyOnTheBorder()
    {
        const int size = 60;
        var image = Render(size, size, (x, y) => x >= 0 && x <= 30 && y >= 0 && y <= 30, samples: 1);

        var result = BitmapTracer.Trace(image, Options(size));

        var subpath = Assert.Single(Assert.Single(result.VectorPaths).Path.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.InRange(subpath.Nodes.Min(n => n.Anchor.X), -0.02, 0.02);
        Assert.InRange(subpath.Nodes.Max(n => n.Anchor.Y), size - 0.02, size + 0.02);
    }


    // ------------------------------------------------------------------ polygons: every node stays on the edge

    [Fact]
    public void ThinAngledBar_StaysOnItsEdgesWithoutOvershooting()
    {
        var bar = RotatedRectangle(60, 50, 50, 3.5, 30);
        AssertTraceHugsPolygon(bar, 120, 100, tolerance: 0.7);
    }

    [Fact]
    public void Cross_StaysOnItsEdgesWithoutOvershooting()
    {
        Pt[] cross =
        [
            new(30, 54), new(54, 54), new(54, 30), new(66, 30), new(66, 54), new(90, 54),
            new(90, 66), new(66, 66), new(66, 90), new(54, 90), new(54, 66), new(30, 66),
        ];
        AssertTraceHugsPolygon(cross, 120, 120, tolerance: 0.4);
    }

    [Fact]
    public void RotatedSquare_StaysOnItsEdgesWithoutOvershooting()
    {
        var diamond = RotatedRectangle(50, 50, 40, 40, 45);
        AssertTraceHugsPolygon(diamond, 100, 100, tolerance: 0.4);
    }

    [Fact]
    public void AcuteWedge_TipDoesNotOvershoot()
    {
        Pt[] wedge = [new(15, 50), new(70, 38), new(70, 62)];
        AssertTraceHugsPolygon(wedge, 100, 100, tolerance: 1.0);
    }

    private readonly record struct Pt(double X, double Y);

    private static Pt[] RotatedRectangle(double cx, double cy, double length, double width, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        (double X, double Y)[] local = [(-length / 2, -width / 2), (length / 2, -width / 2), (length / 2, width / 2), (-length / 2, width / 2)];
        return local.Select(p => new Pt(cx + p.X * cos - p.Y * sin, cy + p.X * sin + p.Y * cos)).ToArray();
    }

    /// <summary>Renders the polygon anti-aliased, traces it at 1 mm per pixel, and requires that the whole
    /// flattened trace and every anchor and handle stay within the tolerance of the polygon's true boundary and
    /// bounding box (a bulge past a tip would break both).</summary>
    private static void AssertTraceHugsPolygon(Pt[] polygon, int width, int height, double tolerance)
    {
        var image = Render(width, height, (x, y) => PolygonContains(polygon, x, y));
        var result = BitmapTracer.Trace(image, Options(width));

        var path = Assert.Single(result.VectorPaths);
        var subpath = Assert.Single(path.Path.Subpaths);

        var minX = polygon.Min(p => p.X) - tolerance;
        var maxX = polygon.Max(p => p.X) + tolerance;
        var minY = polygon.Min(p => p.Y) - tolerance;
        var maxY = polygon.Max(p => p.Y) + tolerance;
        foreach (var node in subpath.Nodes)
        {
            foreach (var position in new[] { node.Anchor, node.HandleIn, node.HandleOut }.Where(p => p is not null).Select(p => p!.Value))
            {
                var y = height - position.Y;
                Assert.InRange(position.X, minX, maxX);
                Assert.InRange(y, minY, maxY);
            }
        }

        foreach (var point in subpath.Flatten(0.005))
        {
            var distance = DistanceToBoundary(polygon, point.X, height - point.Y);
            Assert.True(distance <= tolerance, $"trace point ({point.X:0.00}, {height - point.Y:0.00}) is {distance:0.00} px from the true edge");
        }
    }

    private static bool PolygonContains(Pt[] polygon, double x, double y)
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

    private static double DistanceToBoundary(Pt[] polygon, double x, double y)
    {
        var best = double.MaxValue;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var t = Math.Clamp(((x - a.X) * abx + (y - a.Y) * aby) / (abx * abx + aby * aby), 0, 1);
            best = Math.Min(best, Math.Sqrt(Sq(x - (a.X + abx * t)) + Sq(y - (a.Y + aby * t))));
        }
        return best;
    }
    // ------------------------------------------------------------------ helpers

    private static BitmapTraceOptions Options(int widthPx) => new() { TargetWidthMm = widthPx };

    private static double Sq(double v) => v * v;

    private static double Distance(Position a, Position b) => Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y));

    private static GrayscaleImage Render(int width, int height, Func<double, double, bool> inside, int samples = 8, int ink = 0)
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
                var coverage = hits / (double)(samples * samples);
                data[y * width + x] = (byte)Math.Round(255 - coverage * (255 - ink));
            }
        }
        return new GrayscaleImage { Width = width, Height = height, Luminance = data };
    }

    private static double SignedArea(VectorSubpath subpath)
    {
        var nodes = subpath.Nodes;
        var area = 0d;
        for (var i = 0; i < nodes.Count; i++)
        {
            var current = nodes[i].Anchor;
            var next = nodes[(i + 1) % nodes.Count].Anchor;
            area += current.X * next.Y - next.X * current.Y;
        }
        return area / 2;
    }

    private static int TotalWinding(IReadOnlyList<IReadOnlyList<Position>> polygons, Position point) =>
        polygons.Sum(polygon => Winding(polygon, point));

    /// <summary>Dan Sunday's winding number of a closed polygon around a point.</summary>
    private static int Winding(IReadOnlyList<Position> polygon, Position point)
    {
        var winding = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var left = (b.X - a.X) * (point.Y - a.Y) - (point.X - a.X) * (b.Y - a.Y);
            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && left > 0) winding++;
            }
            else if (b.Y <= point.Y && left < 0)
            {
                winding--;
            }
        }
        return winding;
    }
}
