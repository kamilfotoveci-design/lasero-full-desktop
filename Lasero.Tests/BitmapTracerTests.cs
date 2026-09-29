using System.Drawing;
using System.IO;
using Lasero.Core.Grbl;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.Tests;

public sealed class BitmapTracerTests
{
    [Fact]
    public void Trace_FilledRectangle_ProducesSingleFourCornerSubpath()
    {
        var path = CreateBitmap(80, 60, graphics => graphics.FillRectangle(Brushes.Black, 10, 12, 40, 28));
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 80,
                MinimumFeaturePixels = 1,
            });

            Assert.Single(result.VectorPaths);
            var traced = result.VectorPaths[0];
            Assert.Single(traced.Path.Subpaths);
            var subpath = traced.Path.Subpaths[0];
            Assert.True(subpath.IsClosed);
            Assert.Equal(4, subpath.Nodes.Count);
            foreach (var node in subpath.Nodes)
            {
                Assert.Equal(VectorNodeType.Corner, node.Type);
                Assert.Null(node.HandleIn);
                Assert.Null(node.HandleOut);
            }

            Assert.Equal(80, result.WidthMm, 6);
            Assert.Equal(60, result.HeightMm, 6);
            // Document is still populated (the flattened legacy view Lasero.Avalonia and the live
            // preview still read) — no regression there either.
            Assert.Single(result.Document.Shapes);
            Assert.True(result.Document.Shapes[0].IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_RemovesIsolatedNoiseBelowMinimumFeatureSize()
    {
        var path = CreateBitmap(32, 32, graphics =>
        {
            graphics.FillRectangle(Brushes.Black, 3, 3, 1, 1);
            graphics.FillRectangle(Brushes.Black, 10, 10, 8, 8);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 32,
                MinimumFeaturePixels = 4,
                NoiseRemoval = 0,
            });

            Assert.Single(result.VectorPaths);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_FilledCircle_CollapsesRawContourDensityIntoAFewSmoothNodes()
    {
        var path = CreateBitmap(200, 200, graphics => graphics.FillEllipse(Brushes.Black, 40, 40, 120, 120));
        try
        {
            var options = new BitmapTraceOptions { TargetWidthMm = 200, MinimumFeaturePixels = 1 };

            var grayscale = BitmapLoader.LoadGrayscale(path);
            var rawTree = ContourExtractor.Extract(grayscale, options);
            var rawPointCount = Assert.Single(rawTree.Roots).Points.Count;

            var result = BitmapTracer.Trace(path, options);
            Assert.Single(result.VectorPaths);
            var subpath = Assert.Single(result.VectorPaths[0].Path.Subpaths);
            Assert.True(subpath.IsClosed);

            // The raw pixel-walk contour has roughly one point per boundary pixel (~hundreds for a
            // 120px-wide circle); the fitted curve should collapse that to a handful of smooth nodes.
            Assert.True(rawPointCount > 100, $"expected a dense raw contour, got {rawPointCount} points");
            Assert.True(subpath.Nodes.Count < 20, $"expected < 20 fitted nodes, got {subpath.Nodes.Count}");
            Assert.All(subpath.Nodes, node => Assert.True(node.HasAnyHandle || subpath.Nodes.Count <= 2));

            // A smooth path is useful only if it still hugs the bitmap. The source disc is centered
            // at (100,100) with a 60 px radius; allow for its antialiased pixel boundary.
            var radialErrors = subpath.Flatten(0.01).Select(point =>
                Math.Abs(Distance(point, new Position(100, 100, 0)) - 60)).ToList();
            Assert.True(radialErrors.Max() < 3,
                $"trace moved too far from the source circle: max radial error {radialErrors.Max():0.###} px");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_LetterOShape_ProducesTwoOppositelyWoundSubpathsThatReadCorrectlyUnderNonzeroFill()
    {
        // A filled outer disc with a smaller disc of background punched out of its middle — the
        // simplest possible "outer contour plus one hole" compound path.
        const int size = 200;
        const double centerPx = size / 2.0;
        const double outerRadiusPx = 70;
        const double innerRadiusPx = 35;
        var path = CreateBitmap(size, size, graphics =>
        {
            graphics.FillEllipse(Brushes.Black, (float)(centerPx - outerRadiusPx), (float)(centerPx - outerRadiusPx), (float)(outerRadiusPx * 2), (float)(outerRadiusPx * 2));
            graphics.FillEllipse(Brushes.White, (float)(centerPx - innerRadiusPx), (float)(centerPx - innerRadiusPx), (float)(innerRadiusPx * 2), (float)(innerRadiusPx * 2));
        });
        try
        {
            // 1 mm per source pixel keeps the math below trivial to reason about.
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = size, MinimumFeaturePixels = 1 });

            Assert.Single(result.VectorPaths);
            var subpaths = result.VectorPaths[0].Path.Subpaths;
            Assert.Equal(2, subpaths.Count);

            var areas = subpaths.Select(SignedArea).ToList();
            Assert.True(Math.Sign(areas[0]) != Math.Sign(areas[1]),
                $"expected opposite winding, got signed areas {areas[0]} and {areas[1]}");

            // Y is flipped to mm-up during tracing (see CompoundPathBuilder.ToMillimeters), so the
            // pixel-space center/radii map 1:1 onto the same mm coordinates here (scale == 1, and the
            // vertical flip does not move the shape's own center).
            var center = new Position(centerPx, size - centerPx, 0);
            var polygons = subpaths.Select(sp => sp.Flatten()).ToList();

            var ringPoint = Offset(center, (outerRadiusPx + innerRadiusPx) / 2, 0);
            var holePoint = center;
            var outsidePoint = Offset(center, outerRadiusPx * 1.5, 0);

            Assert.NotEqual(0, TotalWindingNumber(polygons, ringPoint));
            Assert.Equal(0, TotalWindingNumber(polygons, holePoint));
            Assert.Equal(0, TotalWindingNumber(polygons, outsidePoint));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_ShapeWithTwoHoles_ProducesOneOuterAndTwoHoleSubpathsAllCorrectlyWound()
    {
        const int width = 240;
        const int height = 160;
        var path = CreateBitmap(width, height, graphics =>
        {
            graphics.FillRectangle(Brushes.Black, 10, 10, width - 20, height - 20);
            graphics.FillRectangle(Brushes.White, 40, 40, 50, 80);
            graphics.FillRectangle(Brushes.White, 150, 40, 50, 80);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = width, MinimumFeaturePixels = 1 });

            Assert.Single(result.VectorPaths);
            var subpaths = result.VectorPaths[0].Path.Subpaths;
            Assert.Equal(3, subpaths.Count);

            var areas = subpaths.Select(SignedArea).ToList();
            var outerCount = areas.Count(a => a > 0);
            var holeCount = areas.Count(a => a < 0);
            // Whichever sign the outer contour landed on, exactly one subpath should carry it and the
            // other two (the holes) the opposite sign.
            Assert.True((outerCount == 1 && holeCount == 2) || (outerCount == 2 && holeCount == 1));

            var polygons = subpaths.Select(sp => sp.Flatten()).ToList();
            Assert.NotEqual(0, TotalWindingNumber(polygons, new Position(width / 2.0, height / 2.0, 0)));
            Assert.Equal(0, TotalWindingNumber(polygons, new Position(65, height / 2.0, 0)));
            Assert.Equal(0, TotalWindingNumber(polygons, new Position(175, height / 2.0, 0)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_SharpTriangle_PreservesCornersWithoutSmoothing()
    {
        const int size = 100;
        (float X, float Y)[] vertices = [(50, 10), (90, 85), (10, 85)];
        var path = CreateBitmap(size, size, graphics =>
            graphics.FillPolygon(Brushes.Black, vertices.Select(v => new PointF(v.X, v.Y)).ToArray()));
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = size, MinimumFeaturePixels = 1 });

            Assert.Single(result.VectorPaths);
            var subpath = Assert.Single(result.VectorPaths[0].Path.Subpaths);

            // Heavily simplified compared to the raw pixel walk (several hundred points for a triangle
            // this size) — a little headroom above the ideal 3 for pixel-rasterization artifacts at
            // the apexes (a rasterized sharp point is rarely a single pixel).
            Assert.True(subpath.Nodes.Count <= 8, $"expected a heavily simplified triangle, got {subpath.Nodes.Count} nodes");

            // Every rasterized vertex must have a sharp (Corner-typed) node close to it — no curve
            // smoothing across any of the triangle's points.
            foreach (var vertex in vertices)
            {
                var expected = new Position(vertex.X, size - vertex.Y, 0);
                var hasNearbyCorner = subpath.Nodes.Any(node =>
                    node.Type == VectorNodeType.Corner && Distance(node.Anchor, expected) < 5);
                Assert.True(hasNearbyCorner, $"expected a Corner-typed node near {expected}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_TwoDisconnectedRectangles_ProducesTwoSeparateTracedObjects()
    {
        var path = CreateBitmap(120, 60, graphics =>
        {
            graphics.FillRectangle(Brushes.Black, 5, 5, 30, 30);
            graphics.FillRectangle(Brushes.Black, 80, 20, 30, 30);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = 120, MinimumFeaturePixels = 1 });

            Assert.Equal(2, result.VectorPaths.Count);
            Assert.All(result.VectorPaths, traced => Assert.Single(traced.Path.Subpaths));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ThresholdMode.Manual)]
    [InlineData(ThresholdMode.Auto)]
    [InlineData(ThresholdMode.Adaptive)]
    public void Trace_EachThresholdMode_ProducesASaneResultOnASimpleFixture(ThresholdMode mode)
    {
        var path = CreateBitmap(100, 100, graphics => graphics.FillEllipse(Brushes.Black, 20, 20, 60, 60));
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 100,
                MinimumFeaturePixels = 1,
                ThresholdMode = mode,
            });

            Assert.NotEmpty(result.VectorPaths);
            Assert.NotEmpty(result.Document.Shapes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_OutlineMode_ProducesNonEmptyContourGeometry()
    {
        var path = CreateBitmap(120, 120, graphics =>
        {
            graphics.FillRectangle(Brushes.Black, 20, 20, 40, 40);
            graphics.FillEllipse(Brushes.Black, 70, 60, 35, 35);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 120,
                MinimumFeaturePixels = 1,
                Mode = TraceMode.Outline,
            });

            Assert.NotEmpty(result.VectorPaths);
            Assert.True(result.PointCount > 0);
            Assert.NotEmpty(result.Document.Shapes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_ColorBlocks_ProducesEditableFilledLayersAndDisablesWhiteBackground()
    {
        if (!VTracerColorTracer.IsAvailable) return;
        var path = CreateBitmap(120, 80, graphics =>
        {
            graphics.FillRectangle(Brushes.Red, 10, 12, 35, 50);
            graphics.FillRectangle(Brushes.Blue, 70, 12, 35, 50);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                Mode = TraceMode.Color,
                TargetWidthMm = 120,
                MinimumFeaturePixels = 1,
            });

            Assert.NotEmpty(result.VectorPaths);
            Assert.True(result.Document.Layers.Count >= 2);
            Assert.All(result.Document.Layers, layer => Assert.Equal(Lasero.Core.Layers.LayerMode.Fill, layer.Mode));
            Assert.All(result.Document.Shapes, shape => Assert.Contains(result.Document.Layers,
                layer => layer.Id == shape.LayerId && layer.Color == shape.LayerColor));
            Assert.All(result.VectorPaths, item => Assert.All(item.Path.Subpaths,
                subpath => Assert.True(subpath.IsClosed && subpath.Nodes.Count >= 3)));
            Assert.Contains(result.Document.Layers, layer => !layer.IsEnabled &&
                layer.Color.R >= 245 && layer.Color.G >= 245 && layer.Color.B >= 245);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_BlankWhiteImage_ReturnsEmptyResultWithoutThrowing()
    {
        var path = CreateBitmap(40, 40, _ => { }); // stays fully white (the default background)
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = 40 });

            Assert.Empty(result.VectorPaths);
            Assert.Empty(result.Document.Shapes);
            Assert.Equal(0, result.ContourCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_FullyBlackImage_ReturnsEmptyResultWithoutThrowing()
    {
        var path = CreateBitmap(40, 40, graphics => graphics.FillRectangle(Brushes.Black, 0, 0, 40, 40));
        try
        {
            // A fully-inked canvas is degenerate the same way a fully-blank one is: there is no
            // distinguishable "ink vs. background" boundary to trace. Invert=true asks the tracer to
            // treat *light* pixels as foreground, so a fully-black source correctly has nothing to
            // find (a fully-black source under the default dark-is-foreground convention would instead
            // legitimately trace one giant frame-sized rectangle, which is not what this case is
            // testing — see Trace_BlankWhiteImage_ReturnsEmptyResultWithoutThrowing for that half).
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = 40, Invert = true });

            Assert.Empty(result.VectorPaths);
            Assert.Empty(result.Document.Shapes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_InvalidOptions_ThrowsInsteadOfReturningAnEmptyResult()
    {
        var path = CreateBitmap(20, 20, graphics => graphics.FillRectangle(Brushes.Black, 2, 2, 10, 10));
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = 20, Detail = 2 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BitmapTracer.Trace(path, new BitmapTraceOptions { TargetWidthMm = 0 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateBitmap(int width, int height, Action<Graphics> draw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-trace-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        draw(graphics);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static Position Offset(Position center, double dx, double dy) => new(center.X + dx, center.Y + dy, 0);

    private static double Distance(Position a, Position b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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

    /// <summary>Sum of the winding number contributed by every subpath's flattened polygon at
    /// <paramref name="point"/> — the same FillRule.Nonzero test SceneCanvas.xaml.cs's
    /// BuildCompoundGeometry relies on conceptually, reimplemented directly here (Dan Sunday's
    /// standard winding-number-for-a-point-in-polygon algorithm) so this test does not need WPF.</summary>
    private static int TotalWindingNumber(IReadOnlyList<IReadOnlyList<Position>> polygons, Position point) =>
        polygons.Sum(polygon => WindingNumber(polygon, point));

    private static int WindingNumber(IReadOnlyList<Position> polygon, Position point)
    {
        var winding = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && IsLeft(a, b, point) > 0) winding++;
            }
            else
            {
                if (b.Y <= point.Y && IsLeft(a, b, point) < 0) winding--;
            }
        }
        return winding;
    }

    private static double IsLeft(Position a, Position b, Position p) =>
        (b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y);
}
