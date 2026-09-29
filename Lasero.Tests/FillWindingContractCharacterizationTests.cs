using System.Globalization;
using Lasero.Core.Geometry;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Trace;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Phase 2 (fill / winding contract) CHARACTERISATION tests. See docs/fill-winding-contract.md.
///
/// They were written as characterization of the pre-contract behaviour (toolpath fill = layer-wide
/// EvenOdd, canvas = per-compound-path NonZero). The fill contract (NonZero per group, groups
/// unioned) has since landed in ToolpathBuilder and SceneDocument.ToImportedDocument, so the
/// toolpath/canvas cases are now "Agreement_" tests against the canvas oracle. The tests still named
/// "Disagreement_" (SVG fill-rule, offset) describe producer gaps that later migration steps close.
/// </summary>
public sealed class FillWindingContractCharacterizationTests
{
    private const double ControllerMaximumS = 1000;
    private static readonly RgbColor Black = new(0, 0, 0);
    private const double Interval = 5;

    // ------------------------------------------------------------------ helpers

    private readonly record struct Span(double Y, double X0, double X1);

    private static LayerSettings FillLayer(string name = "Vyplň") => new()
    {
        Color = Black,
        Name = name,
        Mode = LayerMode.Fill,
        Speed = 3000,
        Power = 30,
        Passes = 1,
        FillLineIntervalMm = Interval,
    };

    /// <summary>Counter-clockwise (positive signed area) rectangle, closed with a repeated point.</summary>
    private static ImportedShape Rect(double x0, double y0, double x1, double y1, Guid set, bool clockwise = false,
        Guid layerId = default)
    {
        var ring = new List<Position>
        {
            new(x0, y0, 0), new(x1, y0, 0), new(x1, y1, 0), new(x0, y1, 0), new(x0, y0, 0),
        };
        if (clockwise) ring.Reverse();
        return new ImportedShape
        {
            Points = ring,
            IsClosed = true,
            LayerColor = Black,
            LayerId = layerId,
            PreferredMode = LayerMode.Fill,
            GeometrySetId = set,
        };
    }

    private static List<string> Emit(IReadOnlyList<ImportedShape> shapes, LayerSettings? layer = null)
    {
        layer ??= FillLayer();
        return ToolpathBuilder.BuildGCode(new ImportedDocument
        {
            Shapes = shapes,
            Layers = [layer],
            BoundingBox = new BoundingBox2D(0, 0, 100, 100),
        }, ControllerMaximumS);
    }

    /// <summary>Every engraved run: the G0 start and the following G1 end, normalised to X0 &lt;= X1.</summary>
    private static List<Span> Spans(IEnumerable<string> gcode)
    {
        var result = new List<Span>();
        (double X, double Y)? start = null;
        foreach (var line in gcode)
        {
            if (line.StartsWith("G0 ", StringComparison.Ordinal)) start = Xy(line);
            else if (line.StartsWith("G1 ", StringComparison.Ordinal) && start is { } s)
            {
                var end = Xy(line);
                result.Add(new Span(end.Y, Math.Min(s.X, end.X), Math.Max(s.X, end.X)));
                start = null;
            }
        }
        return result.OrderBy(span => span.Y).ThenBy(span => span.X0).ToList();
    }

    private static (double X, double Y) Xy(string line)
    {
        double Word(char letter)
        {
            var token = line.Split(' ').First(t => t.Length > 1 && t[0] == letter);
            return double.Parse(token[1..], CultureInfo.InvariantCulture);
        }
        return (Word('X'), Word('Y'));
    }

    private static bool Burns(IEnumerable<Span> spans, double x, double y) =>
        spans.Any(span => Math.Abs(span.Y - y) < 1e-6 && x > span.X0 && x < span.X1);

    /// <summary>What SceneCanvas.BuildCompoundGeometry draws (FillRule.Nonzero, one path per
    /// GeometrySetId group, groups painted independently and therefore unioned).</summary>
    private static bool CanvasFills(IEnumerable<ImportedShape> shapes, double x, double y) =>
        shapes.Where(s => s.IsClosed && s.Points.Count >= 3)
            .GroupBy(s => s.GeometrySetId)
            .Any(group => group.Sum(s => WindingContribution(s.Points, x, y)) != 0);

    private static int WindingContribution(IReadOnlyList<Position> ring, double x, double y)
    {
        var winding = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (a.Y <= y)
            {
                if (b.Y > y && Cross(a, b, x, y) > 0) winding++;
            }
            else if (b.Y <= y && Cross(a, b, x, y) < 0) winding--;
        }
        return winding;
    }

    private static double Cross(Position a, Position b, double x, double y) =>
        (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);

    private static double SignedArea(IReadOnlyList<Position> ring)
    {
        double twice = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return twice / 2;
    }

    // ------------------------------------------------------- toolpath fill: current rule

    /// <summary>Agreement case: a declared compound path wound the "correct" way (hole opposite to
    /// outer). EvenOdd and NonZero give the same answer, so the toolpath matches the canvas.</summary>
    [Fact]
    public void Agreement_OppositeWoundHoleInOneSet_IsHollowInToolpathAndCanvas()
    {
        var set = Guid.NewGuid();
        var shapes = new[] { Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set, clockwise: true) };

        var spans = Spans(Emit(shapes));

        Assert.False(Burns(spans, 20, 17.5));
        Assert.True(Burns(spans, 5, 17.5));
        Assert.False(CanvasFills(shapes, 20, 17.5));
    }

    /// <summary>Winding now decides holes: reversing the inner ring of a same-set pair turns a solid
    /// merge into a hole.</summary>
    [Fact]
    public void Contract_WindingDirectionDistinguishesHoleFromMerge()
    {
        var set = Guid.NewGuid();
        var sameWound = Spans(Emit([Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set)]));
        var oppositeWound = Spans(Emit([Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set, clockwise: true)]));

        Assert.True(Burns(sameWound, 20, 17.5));
        Assert.False(Burns(oppositeWound, 20, 17.5));
    }

    /// <summary>Same-wound nested rings in ONE declared set merge. Previously the toolpath left a hole
    /// while the canvas drew it solid; both are now solid.</summary>
    [Fact]
    public void Agreement_SameWoundNestedRingsInOneSet_SolidInToolpathAndCanvas()
    {
        var set = Guid.NewGuid();
        var shapes = new[] { Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set) };

        var spans = Spans(Emit(shapes));

        Assert.True(Burns(spans, 20, 17.5));
        Assert.True(CanvasFills(shapes, 20, 17.5));
    }

    /// <summary>Phase 1 finding D9, fixed. Two UNRELATED nested shapes (distinct sets) are two
    /// independent filled regions in both the canvas and the toolpath.</summary>
    [Fact]
    public void Agreement_NestedRingsInDifferentSets_SolidInToolpathAndCanvas()
    {
        var shapes = new[] { Rect(0, 0, 40, 40, Guid.NewGuid()), Rect(10, 10, 30, 30, Guid.NewGuid()) };

        var spans = Spans(Emit(shapes));

        Assert.True(Burns(spans, 20, 17.5));
        Assert.True(CanvasFills(shapes, 20, 17.5));
    }

    /// <summary>Partially overlapping (not nested) unrelated shapes: the overlap is
    /// engraved (union), matching the canvas.</summary>
    [Fact]
    public void Agreement_PartiallyOverlappingShapes_OverlapIsEngraved()
    {
        var shapes = new[] { Rect(0, 0, 20, 20, Guid.NewGuid()), Rect(10, 10, 30, 30, Guid.NewGuid()) };

        var spans = Spans(Emit(shapes));

        Assert.True(Burns(spans, 5, 12.5));
        Assert.True(Burns(spans, 15, 12.5));
        Assert.True(Burns(spans, 25, 12.5));
        Assert.True(CanvasFills(shapes, 15, 12.5));
    }

    /// <summary>Same overlap but in one set: same-wound rings merge under NonZero, identical to the
    /// different-sets union.</summary>
    [Fact]
    public void Agreement_PartialOverlapInOneSetBehavesLikeDifferentSets()
    {
        var set = Guid.NewGuid();
        var oneSet = Emit([Rect(0, 0, 20, 20, set), Rect(10, 10, 30, 30, set)]);
        var twoSets = Emit([Rect(0, 0, 20, 20, Guid.NewGuid()), Rect(10, 10, 30, 30, Guid.NewGuid())]);

        Assert.Equal(oneSet, twoSets);
    }

    /// <summary>Shapes on different processing layers never interact (LayerId split happens before
    /// ToolpathBuilder is invoked per layer by GCodeViewModel.BuildSceneGCode). Pinned so the
    /// contract keeps the layer as the outermost grouping key.</summary>
    [Fact]
    public void Current_ShapesOnDifferentLayersDoNotCarveEachOther()
    {
        var outerLayer = FillLayer("outer");
        var innerLayer = FillLayer("inner");
        var outer = Rect(0, 0, 40, 40, Guid.NewGuid(), layerId: outerLayer.Id);
        var inner = Rect(10, 10, 30, 30, Guid.NewGuid(), layerId: innerLayer.Id);

        var gcode = ToolpathBuilder.BuildGCode(new ImportedDocument
        {
            Shapes = [outer, inner],
            Layers = [outerLayer, innerLayer],
            BoundingBox = new BoundingBox2D(0, 0, 40, 40),
        }, ControllerMaximumS);

        var secondLayerStart = gcode.FindIndex(line => line.Contains("Vrstva inner", StringComparison.Ordinal));
        var outerSpans = Spans(gcode.Take(secondLayerStart));
        Assert.True(Burns(outerSpans, 20, 17.5)); // solid: the inner layer did not carve it
        Assert.Contains(Spans(gcode.Skip(secondLayerStart)), span => span.X0 == 10 && span.X1 == 30);
    }

    /// <summary>Open shapes never contribute to the fill (ToolpathBuilder.cs:87 filter), even when they
    /// cross a closed shape on the same layer.</summary>
    [Fact]
    public void Current_OpenShapesDoNotAffectFill()
    {
        var set = Guid.NewGuid();
        var rect = Rect(0, 0, 40, 40, set);
        var open = new ImportedShape
        {
            Points = [new Position(-5, 20, 0), new Position(45, 20, 0), new Position(20, 60, 0)],
            IsClosed = false,
            LayerColor = Black,
            PreferredMode = LayerMode.Fill,
            GeometrySetId = set,
        };

        Assert.Equal(Emit([rect]), Emit([rect, open]));
    }

    // ------------------------------------------------------------- producers: SVG import

    private const string NestedSameWoundPath =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40'><path fill='black' {0} " +
        "d='M0 0 H40 V40 H0 Z M10 10 H30 V30 H10 Z'/></svg>";

    /// <summary>fill-rule="nonzero" and an absent fill-rule import identically (SVG default); every
    /// contour keeps GeometrySetId == Guid.Empty (one compound group per imported object).</summary>
    [Fact]
    public void Contract_SvgNonZeroAndDefaultImportIdentically()
    {
        var none = SvgImporter.Import(string.Format(NestedSameWoundPath, ""), 40);
        var nonZero = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='nonzero'"), 40);

        Assert.Equal(2, none.Shapes.Count);
        Assert.All(none.Shapes, shape => Assert.Equal(Guid.Empty, shape.GeometrySetId));
        Assert.Equal(none.Shapes.Select(s => s.Points), nonZero.Shapes.Select(s => s.Points));
    }

    /// <summary>fill-rule="evenodd" on a same-wound nested pair is re-wound by nesting depth on import
    /// (inner ring reversed), so the SVG author's hole survives the NonZero fill contract. Flattened
    /// shapes and the editable VectorPath stay consistent.</summary>
    [Fact]
    public void Agreement_SvgEvenOddSameWoundNestedPath_BecomesHollowInToolpathAndCanvas()
    {
        var document = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='evenodd'"), 40);
        document.Layers[0].FillLineIntervalMm = Interval;

        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.False(Burns(spans, 20, 17.5));
        Assert.True(Burns(spans, 5, 17.5));
        Assert.False(CanvasFills(document.Shapes, 20, 17.5));

        var areas = document.Shapes.Select(shape => SignedArea(shape.Points)).ToList();
        Assert.True(areas[0] > 0 && areas[1] < 0);
        var path = Assert.IsType<VectorPath>(document.VectorPath);
        var pathAreas = path.FlattenAll().Select(SignedArea).ToList();
        Assert.True(pathAreas[0] > 0 && pathAreas[1] < 0);
    }

    /// <summary>fill-rule is inherited (group style) and alternates with depth: island inside a hole
    /// is filled again.</summary>
    [Fact]
    public void Agreement_SvgEvenOddInheritedFromGroup_AlternatesWithNestingDepth()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 60 60'><g style='fill-rule:evenodd'>" +
                  "<path fill='black' d='M0 0 H60 V60 H0 Z M10 10 H50 V50 H10 Z M20 20 H40 V40 H20 Z'/></g></svg>";
        var document = SvgImporter.Import(svg, 60);
        document.Layers[0].FillLineIntervalMm = Interval;

        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.True(Burns(spans, 5, 32.5));   // ring
        Assert.False(Burns(spans, 15, 32.5)); // hole
        Assert.True(Burns(spans, 30, 32.5));  // island
        Assert.False(CanvasFills(document.Shapes, 15, 32.5));
        Assert.True(CanvasFills(document.Shapes, 30, 32.5));
    }

    /// <summary>A same-wound nested pair in one &lt;path&gt; is SOLID in SVG's default nonzero rule and on
    /// Lasero's canvas; the toolpath now agrees. (fill-rule="evenodd" is handled separately.)</summary>
    [Fact]
    public void Agreement_SvgSameWoundNestedPath_SolidInToolpathAndCanvas()
    {
        var document = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='nonzero'"), 40);
        document.Layers[0].FillLineIntervalMm = Interval;

        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.True(Burns(spans, 20, 17.5));
        Assert.True(CanvasFills(document.Shapes, 20, 17.5));
    }

    /// <summary>A genuinely hollow SVG (hole wound opposite) is right in both consumers.</summary>
    [Fact]
    public void Agreement_SvgOppositeWoundHole_HollowInToolpathAndCanvas()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40'><path fill='black' " +
                  "d='M0 0 H40 V40 H0 Z M10 10 V30 H30 V10 Z'/></svg>";
        var document = SvgImporter.Import(svg, 40);
        document.Layers[0].FillLineIntervalMm = Interval;

        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.False(Burns(spans, 20, 17.5));
        Assert.False(CanvasFills(document.Shapes, 20, 17.5));
    }

    /// <summary>Legacy Guid.Empty means "one compound path per SceneObject". SceneDocument.
    /// ToImportedDocument now resolves it to the object's id, so two separately imported SVGs that
    /// overlap no longer carve each other, matching the canvas.</summary>
    [Fact]
    public void Agreement_TwoLegacyEmptySetObjects_DoNotCarveEachOtherInToolpath()
    {
        string Square(int min, int max) =>
            $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40'><path fill='black' " +
            $"d='M{min} {min} H{max} V{max} H{min} Z'/></svg>";
        var outer = SvgImporter.Import(Square(0, 40), 40);
        var inner = SvgImporter.Import(Square(10, 30), 40);

        var scene = new SceneDocument();
        var outerObject = SceneObjectFactory.FromImportedDocument(outer, "outer");
        var innerObject = SceneObjectFactory.FromImportedDocument(inner, "inner");
        scene.Objects.Add(outerObject);
        scene.Objects.Add(innerObject);
        scene.EnsureLayers(outer.Layers.Concat(inner.Layers).ToList(), [outerObject, innerObject]);
        foreach (var layer in scene.Layers) layer.FillLineIntervalMm = Interval;

        Assert.All(scene.Objects.SelectMany(o => o.GetWorldShapes()),
            shape => Assert.Equal(Guid.Empty, shape.GeometrySetId));

        var document = scene.ToImportedDocument();
        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.True(Burns(spans, 20, 17.5));
        // Canvas oracle: each object is its own group, so both are simply filled.
        foreach (var obj in scene.Objects)
            Assert.True(CanvasFills(obj.GetWorldShapes(), 20, 17.5));
    }

    // ------------------------------------------------ producers: tracer / boolean / offset

    private static RawContour Square(int x0, int y0, int x1, int y1, int depth, bool clockwiseOnScreen,
        params RawContour[] children)
    {
        var points = new List<PixelPoint> { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
        if (clockwiseOnScreen) points.Reverse();
        return new RawContour { Points = points, Depth = depth, Children = children };
    }

    /// <summary>CompoundPathBuilder is the one producer that DECLARES winding: it forces even-depth
    /// contours positive and odd-depth (hole) contours negative regardless of the order the contour
    /// extractor emitted them. Output is therefore consistent for both EvenOdd and NonZero.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Producer_TracerForcesOuterPositiveAndHoleNegativeWinding(bool outerReversed, bool holeReversed)
    {
        var hole = Square(30, 30, 70, 70, depth: 1, clockwiseOnScreen: holeReversed);
        var root = Square(0, 0, 100, 100, depth: 0, clockwiseOnScreen: outerReversed, hole);
        var tree = new ContourTree { Roots = [root], Width = 100, Height = 100 };

        var objects = CompoundPathBuilder.Build(tree, new BitmapTraceOptions(), Black, scaleMmPerPixel: 1);

        var subpaths = Assert.Single(objects).Path.Subpaths;
        Assert.Equal(2, subpaths.Count);
        var areas = subpaths.Select(sub => SignedArea(sub.Nodes.Select(n => n.Anchor).ToList())).ToList();
        Assert.True(areas[0] > 0, "outer contour must be positive");
        Assert.True(areas[1] < 0, "hole contour must be negative");
        Assert.True(Math.Abs(areas[0]) > Math.Abs(areas[1]));
    }

    /// <summary>Boolean output (Clipper2) comes back outer-positive / hole-negative, and feeding it to
    /// the toolpath in one set yields the intended hollow region: producer and consumer agree.</summary>
    [Fact]
    public void Agreement_BooleanResolveOutputFillsHollowInToolpathAndCanvas()
    {
        var outer = Rect(0, 0, 40, 40, Guid.Empty).Points.Take(4).ToList();
        var inner = Rect(10, 10, 30, 30, Guid.Empty).Points.Take(4).ToList();

        var rings = Clipper2VectorBooleanService.Default.Resolve([outer, inner], VectorFillRule.EvenOdd);

        Assert.Equal(2, rings.Count);
        var signs = rings.Select(ring => Math.Sign(SignedArea(ring))).ToList();
        Assert.Equal(0, signs.Sum());

        var set = Guid.NewGuid();
        var shapes = rings.Select(ring => new ImportedShape
        {
            Points = ring, IsClosed = true, LayerColor = Black, PreferredMode = LayerMode.Fill, GeometrySetId = set,
        }).ToList();
        var spans = Spans(Emit(shapes));
        Assert.False(Burns(spans, 20, 17.5));
        Assert.False(CanvasFills(shapes, 20, 17.5));
        Assert.True(Burns(spans, 5, 17.5));
    }

    /// <summary>Offset preserves a properly wound outer/hole pair: outer grows, hole shrinks, opposite
    /// signs kept.</summary>
    [Fact]
    public void Producer_OffsetKeepsOppositeWindingOfWellFormedCompound()
    {
        var outer = Rect(0, 0, 40, 40, Guid.Empty).Points.Take(4).ToList();
        var hole = Rect(10, 10, 30, 30, Guid.Empty, clockwise: true).Points.Skip(1).ToList();

        var rings = Clipper2VectorOffsetService.Default.OffsetClosedGroup(
            [outer, hole], 2, VectorJoinType.Miter, 2);

        Assert.Equal(2, rings.Count);
        var areas = rings.Select(SignedArea).OrderByDescending(Math.Abs).ToList();
        Assert.Equal(44 * 44, Math.Abs(areas[0]), precision: 3);
        Assert.Equal(16 * 16, Math.Abs(areas[1]), precision: 3);
        Assert.NotEqual(Math.Sign(areas[0]), Math.Sign(areas[1]));
    }

    /// <summary>VectorOffsetPlanner does no winding normalisation (audit 5.1): a same-wound nested
    /// pair -- which the toolpath engraves as a hollow compound path -- is offset as two overlapping
    /// OUTERS. The hole is not preserved. Pins today's result so the contract change is visible.</summary>
    [Fact]
    public void Disagreement_OffsetOfSameWoundNestedPairLosesTheHoleTheToolpathWouldEngrave()
    {
        var outer = Rect(0, 0, 40, 40, Guid.Empty).Points.Take(4).ToList();
        var inner = Rect(10, 10, 30, 30, Guid.Empty).Points.Take(4).ToList();

        var rings = Clipper2VectorOffsetService.Default.OffsetClosedGroup(
            [outer, inner], 2, VectorJoinType.Miter, 2);

        // Today: the two same-wound rings merge into one 44x44 outer; no hole ring is returned.
        var ring = Assert.Single(rings);
        Assert.Equal(44 * 44, Math.Abs(SignedArea(ring)), precision: 3);
    }
}
