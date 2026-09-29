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
/// These pin what the code does TODAY where the producers (SVG import, tracer, boolean, offset,
/// text) and the consumers (ToolpathBuilder fill = layer-wide EvenOdd; canvas = per-compound-path
/// NonZero) disagree. They are behaviour locks, not endorsements. Tests whose name starts with
/// "Disagreement_" assert a state that the contract decision is expected to change; they name the
/// oracle ("what the canvas draws") next to the actual toolpath so the disagreement is explicit.
/// When the contract lands, invert those tests deliberately -- do not delete them.
///
/// No golden files are involved and none are regenerated here.
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

    /// <summary>Fill ignores winding direction entirely: reversing one ring of a same-set pair does
    /// not change a single engraved run. (Cut output does follow point order -- see the existing
    /// geometry-reversed-path golden.)</summary>
    [Fact]
    public void Current_FillOutputIsIndependentOfRingWindingDirection()
    {
        var set = Guid.NewGuid();
        var sameWound = Emit([Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set)]);
        var oppositeWound = Emit([Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set, clockwise: true)]);

        Assert.Equal(sameWound, oppositeWound);
    }

    /// <summary>DISAGREEMENT. Same-wound nested rings in ONE declared set (what the existing
    /// geometry-compound-path-hole-fill golden feeds in, and what an SVG whose author relied on
    /// fill-rule="evenodd" produces). The toolpath leaves a hole; the canvas (NonZero) draws it solid.
    /// The operator previews solid metal and gets a hollow engraving.</summary>
    [Fact]
    public void Disagreement_SameWoundNestedRingsInOneSet_ToolpathHollowCanvasSolid()
    {
        var set = Guid.NewGuid();
        var shapes = new[] { Rect(0, 0, 40, 40, set), Rect(10, 10, 30, 30, set) };

        var spans = Spans(Emit(shapes));

        Assert.False(Burns(spans, 20, 17.5)); // toolpath: hole
        Assert.True(CanvasFills(shapes, 20, 17.5)); // canvas: solid
    }

    /// <summary>DISAGREEMENT (Phase 1 finding D9, restated with the canvas oracle). Two UNRELATED
    /// nested shapes (distinct sets): the canvas paints two independent filled regions, the toolpath
    /// carves the inner one out of the outer.</summary>
    [Fact]
    public void Disagreement_NestedRingsInDifferentSets_ToolpathHollowCanvasSolid()
    {
        var shapes = new[] { Rect(0, 0, 40, 40, Guid.NewGuid()), Rect(10, 10, 30, 30, Guid.NewGuid()) };

        var spans = Spans(Emit(shapes));

        Assert.False(Burns(spans, 20, 17.5));
        Assert.True(CanvasFills(shapes, 20, 17.5));
    }

    /// <summary>DISAGREEMENT. Partially overlapping (not nested) unrelated shapes: the overlap is an
    /// unengraved lens in the toolpath, filled on the canvas.</summary>
    [Fact]
    public void Disagreement_PartiallyOverlappingShapes_OverlapIsUnengraved()
    {
        var shapes = new[] { Rect(0, 0, 20, 20, Guid.NewGuid()), Rect(10, 10, 30, 30, Guid.NewGuid()) };

        var spans = Spans(Emit(shapes));

        Assert.True(Burns(spans, 5, 12.5));
        Assert.False(Burns(spans, 15, 12.5)); // overlap: skipped by the toolpath
        Assert.True(Burns(spans, 25, 12.5));
        Assert.True(CanvasFills(shapes, 15, 12.5));
    }

    /// <summary>Same overlap but in one set: EvenOdd (toolpath) and NonZero same-wound (canvas) still
    /// disagree; GeometrySetId makes no difference to the toolpath.</summary>
    [Fact]
    public void Disagreement_PartialOverlapInOneSetBehavesLikeDifferentSets()
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

    /// <summary>SvgImporter never reads fill-rule (no occurrence in Lasero.Core): evenodd, nonzero and
    /// absent give byte-identical shapes. Every imported contour has GeometrySetId == Guid.Empty.</summary>
    [Fact]
    public void Current_SvgImporterIgnoresFillRuleAndLeavesGeometrySetEmpty()
    {
        var none = SvgImporter.Import(string.Format(NestedSameWoundPath, ""), 40);
        var evenOdd = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='evenodd'"), 40);
        var nonZero = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='nonzero'"), 40);

        Assert.Equal(2, none.Shapes.Count);
        Assert.All(none.Shapes, shape => Assert.Equal(Guid.Empty, shape.GeometrySetId));
        Assert.Equal(none.Shapes.Select(s => s.Points), evenOdd.Shapes.Select(s => s.Points));
        Assert.Equal(none.Shapes.Select(s => s.Points), nonZero.Shapes.Select(s => s.Points));
    }

    /// <summary>DISAGREEMENT for SVG semantics: a same-wound nested pair in one &lt;path&gt; is SOLID in
    /// SVG's default nonzero rule (and on Lasero's canvas), but the toolpath engraves a hole. The
    /// author of the SVG has no way to say otherwise -- fill-rule is dropped on import.</summary>
    [Fact]
    public void Disagreement_SvgSameWoundNestedPath_ToolpathHollowCanvasSolid()
    {
        var document = SvgImporter.Import(string.Format(NestedSameWoundPath, "fill-rule='nonzero'"), 40);
        document.Layers[0].FillLineIntervalMm = Interval;

        var spans = Spans(ToolpathBuilder.BuildGCode(document, ControllerMaximumS));

        Assert.False(Burns(spans, 20, 17.5));
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

    /// <summary>Legacy Guid.Empty means "one compound path per SceneObject" for the canvas and for
    /// BuildSourceRings, but SceneDocument.ToImportedDocument flattens every object's shapes without
    /// rewriting it -- so ToolpathBuilder sees two objects' Empty-set shapes as one pool. Two
    /// separately imported SVGs that overlap therefore carve each other. The canvas paints them as
    /// two independent (per-object) regions.</summary>
    [Fact]
    public void Disagreement_TwoLegacyEmptySetObjects_CarveEachOtherInToolpath()
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

        Assert.False(Burns(spans, 20, 17.5)); // toolpath: inner carved out of outer
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
