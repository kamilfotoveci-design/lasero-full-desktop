using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Tests.Golden;

namespace Lasero.Tests;

/// <summary>
/// Freezes the exact G-code <see cref="ToolpathBuilder"/> emits today, across the geometry and
/// operation matrix a real job exercises. These are behaviour locks, not design endorsements: a
/// failure here means the physical output of the machine changed, which during the planned
/// LaserJob/post-processor extraction is exactly the signal we want.
///
/// Geometry is built through the production factories (<see cref="ScenePrimitiveFactory"/>,
/// <see cref="VectorSubpath.Flatten"/>) and routed through
/// <see cref="SceneDocument.ToImportedDocument"/> wherever possible, so the goldens cover real
/// flattening and transform behaviour rather than hand-typed point lists.
/// </summary>
public sealed class GoldenGCodeVectorTests
{
    // GRBL $30 default. A second scaling case below uses 255 to pin the percent -> S conversion.
    private const double ControllerMaximumS = 1000;

    private static readonly RgbColor Black = new(0, 0, 0);
    private static readonly RgbColor Red = new(0xE0, 0x10, 0x10);

    // ---------------------------------------------------------------- geometry

    [Fact]
    public void Golden_Geometry_SingleLine() =>
        VerifyScene("geometry-single-line", CutLayer(Black), document =>
            document.Objects.Add(ScenePrimitiveFactory.CreateLine(
                new Position(10, 10, 0), new Position(40, 25, 0), Black, "Čára")));

    [Fact]
    public void Golden_Geometry_OpenPolyline() =>
        VerifyShapes("geometry-open-polyline", CutLayer(Black),
            Shape(closed: false, Black,
                new Position(0, 0, 0),
                new Position(10, 0, 0),
                new Position(10, 10, 0),
                new Position(25, 4, 0)));

    [Fact]
    public void Golden_Geometry_ClosedRectangle() =>
        VerifyScene("geometry-closed-rectangle", CutLayer(Black), document =>
            document.Objects.Add(ScenePrimitiveFactory.CreateRectangle(
                new Position(5, 5, 0), new Position(35, 20, 0), Black, "Obdélník")));

    [Fact]
    public void Golden_Geometry_Ellipse() =>
        VerifyScene("geometry-ellipse", CutLayer(Black), document =>
            document.Objects.Add(ScenePrimitiveFactory.CreateEllipse(
                new Position(0, 0, 0), new Position(30, 18, 0), Black, "Elipsa")));

    [Fact]
    public void Golden_Geometry_Polygon() =>
        VerifyScene("geometry-polygon", CutLayer(Black), document =>
            document.Objects.Add(ScenePrimitiveFactory.CreatePolygon(
                new Position(0, 0, 0), new Position(24, 24, 0), 6, Black, "Šestiúhelník")));

    /// <summary>
    /// An open cubic Bézier flattened by the production adaptive subdivision. This is the golden
    /// that pins <see cref="VectorPath.DefaultFlattenToleranceMm"/> (0.05 mm) — change the tolerance
    /// and the emitted point count changes with it.
    /// </summary>
    [Fact]
    public void Golden_Geometry_CubicBezierOpen()
    {
        var subpath = new VectorSubpath
        {
            IsClosed = false,
            Nodes =
            [
                new VectorNode(new Position(0, 0, 0), null, new Position(0, 20, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(40, 20, 0), new Position(40, 0, 0), null, VectorNodeType.Smooth),
            ],
        };

        VerifyShapes("geometry-cubic-bezier-open", CutLayer(Black),
            Shape(closed: false, Black, subpath.Flatten()));
    }

    /// <summary>A closed four-segment cubic circle (the standard kappa construction) — the shape an
    /// SVG &lt;circle&gt; becomes after arc-to-Bézier conversion on import.</summary>
    [Fact]
    public void Golden_Geometry_CubicBezierClosedCircle()
    {
        const double r = 15;
        const double k = 0.5522847498307936 * r;
        var subpath = new VectorSubpath
        {
            IsClosed = true,
            Nodes =
            [
                new VectorNode(new Position(r, 0, 0), new Position(r, -k, 0), new Position(r, k, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(0, r, 0), new Position(k, r, 0), new Position(-k, r, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(-r, 0, 0), new Position(-r, k, 0), new Position(-r, -k, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(0, -r, 0), new Position(-k, -r, 0), new Position(k, -r, 0), VectorNodeType.Smooth),
            ],
        };

        VerifyShapes("geometry-cubic-bezier-closed-circle", CutLayer(Black),
            Shape(closed: true, Black, subpath.Flatten()));
    }

    [Fact]
    public void Golden_Geometry_MultipleIndependentShapes() =>
        VerifyScene("geometry-multiple-independent-shapes", CutLayer(Black), document =>
        {
            document.Objects.Add(ScenePrimitiveFactory.CreateRectangle(
                new Position(0, 0, 0), new Position(10, 10, 0), Black, "A"));
            document.Objects.Add(ScenePrimitiveFactory.CreateRectangle(
                new Position(30, 0, 0), new Position(40, 10, 0), Black, "B"));
            document.Objects.Add(ScenePrimitiveFactory.CreateRectangle(
                new Position(60, 0, 0), new Position(70, 10, 0), Black, "C"));
        });

    /// <summary>
    /// Two rectangles where one sits fully inside the other but they are NOT one compound path
    /// (distinct GeometrySetIds). On a Cut layer this is unambiguous. The Fill counterpart below is
    /// where the layer-wide even-odd rule becomes visible.
    /// </summary>
    [Fact]
    public void Golden_Geometry_NestedUnrelatedShapes_Cut() =>
        VerifyShapes("geometry-nested-unrelated-cut", CutLayer(Black),
            Rect(0, 0, 40, 40, Black, Guid.NewGuid()),
            Rect(10, 10, 30, 30, Black, Guid.NewGuid()));

    /// <summary>
    /// The same nested pair on a Fill layer. Documents the current layer-wide even-odd behaviour:
    /// the inner rectangle carves a hole out of the outer one even though they are unrelated
    /// objects. See <see cref="GCodeEmitterDifferenceTests"/> for the full write-up — this golden
    /// exists so the Phase 2 fill-contract work has to consciously change it.
    /// </summary>
    [Fact]
    public void Golden_Geometry_NestedUnrelatedShapes_Fill() =>
        VerifyShapes("geometry-nested-unrelated-fill", FillLayer(Black, intervalMm: 5),
            Rect(0, 0, 40, 40, Black, Guid.NewGuid()),
            Rect(10, 10, 30, 30, Black, Guid.NewGuid()));

    /// <summary>Outer contour plus a genuine hole — one compound path, one shared GeometrySetId.</summary>
    [Fact]
    public void Golden_Geometry_CompoundPathWithHole_Fill()
    {
        var set = Guid.NewGuid();
        VerifyShapes("geometry-compound-path-hole-fill", FillLayer(Black, intervalMm: 5),
            Rect(0, 0, 40, 40, Black, set),
            Rect(10, 10, 30, 30, Black, set));
    }

    [Fact]
    public void Golden_Geometry_CompoundPathWithHole_Cut()
    {
        var set = Guid.NewGuid();
        VerifyShapes("geometry-compound-path-hole-cut", CutLayer(Black),
            Rect(0, 0, 40, 40, Black, set),
            Rect(10, 10, 30, 30, Black, set));
    }

    /// <summary>
    /// The same rectangle wound the other way. Pins whether contour direction reaches the machine —
    /// it does, as emitted point order. Relevant to the Phase 2 winding contract.
    /// </summary>
    [Fact]
    public void Golden_Geometry_ReversedPath()
    {
        var forward = Rect(0, 0, 20, 10, Black, Guid.Empty);
        var reversed = forward with { Points = forward.Points.Reverse().ToList() };
        VerifyShapes("geometry-reversed-path", CutLayer(Black), reversed);
    }

    /// <summary>
    /// A closed shape whose last point does not repeat the first. ToolpathBuilder closes it with an
    /// explicit extra G1 (AppendCutLayer's IsClosed branch) — that synthesised closing move is the
    /// behaviour this golden protects.
    /// </summary>
    [Fact]
    public void Golden_Geometry_ClosedShapeWithoutRepeatedFirstPoint() =>
        VerifyShapes("geometry-closed-without-repeated-point", CutLayer(Black),
            Shape(closed: true, Black,
                new Position(0, 0, 0),
                new Position(20, 0, 0),
                new Position(20, 10, 0),
                new Position(0, 10, 0)));

    /// <summary>Move + rotate + non-uniform scale applied through the real ObjectTransform pipeline.</summary>
    [Fact]
    public void Golden_Geometry_Transformed() =>
        VerifyScene("geometry-transformed", CutLayer(Black), document =>
        {
            var rectangle = ScenePrimitiveFactory.CreateRectangle(
                new Position(0, 0, 0), new Position(20, 10, 0), Black, "Transformovaný");
            rectangle.Transform = new ObjectTransform(X: 12.5, Y: 7.25, RotationDeg: 30, ScaleX: 1.5, ScaleY: 0.75);
            document.Objects.Add(rectangle);
        });

    /// <summary>Document offset, as applied when the job is placed relative to the current head position.</summary>
    [Fact]
    public void Golden_Geometry_PlacementOffset()
    {
        var document = new SceneDocument();
        document.Layers.Add(CutLayer(Black));
        document.Objects.Add(ScenePrimitiveFactory.CreateRectangle(
            new Position(0, 0, 0), new Position(20, 10, 0), Black, "Posunutý"));

        GoldenGCode.Verify("geometry-placement-offset",
            ToolpathBuilder.BuildGCode(document.ToImportedDocument(offsetX: 128.4, offsetY: 72), ControllerMaximumS));
    }

    // -------------------------------------------------------------- operations

    [Fact]
    public void Golden_Operation_Cut() =>
        VerifyShapes("operation-cut", CutLayer(Black), Rect(0, 0, 20, 10, Black, Guid.Empty));

    [Fact]
    public void Golden_Operation_Fill() =>
        VerifyShapes("operation-fill", FillLayer(Black, intervalMm: 2), Rect(0, 0, 20, 10, Black, Guid.Empty));

    /// <summary>Fill+Cut. The engrave-before-cut ordering here is a safety property, not a preference:
    /// cutting the contour first would let the part shift before it is engraved.</summary>
    [Fact]
    public void Golden_Operation_FillAndCut()
    {
        var layer = FillLayer(Black, intervalMm: 4);
        layer.Mode = LayerMode.FillAndCut;
        VerifyShapes("operation-fill-and-cut", layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
    }

    [Fact]
    public void Golden_Operation_MultiplePasses()
    {
        var layer = CutLayer(Black);
        layer.Passes = 3;
        VerifyShapes("operation-multiple-passes", layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
    }

    [Fact]
    public void Golden_Operation_SlowCutHighPower()
    {
        var layer = CutLayer(Black);
        layer.Speed = 120;
        layer.Power = 100;
        VerifyShapes("operation-slow-cut-high-power", layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
    }

    [Fact]
    public void Golden_Operation_FastEngraveLowPower()
    {
        var layer = CutLayer(Black);
        layer.Speed = 6000;
        layer.Power = 12.5;
        VerifyShapes("operation-fast-engrave-low-power", layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
    }

    /// <summary>Non-1000 controller maximum — pins GrblPowerScale's percent -> S conversion and the
    /// "0.###" rounding that turns 12.5% of 255 into a fractional S word.</summary>
    [Fact]
    public void Golden_Operation_PowerScalingAgainst255Controller()
    {
        var layer = CutLayer(Black);
        layer.Power = 12.5;
        var document = DocumentOf(layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
        GoldenGCode.Verify("operation-power-scaling-255", ToolpathBuilder.BuildGCode(document, 255));
    }

    [Fact]
    public void Golden_Operation_DisabledLayerEmitsNothing()
    {
        var layer = CutLayer(Black);
        layer.IsEnabled = false;
        VerifyShapes("operation-disabled-layer", layer, Rect(0, 0, 20, 10, Black, Guid.Empty));
    }

    /// <summary>Two layers in one document, matched by LayerId. Layer collection order is the
    /// manufacturing order — ToolpathBuilder must never reorder it.</summary>
    [Fact]
    public void Golden_Operation_TwoLayersFillThenCut()
    {
        var fill = FillLayer(Black, intervalMm: 5);
        var cut = CutLayer(Red);
        GoldenGCode.Verify("operation-two-layers-fill-then-cut", ToolpathBuilder.BuildGCode(
            new ImportedDocument
            {
                Shapes = [Rect(0, 0, 20, 20, Black, Guid.Empty, fill.Id), Rect(0, 0, 20, 20, Red, Guid.Empty, cut.Id)],
                Layers = [fill, cut],
                BoundingBox = new BoundingBox2D(0, 0, 20, 20),
            },
            ControllerMaximumS));
    }

    /// <summary>The same two layers with the operator's ordering reversed. Cut-before-fill is a
    /// legitimate (if usually unwise) operator choice — preflight may warn, the builder must obey.</summary>
    [Fact]
    public void Golden_Operation_TwoLayersCutThenFill()
    {
        var fill = FillLayer(Black, intervalMm: 5);
        var cut = CutLayer(Red);
        GoldenGCode.Verify("operation-two-layers-cut-then-fill", ToolpathBuilder.BuildGCode(
            new ImportedDocument
            {
                Shapes = [Rect(0, 0, 20, 20, Black, Guid.Empty, fill.Id), Rect(0, 0, 20, 20, Red, Guid.Empty, cut.Id)],
                Layers = [cut, fill],
                BoundingBox = new BoundingBox2D(0, 0, 20, 20),
            },
            ControllerMaximumS));
    }

    /// <summary>Legacy colour-matched geometry (LayerId == Guid.Empty) alongside ID-matched geometry.</summary>
    [Fact]
    public void Golden_Operation_LegacyColourMatchedGeometry()
    {
        var layer = CutLayer(Red);
        GoldenGCode.Verify("operation-legacy-colour-matched", ToolpathBuilder.BuildGCode(
            new ImportedDocument
            {
                Shapes = [Rect(0, 0, 20, 10, Red, Guid.Empty)],
                Layers = [layer],
                BoundingBox = new BoundingBox2D(0, 0, 20, 10),
            },
            ControllerMaximumS));
    }

    /// <summary>A degenerate single-point shape. AppendCutLayer skips anything under 2 points — this
    /// pins that it is skipped silently rather than emitting a zero-length burn.</summary>
    [Fact]
    public void Golden_Operation_SinglePointShapeIsSkipped() =>
        VerifyShapes("operation-single-point-skipped", CutLayer(Black),
            Shape(closed: false, Black, new Position(5, 5, 0)));

    /// <summary>A fill layer whose shapes are all open. AppendFillLayer requires closed shapes with
    /// 3+ points, so this emits a layer header and nothing else.</summary>
    [Fact]
    public void Golden_Operation_FillWithOnlyOpenShapes() =>
        VerifyShapes("operation-fill-only-open-shapes", FillLayer(Black, intervalMm: 2),
            Shape(closed: false, Black,
                new Position(0, 0, 0), new Position(20, 0, 0), new Position(20, 10, 0)));

    // ----------------------------------------------------------------- helpers

    private static void VerifyScene(string name, LayerSettings layer, Action<SceneDocument> build)
    {
        var document = new SceneDocument();
        document.Layers.Add(layer);
        build(document);
        GoldenGCode.Verify(name, ToolpathBuilder.BuildGCode(document.ToImportedDocument(), ControllerMaximumS));
    }

    private static void VerifyShapes(string name, LayerSettings layer, params ImportedShape[] shapes) =>
        GoldenGCode.Verify(name, ToolpathBuilder.BuildGCode(DocumentOf(layer, shapes), ControllerMaximumS));

    private static ImportedDocument DocumentOf(LayerSettings layer, params ImportedShape[] shapes)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in shapes.SelectMany(shape => shape.Points))
            bounds = bounds.Include(point.X, point.Y);

        return new ImportedDocument
        {
            Shapes = shapes,
            Layers = [layer],
            BoundingBox = shapes.Length == 0 ? new BoundingBox2D(0, 0, 0, 0) : bounds,
        };
    }

    private static ImportedShape Shape(bool closed, RgbColor color, params Position[] points) =>
        Shape(closed, color, (IReadOnlyList<Position>)points);

    private static ImportedShape Shape(bool closed, RgbColor color, IReadOnlyList<Position> points) => new()
    {
        Points = points,
        IsClosed = closed,
        LayerColor = color,
        PreferredMode = LayerMode.Cut,
    };

    private static ImportedShape Rect(
        double minX, double minY, double maxX, double maxY,
        RgbColor color, Guid geometrySetId, Guid layerId = default) => new()
    {
        Points =
        [
            new Position(minX, minY, 0),
            new Position(maxX, minY, 0),
            new Position(maxX, maxY, 0),
            new Position(minX, maxY, 0),
            new Position(minX, minY, 0),
        ],
        IsClosed = true,
        LayerColor = color,
        PreferredMode = LayerMode.Cut,
        GeometrySetId = geometrySetId,
        LayerId = layerId,
    };

    private static LayerSettings CutLayer(RgbColor color) => new()
    {
        Color = color,
        Name = "Řez",
        Mode = LayerMode.Cut,
        Speed = 350,
        Power = 95,
        Passes = 1,
    };

    private static LayerSettings FillLayer(RgbColor color, double intervalMm) => new()
    {
        Color = color,
        Name = "Výplň",
        Mode = LayerMode.Fill,
        Speed = 3000,
        Power = 30,
        Passes = 1,
        FillLineIntervalMm = intervalMm,
    };
}
