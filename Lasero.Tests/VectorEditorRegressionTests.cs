using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>Regression tests for the vector-editor correctness batch: world-scale flattening, group
/// flip, hit-testing at zoom, smooth-node invariants, node removal, ellipse-to-Bezier conversion,
/// weld, joins, sliver tolerance, metadata alignment and load-time reconciliation.</summary>
public class VectorEditorRegressionTests
{
    private static readonly RgbColor Red = new(255, 0, 0);

    private static Position P(double x, double y) => new(x, y, 0);

    private static VectorSubpath Circle(int nodeCount, double radius, double cx = 0, double cy = 0)
    {
        var k = 4.0 / 3.0 * Math.Tan(Math.PI / (2.0 * nodeCount));
        var nodes = new List<VectorNode>();
        for (var i = 0; i < nodeCount; i++)
        {
            var a = 2 * Math.PI * i / nodeCount;
            var anchor = P(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            var tx = -Math.Sin(a) * radius * k;
            var ty = Math.Cos(a) * radius * k;
            nodes.Add(new VectorNode(anchor, P(anchor.X - tx, anchor.Y - ty), P(anchor.X + tx, anchor.Y + ty),
                VectorNodeType.Smooth));
        }
        return new VectorSubpath { Nodes = nodes, IsClosed = true };
    }

    private static VectorPath Single(VectorSubpath subpath) => new() { Subpaths = [subpath] };

    // 1 ---------------------------------------------------------------------------------------

    [Fact]
    public void EnlargedVectorPathIsFlattenedAtWorldTolerance()
    {
        var obj = VectorPathSceneFactory.Create(Single(Circle(4, 10)), Red, "circle");
        obj.Transform = obj.Transform with { ScaleX = 10, ScaleY = 10 };

        var points = obj.GetWorldShapes().Single().Points;

        Assert.True(points.Count > 40);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var mx = (points[i].X + points[i + 1].X) / 2;
            var my = (points[i].Y + points[i + 1].Y) / 2;
            var radius = Math.Sqrt(mx * mx + my * my);
            Assert.InRange(radius, 100 - 0.08, 100 + 0.08); // chord sagitta stays near 0.05 mm
        }
    }

    [Fact]
    public void LargeEllipsesGetMoreSegmentsThanSmallOnes()
    {
        var small = ScenePrimitiveFactory.CreateEllipse(P(0, 0), P(20, 20), Red, "s");
        var large = ScenePrimitiveFactory.CreateEllipse(P(0, 0), P(6000, 6000), Red, "l");

        Assert.Equal(65, small.LocalShapes[0].Points.Count);
        Assert.True(large.LocalShapes[0].Points.Count > 200);
    }

    // 2 ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45)]
    [InlineData(30)]
    [InlineData(135)]
    public void FlippingARotatedChildMirrorsItsBoundsInsteadOfCollapsingIt(double rotation)
    {
        var obj = ScenePrimitiveFactory.CreateRectangle(P(0, 0), P(10, 4), Red, "r");
        obj.Transform = obj.Transform with { RotationDeg = rotation };
        var before = obj.WorldBounds();
        var anchor = P(30, 0);

        obj.Transform = GroupTransform.ScaleAbout(obj.Transform, obj.LocalPivot, anchor, -1, 1);

        var after = obj.WorldBounds();
        Assert.True(Math.Abs(obj.Transform.ScaleX) > 0.5 && Math.Abs(obj.Transform.ScaleY) > 0.5);
        Assert.Equal(2 * anchor.X - before.MaxX, after.MinX, precision: 6);
        Assert.Equal(2 * anchor.X - before.MinX, after.MaxX, precision: 6);
        Assert.Equal(before.MinY, after.MinY, precision: 6);
        Assert.Equal(before.MaxY, after.MaxY, precision: 6);
    }

    // 4 ---------------------------------------------------------------------------------------

    [Fact]
    public void RefreshingALinkedOffsetDropsAnyPlacementCarriedByTheOutput()
    {
        var output = VectorPathSceneFactory.Create(Single(Circle(4, 5)), Red, "offset");
        output.OffsetLink = new OffsetLink { SourceIds = [Guid.NewGuid()], Options = new VectorOffsetOptions() };
        output.Transform = new ObjectTransform(7, -3, 20, 1.5, 1);

        output.RefreshDerivedVectorPath(Single(Circle(4, 6, 40, 40)));

        Assert.Equal(ObjectTransform.Identity, output.Transform);
        var world = output.GetWorldShapes().Single().Points;
        Assert.InRange(world.Max(p => p.X), 45.9, 46.1);
    }

    [Fact]
    public void IndependentTransformTargetsLeavesOutOffsetsWhoseSourceIsSelected()
    {
        var source = VectorPathSceneFactory.Create(Single(Circle(4, 5)), Red, "src");
        var offset = VectorPathSceneFactory.Create(Single(Circle(4, 6)), Red, "off");
        offset.OffsetLink = new OffsetLink { SourceIds = [source.Id], Options = new VectorOffsetOptions() };

        var targets = SceneViewModel.IndependentTransformTargets([source, offset]);

        Assert.Same(source, Assert.Single(targets));
        Assert.Same(offset, Assert.Single(SceneViewModel.IndependentTransformTargets([offset])));
    }

    // 5 ---------------------------------------------------------------------------------------

    [Fact]
    public void HugeZoomedCurveHasNoHitDeadZones()
    {
        var path = VectorPath.SingleOpen(
        [
            new VectorNode(P(0, 0), null, P(300, 800), VectorNodeType.Corner),
            new VectorNode(P(1000, 0), P(700, -800), null, VectorNodeType.Corner),
        ]);

        foreach (var t in new[] { 0.13, 0.37, 0.52, 0.81 })
        {
            var onCurve = CubicBezier.Evaluate(P(0, 0), P(300, 800), P(700, -800), P(1000, 0), t);
            var hit = VectorPathHitTester.FindNearest(path, onCurve, 6, p => p);

            Assert.NotNull(hit);
            Assert.True(hit!.Value.Distance < 0.5, $"t={t} distance {hit.Value.Distance}");
            Assert.InRange(hit.Value.T, t - 0.01, t + 0.01);
        }
    }

    // 6 ---------------------------------------------------------------------------------------

    [Fact]
    public void DraggingASegmentKeepsSmoothNodeHandlesCollinear()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(P(0, 0), P(-5, 0), P(5, 0), VectorNodeType.Smooth),
                new VectorNode(P(20, 0), P(15, 0), P(25, 0), VectorNodeType.Smooth),
            ],
        };

        var result = VectorPathEditor.DragSegmentPoint(subpath, 0, 0.5, P(10, 8));

        foreach (var node in result.Nodes)
        {
            var inX = node.HandleIn!.Value.X - node.Anchor.X;
            var inY = node.HandleIn!.Value.Y - node.Anchor.Y;
            var outX = node.HandleOut!.Value.X - node.Anchor.X;
            var outY = node.HandleOut!.Value.Y - node.Anchor.Y;
            Assert.Equal(0, inX * outY - inY * outX, precision: 6); // collinear through the anchor
            Assert.True(inX * outX + inY * outY < 0);               // on opposite sides
        }
        Assert.Equal(5, Math.Sqrt(Math.Pow(result.Nodes[0].HandleIn!.Value.X, 2) + Math.Pow(result.Nodes[0].HandleIn!.Value.Y, 2)), precision: 6);
    }

    // 13 --------------------------------------------------------------------------------------

    [Fact]
    public void RemovingASmoothNodeRefitsTheNeighbourHandlesToTheOriginalCurve()
    {
        var circle = Circle(8, 50);
        var original = circle.Flatten(0.01);
        // Remove every other node ... one removal is enough to prove the refit: node 1.
        var removed = VectorPathEditor.RemoveNode(circle, 1);

        Assert.Equal(7, removed.Nodes.Count);
        var after = removed.Flatten(0.01);
        var maxDrift = original.Max(p => after.Min(q => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y))));
        // Without the refit the neighbours keep the short 1/8-arc handles and the curve cuts the corner
        // by several millimetres; refitted it stays within ~1 mm of the 50 mm circle.
        Assert.True(maxDrift < 1.5, $"drift {maxDrift}");
    }

    // 8 ---------------------------------------------------------------------------------------

    [Fact]
    public void EllipseRingBecomesFourBezierNodes()
    {
        var ellipse = ScenePrimitiveFactory.CreateEllipse(P(0, 0), P(40, 20), Red, "e");
        var ring = ellipse.LocalShapes[0].Points;

        Assert.True(VectorPathEditor.TryFitEllipseSubpath(ring, ring.Count - 1, out var subpath));

        Assert.Equal(4, subpath.Nodes.Count);
        Assert.True(subpath.IsClosed);
        foreach (var point in subpath.Flatten(0.01))
        {
            var nx = point.X / 20;
            var ny = point.Y / 10;
            Assert.InRange(nx * nx + ny * ny, 0.99, 1.01);
        }
    }

    [Fact]
    public void NonEllipticalRingsAreNotConvertedToBezier()
    {
        var square = new[] { P(0, 0), P(10, 0), P(10, 10), P(0, 10) };
        Assert.False(VectorPathEditor.TryFitEllipseSubpath(square, 4, out _));
        var star = ScenePrimitiveFactory.CreateStar(P(0, 0), P(40, 40), 8, 0.5, Red, "s");
        var ring = star.LocalShapes[0].Points;
        Assert.False(VectorPathEditor.TryFitEllipseSubpath(ring, ring.Count - 1, out _));
    }

    [Fact]
    public void ConvertingAnEllipseToCurvesKeepsShapesInStepWithNodesAndIsOneUndoStep()
    {
        var vm = new SceneViewModel();
        var ellipse = ScenePrimitiveFactory.CreateEllipse(P(0, 0), P(40, 20), Red, "e");
        vm.Execute(new AddObjectCommand(vm.Scene, ellipse, [LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez")]));
        vm.SelectedObjects.ReplaceWith([ellipse]);

        var converted = vm.ConvertSelectedObjectToCurves();

        Assert.NotNull(converted);
        Assert.Equal(4, converted!.VectorPath!.Subpaths[0].Nodes.Count);
        Assert.Equal(converted.VectorPath.Subpaths.Count, converted.LocalShapes.Count);
        Assert.Equal(ellipse.LocalShapes[0].LayerId, converted.LocalShapes[0].LayerId);
        vm.UndoCommand.Execute(null);
        Assert.Same(ellipse, vm.Objects.Single());
    }

    [Fact]
    public void TrimmingAPlainOutlineIsASingleUndoStep()
    {
        var vm = new SceneViewModel();
        var rect = ScenePrimitiveFactory.CreateRectangle(P(0, 0), P(10, 10), Red, "r");
        vm.Execute(new AddObjectCommand(vm.Scene, rect, [LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez")]));

        var result = vm.ConvertAndEditVectorPath(rect,
            (_, path) => VectorPathEditor.DeleteSegment(path, 0, 0), out var deleteRequested);

        Assert.False(deleteRequested);
        Assert.NotNull(result);
        Assert.False(result!.VectorPath!.Subpaths[0].IsClosed);
        Assert.Single(vm.Objects);
        vm.UndoCommand.Execute(null);
        Assert.Same(rect, vm.Objects.Single());
        Assert.False(rect.IsVectorPath);
    }

    [Fact]
    public void RebuildMapsLayerMetadataBySubpathWhenAShortContourStartsProducingAShape()
    {
        var path = new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath { Nodes = [VectorNode.CornerAt(P(0, 0)), VectorNode.CornerAt(P(5, 0)), VectorNode.CornerAt(P(0, 5))], IsClosed = true },
                new VectorSubpath { Nodes = [VectorNode.CornerAt(P(20, 20))], IsClosed = false }, // no shape
                new VectorSubpath { Nodes = [VectorNode.CornerAt(P(30, 0)), VectorNode.CornerAt(P(35, 0)), VectorNode.CornerAt(P(30, 5))], IsClosed = true },
            ],
        };
        var obj = VectorPathSceneFactory.Create(path, Red, "multi");
        Assert.Equal(2, obj.LocalShapes.Count);
        var layerA = Guid.NewGuid();
        var layerB = Guid.NewGuid();
        obj.LocalShapes = [obj.LocalShapes[0] with { LayerId = layerA }, obj.LocalShapes[1] with { LayerId = layerB }];

        var extended = path.ReplaceSubpath(1,
            new VectorSubpath { Nodes = [VectorNode.CornerAt(P(20, 20)), VectorNode.CornerAt(P(25, 25))], IsClosed = false });
        var rebuilt = VectorPathSceneFactory.Rebuild(obj, extended);

        Assert.Equal(3, rebuilt.LocalShapes.Count);
        Assert.Equal(layerA, rebuilt.LocalShapes[0].LayerId);
        Assert.Equal(layerB, rebuilt.LocalShapes[2].LayerId);
    }

    // 9 / 10 / weld ---------------------------------------------------------------------------

    private static (SceneViewModel Vm, SceneObject A, SceneObject B) TwoOpenLines(double gap)
    {
        var vm = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez");
        var a = VectorPathSceneFactory.Create(VectorPath.SingleOpen([VectorNode.CornerAt(P(0, 0)), VectorNode.CornerAt(P(10, 0))]), Red, "A");
        var b = VectorPathSceneFactory.Create(VectorPath.SingleOpen([VectorNode.CornerAt(P(10 + gap, 0)), VectorNode.CornerAt(P(20, 0))]), Red, "B");
        vm.Execute(new AddObjectCommand(vm.Scene, a, [layer]));
        vm.Execute(new AddObjectCommand(vm.Scene, b, [layer]));
        return (vm, a, b);
    }

    [Fact]
    public void WeldJoinsCoincidentOpenEndpointsInOneUndoStep()
    {
        var (vm, a, b) = TwoOpenLines(0.02);
        vm.SelectedObjects.ReplaceWith([a, b]);
        Assert.True(vm.CanWeldSelection);

        vm.WeldSelectionCommand.Execute(null);

        var welded = Assert.Single(vm.Objects);
        Assert.Equal(3, welded.VectorPath!.Subpaths.Single().Nodes.Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Objects.Count);
    }

    [Fact]
    public void WeldReportsWhenNoEndpointsTouch()
    {
        var (vm, a, b) = TwoOpenLines(5);
        vm.SelectedObjects.ReplaceWith([a, b]);
        string? message = null;
        vm.VectorOperationRejected += text => message = text;

        vm.WeldSelectionCommand.Execute(null);

        Assert.NotNull(message);
        Assert.Equal(2, vm.Objects.Count);
    }

    [Fact]
    public void JoinRefusesLockedTargetsAndKeepsGroupMembership()
    {
        var (vm, a, b) = TwoOpenLines(0);
        b.IsLocked = true;
        Assert.False(vm.JoinObjectEndpoints(a, false, b, true));
        Assert.Equal(2, vm.Objects.Count);

        b.IsLocked = false;
        var group = Guid.NewGuid();
        a.GroupId = group;
        Assert.True(vm.JoinObjectEndpoints(a, false, b, true));
        Assert.Equal(group, vm.Objects.Single().GroupId);
    }

    [Fact]
    public void TwoOpenSubpathsOfOnePathJoinIntoOne()
    {
        var path = new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath { Nodes = [VectorNode.CornerAt(P(0, 0)), VectorNode.CornerAt(P(10, 0))] },
                new VectorSubpath { Nodes = [VectorNode.CornerAt(P(10, 0)), VectorNode.CornerAt(P(10, 10))] },
            ],
        };

        var joined = VectorPathEditor.JoinSubpathsAtEndpoints(path, 0, false, 1, true);

        var subpath = Assert.Single(joined.Subpaths);
        Assert.Equal(3, subpath.Nodes.Count);
        Assert.Equal(P(10, 10), subpath.Nodes[^1].Anchor);
    }

    // 7 ---------------------------------------------------------------------------------------

    [Fact]
    public void SimplifyKeepsCurvesAsBezierNodesWithFewerNodes()
    {
        var circle = Circle(24, 30);

        var simplified = SceneViewModel.RefitCurvedSubpath(circle, VectorPath.DefaultFlattenToleranceMm);

        Assert.True(simplified.Nodes.Count < circle.Nodes.Count);
        Assert.Contains(simplified.Nodes, node => node.HasAnyHandle);
        foreach (var point in simplified.Flatten(0.01))
            Assert.InRange(Math.Sqrt(point.X * point.X + point.Y * point.Y), 30 - 0.2, 30 + 0.2);
    }

    // 11 --------------------------------------------------------------------------------------

    [Fact]
    public void SplitRegionsIgnoresSliversAndTouchingContours()
    {
        var square = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(P(0, 0)), VectorNode.CornerAt(P(10, 0)), VectorNode.CornerAt(P(10, 10)), VectorNode.CornerAt(P(0, 10))],
            IsClosed = true,
        };
        var sliver = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(P(20, 0)), VectorNode.CornerAt(P(25, 0)), VectorNode.CornerAt(P(30, 0))],
            IsClosed = true,
        };
        // A hole whose corner touches the outer boundary.
        var touchingHole = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(P(0, 5)), VectorNode.CornerAt(P(5, 8)), VectorNode.CornerAt(P(5, 2))],
            IsClosed = true,
        };

        var regions = CurveBooleanService.SplitRegions(new VectorPath { Subpaths = [square, sliver, touchingHole] });

        Assert.Single(regions);
    }

    // 16 --------------------------------------------------------------------------------------

    [Fact]
    public void LoadTimeReconciliationRebuildsShapesThatDisagreeWithTheNodes()
    {
        var obj = VectorPathSceneFactory.Create(Single(Circle(4, 10)), Red, "c");
        var layer = Guid.NewGuid();
        var staleShape = obj.LocalShapes[0] with { LayerId = layer, Points = [P(0, 0), P(1, 1), P(0, 0)] };
        obj.LocalShapes = [staleShape];
        obj.LocalBounds = new BoundingBox2D(0, 0, 1, 1);

        Assert.True(obj.ReconcileVectorGeometry());

        Assert.Equal(layer, obj.LocalShapes[0].LayerId);
        Assert.InRange(obj.LocalBounds.Width, 19.9, 20.1);
        Assert.False(obj.ReconcileVectorGeometry()); // now consistent: nothing more to do
        Assert.True(obj.WithPivotAtBoundsCenter().IsPivotAtBoundsCenter);
    }
}
