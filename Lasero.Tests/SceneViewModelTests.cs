using System.Drawing.Imaging;
using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Trace;
using Xunit;

namespace Lasero.Tests;

public class SceneViewModelTests
{
    private static SceneObject MakeSquareObject(double x = 0, double y = 0)
    {
        var shape = new ImportedShape
        {
            Points = new[]
            {
                new Position(0, 0, 0),
                new Position(20, 0, 0),
                new Position(20, 10, 0),
                new Position(0, 10, 0),
                new Position(0, 0, 0),
            },
            IsClosed = true,
            LayerColor = RgbColor.Red,
            PreferredMode = LayerMode.Cut,
        };

        return new SceneObject
        {
            LocalShapes = [shape],
            LocalPivot = new Position(10, 5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 20, 10),
            Transform = ObjectTransform.Identity with { X = x, Y = y },
        };
    }

    private static SceneObject MakeRasterObject()
    {
        var vector = MakeSquareObject();
        return new SceneObject
        {
            LocalShapes = vector.LocalShapes,
            LocalPivot = vector.LocalPivot,
            LocalBounds = vector.LocalBounds,
            RasterFilePath = "photo.png",
            RasterOptions = new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 },
        };
    }

    private static SceneObject MakeClosedObject(LayerSettings layer, double x = 0)
    {
        var item = MakeSquareObject(x);
        item.LocalShapes = item.LocalShapes.Select(shape => shape with
        {
            LayerId = layer.Id,
            LayerColor = layer.Color,
        }).ToList();
        return item;
    }

    [Fact]
    public void InvalidReplacementProjectCannotClearCurrentUnsavedScene()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(0, 0, 0), new Position(20, 10, 0));
        var original = Assert.Single(viewModel.Objects);
        var invalid = new LaseroProjectFile
        {
            Objects = [new ProjectObject { Name = "Poškozený objekt", Shapes = null! }],
            Layers = [],
        };

        Assert.Throws<InvalidDataException>(() => viewModel.LoadProject(invalid));
        Assert.Same(original, Assert.Single(viewModel.Objects));
    }

    [Fact]
    public void EditableVectorPathSurvivesProjectRoundTrip()
    {
        var viewModel = new SceneViewModel();
        var originalPath = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            new VectorNode(
                new Position(10, 0, 0),
                new Position(7, 4, 0),
                new Position(13, -4, 0),
                VectorNodeType.Smooth),
        ]);
        viewModel.AddVectorPath(originalPath);

        var saved = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(viewModel.CreateProject()));
        var reloaded = new SceneViewModel();
        reloaded.LoadProject(saved);

        var restored = Assert.Single(reloaded.Objects);
        var restoredSubpath = Assert.Single(Assert.IsType<VectorPath>(restored.VectorPath).Subpaths);
        Assert.Equal(originalPath.Subpaths[0].IsClosed, restoredSubpath.IsClosed);
        Assert.Equal(originalPath.Subpaths[0].Nodes, restoredSubpath.Nodes);
        Assert.True(restored.IsVectorPath);
    }

    [Fact]
    public void DrawingRectangleAddsASelectedUndoableVectorObject()
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(
            DesignerTool.Rectangle,
            new Position(50, 10, 0),
            new Position(20, 40, 0));

        var rectangle = Assert.Single(viewModel.Objects);
        Assert.Equal("Obdélník", rectangle.Name);
        Assert.Equal(new BoundingBox2D(20, 10, 50, 40), rectangle.WorldBounds());
        Assert.Equal(35, viewModel.SelectedX, precision: 6);
        Assert.Equal(25, viewModel.SelectedY, precision: 6);
        Assert.Equal(30, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(30, viewModel.SelectedHeight, precision: 6);
        Assert.Same(rectangle, Assert.Single(viewModel.SelectedObjects));
        Assert.Single(viewModel.Layers);

        viewModel.UndoCommand.Execute(null);
        Assert.Empty(viewModel.Objects);
    }

    [Fact]
    public void UniteSelectionUsesTopmostVisibleSelectedLayerInsteadOfClickOrder()
    {
        var viewModel = new SceneViewModel();
        var bottomLayer = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Spodní");
        var topLayer = LayerSettings.CreateDefault(new RgbColor(0, 95, 255), LayerMode.Cut, "Horní");
        viewModel.Layers.Add(bottomLayer);
        viewModel.Layers.Add(topLayer);
        var bottom = MakeClosedObject(bottomLayer);
        var top = MakeClosedObject(topLayer, 10);
        viewModel.Objects.Add(bottom);
        viewModel.Objects.Add(top);
        viewModel.SelectedObjects.Add(bottom);
        viewModel.SelectedObjects.Add(top);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.All(result.LocalShapes, shape => Assert.Equal(topLayer.Id, shape.LayerId));
        Assert.Same(result, Assert.Single(viewModel.SelectedObjects));
    }

    [Fact]
    public void UniteSelectionKeepsOriginalsAndExplainsInvalidGeometry()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var invalidShapes = new[]
        {
            new ImportedShape
            {
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = LayerMode.Cut,
                IsClosed = true,
                Points = [new Position(0, 0, 0), new Position(5, 0, 0), new Position(10, 0, 0)],
            },
            new ImportedShape
            {
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = LayerMode.Cut,
                IsClosed = true,
                Points = [new Position(0, 1, 0), new Position(5, 1, 0), new Position(10, 1, 0)],
            },
        };
        var source = new SceneObject
        {
            Name = "Neplatný vektor",
            LocalShapes = invalidShapes,
            LocalPivot = new Position(5, 0.5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 10, 1),
        };
        viewModel.Objects.Add(source);
        viewModel.SelectedObjects.Add(source);
        string? explanation = null;
        viewModel.VectorOperationRejected += message => explanation = message;

        viewModel.UniteSelectionCommand.Execute(null);

        Assert.Same(source, Assert.Single(viewModel.Objects));
        Assert.Same(source, Assert.Single(viewModel.SelectedObjects));
        Assert.NotNull(explanation);
        Assert.Contains("Vektory nebyly změněny", explanation, StringComparison.Ordinal);
    }

    /// <summary>Flattening the object's VectorPath must reproduce exactly the polygon(s) already in
    /// its LocalShapes — the one-directional "VectorPath is authoritative, LocalShapes is a cached
    /// render of it" contract every other vector-producing path in the codebase (VectorPathSceneFactory,
    /// BuildOffsetObject) already follows. A closed shape's LocalShapes repeats its first point as its
    /// last (see ScenePrimitiveFactory); the VectorPath's flattened output does too, since
    /// VectorSubpath.Flatten always re-closes a closed subpath by wrapping back to Nodes[0].</summary>
    private static void AssertVectorPathMatchesLocalShapes(SceneObject result)
    {
        Assert.True(result.IsVectorPath);
        var path = Assert.IsType<VectorPath>(result.VectorPath);
        var flattened = path.FlattenAll();
        Assert.Equal(result.LocalShapes.Count, flattened.Count);
        for (var index = 0; index < flattened.Count; index++)
        {
            Assert.Equal(result.LocalShapes[index].IsClosed, path.Subpaths[index].IsClosed);
            Assert.Equal(result.LocalShapes[index].Points, flattened[index]);
        }
    }

    [Fact]
    public void GroupSelectionProducesANodeEditableVectorPath()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var back = MakeClosedObject(layer);
        var front = MakeClosedObject(layer, 30);
        viewModel.Objects.Add(back);
        viewModel.Objects.Add(front);
        viewModel.SelectedObjects.Add(back);
        viewModel.SelectedObjects.Add(front);

        viewModel.GroupSelectionCommand.Execute(null);

        var group = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(group);
    }

    [Fact]
    public void UngroupSelectionProducesNodeEditableVectorPathParts()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create("e", Position.Zero, 40, new RgbColor(52, 52, 52));
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        viewModel.UngroupSelectionCommand.Execute(null);

        Assert.Equal(2, viewModel.Objects.Count);
        foreach (var part in viewModel.Objects)
            AssertVectorPathMatchesLocalShapes(part);
    }

    // -----------------------------------------------------------------------------------------
    // Curve-preserving Group/Ungroup (docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md
    // addendum's proposal) — Group must carry each source's REAL Bezier geometry into the
    // combined VectorPath (not flatten it to straight segments the way the plain VectorPath-
    // editability fix above does for sources that have no curve data), and Ungroup must recover
    // each part's exact original subpath.
    // -----------------------------------------------------------------------------------------

    private static SceneObject MakeCurvedVectorObject(double x = 0, double y = 0, Guid? layerId = null)
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            new VectorNode(
                new Position(20, 0, 0),
                new Position(15, 8, 0),
                new Position(25, -8, 0),
                VectorNodeType.Smooth),
        ]);
        var obj = VectorPathSceneFactory.Create(path, RgbColor.Black, "Křivka");
        if (layerId is { } id)
            obj.LocalShapes = obj.LocalShapes.Select(shape => shape with { LayerId = id }).ToList();
        obj.Transform = obj.Transform with { X = x, Y = y };
        return obj;
    }

    [Fact]
    public void GroupThenUngroupPreservesTheOriginalCurveExactlyNotFlattenedToStraightSegments()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject();
        var straight = MakeSquareObject(40);
        viewModel.Objects.Add(curve);
        viewModel.Objects.Add(straight);
        viewModel.SelectedObjects.Add(curve);
        viewModel.SelectedObjects.Add(straight);
        var originalWorldCurveSubpath = curve.GetWorldVectorPath()!.Subpaths[0];

        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(group);

        viewModel.SelectedObjects.Clear();
        viewModel.SelectedObjects.Add(group);
        viewModel.UngroupSelectionCommand.Execute(null);

        var parts = viewModel.Objects.ToList();
        Assert.Equal(2, parts.Count);
        foreach (var part in parts) AssertVectorPathMatchesLocalShapes(part);

        var recoveredCurve = Assert.Single(parts, part => part.VectorPath!.Subpaths[0].Nodes.Any(n => n.HasAnyHandle));
        var recoveredWorldSubpath = recoveredCurve.GetWorldVectorPath()!.Subpaths[0];
        Assert.Equal(originalWorldCurveSubpath.Nodes.Count, recoveredWorldSubpath.Nodes.Count);
        for (var i = 0; i < originalWorldCurveSubpath.Nodes.Count; i++)
        {
            var expected = originalWorldCurveSubpath.Nodes[i];
            var actual = recoveredWorldSubpath.Nodes[i];
            Assert.Equal(expected.Type, actual.Type);
            AssertPositionApproximately(expected.Anchor, actual.Anchor);
            Assert.Equal(expected.HandleIn is not null, actual.HandleIn is not null);
            Assert.Equal(expected.HandleOut is not null, actual.HandleOut is not null);
            if (expected.HandleIn is { } hi) AssertPositionApproximately(hi, actual.HandleIn!.Value);
            if (expected.HandleOut is { } ho) AssertPositionApproximately(ho, actual.HandleOut!.Value);
        }
    }

    private static void AssertPositionApproximately(Position expected, Position actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
    }

    [Fact]
    public void GroupThenUngroupSurvivesACompoundPathWithAHole()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create("e", Position.Zero, 40, new RgbColor(52, 52, 52));
        var compoundId = text.LocalShapes.Select(shape => shape.GeometrySetId).Distinct().Single();
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        // Ungroup first to get two standalone parts (outer glyph contour + counter hole), matching
        // UngroupGroupAndUnitePreservesTextCounter's own precedent, then re-group and ungroup again
        // to exercise THIS fix's own round trip.
        viewModel.UngroupSelectionCommand.Execute(null);
        var initialParts = viewModel.Objects.ToList();
        Assert.Equal(2, initialParts.Count);

        viewModel.SelectedObjects.Clear();
        foreach (var part in initialParts) viewModel.SelectedObjects.Add(part);
        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(group);
        Assert.Equal(2, group.VectorPath!.Subpaths.Count);

        viewModel.SelectedObjects.Clear();
        viewModel.SelectedObjects.Add(group);
        viewModel.UngroupSelectionCommand.Execute(null);

        var finalParts = viewModel.Objects.ToList();
        Assert.Equal(2, finalParts.Count);
        foreach (var part in finalParts) AssertVectorPathMatchesLocalShapes(part);
        // Both parts still share one GeometrySetId, so a later Union recombines the outer contour
        // with its counter instead of treating them as unrelated overlapping shapes.
        Assert.Equal(compoundId,
            Assert.Single(finalParts.SelectMany(part => part.LocalShapes).Select(s => s.GeometrySetId).Distinct()));
    }

    [Fact]
    public void GroupThenUngroupPreservesATransformedSourcesWorldGeometry()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject(x: 50, y: -30);
        curve.Transform = curve.Transform with { RotationDeg = 40, ScaleX = 1.5, ScaleY = 1.5 };
        var expectedWorldSubpath = curve.GetWorldVectorPath()!.Subpaths[0];
        var square = MakeSquareObject(x: -20);
        viewModel.Objects.Add(curve);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(curve);
        viewModel.SelectedObjects.Add(square);

        viewModel.GroupSelectionCommand.Execute(null);
        viewModel.SelectedObjects.Clear();
        viewModel.SelectedObjects.Add(Assert.Single(viewModel.Objects));
        viewModel.UngroupSelectionCommand.Execute(null);

        var recoveredCurve = Assert.Single(viewModel.Objects,
            part => part.VectorPath!.Subpaths[0].Nodes.Any(n => n.HasAnyHandle));
        var actualWorldSubpath = recoveredCurve.GetWorldVectorPath()!.Subpaths[0];
        for (var i = 0; i < expectedWorldSubpath.Nodes.Count; i++)
            AssertPositionApproximately(expectedWorldSubpath.Nodes[i].Anchor, actualWorldSubpath.Nodes[i].Anchor);
    }

    [Fact]
    public void UndoGroupRestoresBothOriginalObjectsWithTheirExactVectorPaths()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject();
        var originalPath = curve.VectorPath;
        var square = MakeSquareObject(40);
        viewModel.Objects.Add(curve);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(curve);
        viewModel.SelectedObjects.Add(square);

        viewModel.GroupSelectionCommand.Execute(null);
        Assert.Single(viewModel.Objects);

        viewModel.UndoCommand.Execute(null);

        var restored = viewModel.Objects.ToList();
        Assert.Equal(2, restored.Count);
        var restoredCurve = Assert.Single(restored, o => o.IsVectorPath && o.VectorPath!.Subpaths[0].Nodes.Any(n => n.HasAnyHandle));
        Assert.Equal(originalPath, restoredCurve.VectorPath);
    }

    [Fact]
    public void RedoGroupReappliesTheCurvePreservingGroupResult()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject();
        var square = MakeSquareObject(40);
        viewModel.Objects.Add(curve);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(curve);
        viewModel.SelectedObjects.Add(square);
        viewModel.GroupSelectionCommand.Execute(null);
        var subpathsBefore = Assert.Single(viewModel.Objects).VectorPath!.Subpaths.Count;

        viewModel.UndoCommand.Execute(null);
        viewModel.RedoCommand.Execute(null);

        var group = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(group);
        Assert.Equal(subpathsBefore, group.VectorPath!.Subpaths.Count);
        Assert.Contains(group.VectorPath!.Subpaths, s => s.Nodes.Any(n => n.HasAnyHandle));
    }

    [Fact]
    public void UndoUngroupRestoresTheExactPreUngroupGroupObject()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject();
        var square = MakeSquareObject(40);
        viewModel.Objects.Add(curve);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(curve);
        viewModel.SelectedObjects.Add(square);
        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.Objects);
        var groupPath = group.VectorPath;

        viewModel.SelectedObjects.Clear();
        viewModel.SelectedObjects.Add(group);
        viewModel.UngroupSelectionCommand.Execute(null);
        Assert.Equal(2, viewModel.Objects.Count);

        viewModel.UndoCommand.Execute(null);

        var restoredGroup = Assert.Single(viewModel.Objects);
        Assert.Equal(groupPath, restoredGroup.VectorPath);
    }

    [Fact]
    public void GroupPreservesLayerIdFromEachSource()
    {
        var viewModel = new SceneViewModel();
        var layerA = Guid.NewGuid();
        var layerB = Guid.NewGuid();
        var curveA = MakeCurvedVectorObject(layerId: layerA);
        var curveB = MakeCurvedVectorObject(x: 100, layerId: layerB);
        viewModel.Objects.Add(curveA);
        viewModel.Objects.Add(curveB);
        viewModel.SelectedObjects.Add(curveA);
        viewModel.SelectedObjects.Add(curveB);

        viewModel.GroupSelectionCommand.Execute(null);

        var group = Assert.Single(viewModel.Objects);
        Assert.Contains(group.LocalShapes, shape => shape.LayerId == layerA);
        Assert.Contains(group.LocalShapes, shape => shape.LayerId == layerB);
    }

    [Fact]
    public void GroupGivesEachSourceItsOwnGeometrySetIdWhenSourcesHadNone()
    {
        var viewModel = new SceneViewModel();
        var curveA = MakeCurvedVectorObject();
        var curveB = MakeCurvedVectorObject(x: 100);
        viewModel.Objects.Add(curveA);
        viewModel.Objects.Add(curveB);
        viewModel.SelectedObjects.Add(curveA);
        viewModel.SelectedObjects.Add(curveB);

        viewModel.GroupSelectionCommand.Execute(null);

        var group = Assert.Single(viewModel.Objects);
        Assert.Equal(2, group.LocalShapes.Select(shape => shape.GeometrySetId).Distinct().Count());
        Assert.All(group.LocalShapes, shape => Assert.NotEqual(Guid.Empty, shape.GeometrySetId));
    }

    /// <summary>Duplicating (SceneObject.Clone, shares LocalShapes/VectorPath by reference per its own
    /// doc comment — both are immutable record types so sharing is intentional and safe) and then
    /// Grouping must never let an edit to one object's VectorPath reach back and mutate another's.
    /// VectorPath/VectorSubpath/VectorNode are all immutable records, so this is really asserting the
    /// group/duplicate code paths never mutate a node in place — RecenterVectorPath/BuildCombinedWorld
    /// VectorPath both build new lists rather than mutating shared ones, which this pins down.</summary>
    [Fact]
    public void DuplicateThenGroupDoesNotShareMutableVectorPathStateBetweenObjects()
    {
        var viewModel = new SceneViewModel();
        var curve = MakeCurvedVectorObject();
        viewModel.Objects.Add(curve);
        viewModel.SelectedObjects.Add(curve);

        viewModel.DuplicateCommand.Execute(null);
        var duplicate = Assert.Single(viewModel.SelectedObjects);
        Assert.NotSame(curve, duplicate);

        var square = MakeSquareObject(60);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(square);
        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.SelectedObjects);

        // The original (never grouped) and the duplicate-turned-group-member must each still report
        // their own independent, correct anchors — proving no shared mutable list/array was reused.
        Assert.Equal(new Position(0, 0, 0), curve.VectorPath!.Subpaths[0].Nodes[0].Anchor);
        Assert.Contains(group.VectorPath!.Subpaths, s => s.Nodes.Any(n => n.HasAnyHandle));
    }

    // -----------------------------------------------------------------------------------------
    // Cross-object endpoint join (SceneViewModel.JoinObjectEndpoints) — Lasero.Core's own
    // VectorPathEditor.JoinAtEndpoints/ReverseSubpath are covered directly in
    // VectorPathJoinTests.cs; these pin the world-space/metadata/undo wiring on top of it.
    // -----------------------------------------------------------------------------------------

    private static SceneObject MakeOpenLineObject(Position start, Position end, double x = 0, double y = 0, Guid? layerId = null)
    {
        var path = VectorPath.SingleOpen([VectorNode.CornerAt(start), VectorNode.CornerAt(end)]);
        var obj = VectorPathSceneFactory.Create(path, RgbColor.Black, "Čára");
        if (layerId is { } id)
            obj.LocalShapes = obj.LocalShapes.Select(shape => shape with { LayerId = id }).ToList();
        obj.Transform = obj.Transform with { X = x, Y = y };
        return obj;
    }

    [Theory]
    [InlineData(false, true)]  // A.end -> B.start
    [InlineData(false, false)] // A.end -> B.end
    [InlineData(true, true)]   // A.start -> B.start
    [InlineData(true, false)]  // A.start -> B.end
    public void JoinObjectEndpointsMergesTwoObjectsIntoOneEditablePathForEveryEndpointCombination(
        bool draggedAtStart, bool targetAtStart)
    {
        var viewModel = new SceneViewModel();
        // Place each object's join-side endpoint at world (10,0,0) regardless of which combination
        // is under test, so every case is asserting the same geometric outcome.
        var a = draggedAtStart
            ? MakeOpenLineObject(new Position(10, 0, 0), new Position(0, 0, 0))
            : MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        var b = targetAtStart
            ? MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 5, 0))
            : MakeOpenLineObject(new Position(20, 5, 0), new Position(10, 0, 0));
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        var joined = viewModel.JoinObjectEndpoints(a, draggedAtStart, b, targetAtStart);

        Assert.True(joined);
        var result = Assert.Single(viewModel.Objects);
        Assert.True(result.IsVectorPath);
        AssertVectorPathMatchesLocalShapes(result);
        var worldSubpath = result.GetWorldVectorPath()!.Subpaths[0];
        Assert.Equal(3, worldSubpath.Nodes.Count);
        Assert.False(worldSubpath.IsClosed);
        var anchors = worldSubpath.Nodes.Select(n => n.Anchor).ToList();
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(0, 0, 0)));
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(10, 0, 0)));
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(20, 5, 0)));
        Assert.Same(result, Assert.Single(viewModel.SelectedObjects));
    }

    private static bool PositionsApproximatelyEqual(Position a, Position b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    [Fact]
    public void JoinObjectEndpointsAdoptsTheDraggedObjectsLayerIdNotTheTargets()
    {
        var viewModel = new SceneViewModel();
        var draggedLayer = Guid.NewGuid();
        var targetLayer = Guid.NewGuid();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0), layerId: draggedLayer);
        var b = MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 0, 0), layerId: targetLayer);
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);

        var result = Assert.Single(viewModel.Objects);
        Assert.All(result.LocalShapes, shape => Assert.Equal(draggedLayer, shape.LayerId));
    }

    [Fact]
    public void JoinObjectEndpointsIsUndoableAndRestoresBothOriginalObjectsExactly()
    {
        var viewModel = new SceneViewModel();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        var b = MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 0, 0));
        var originalPathA = a.VectorPath;
        var originalPathB = b.VectorPath;
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);
        Assert.Single(viewModel.Objects);

        viewModel.UndoCommand.Execute(null);

        var restored = viewModel.Objects.ToList();
        Assert.Equal(2, restored.Count);
        Assert.Contains(restored, o => Equals(o.VectorPath, originalPathA));
        Assert.Contains(restored, o => Equals(o.VectorPath, originalPathB));
    }

    [Fact]
    public void JoinObjectEndpointsRefusesAClosedSubpath()
    {
        var viewModel = new SceneViewModel();
        var closedPath = new VectorPath
        {
            Subpaths = [new VectorSubpath
            {
                Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0)), VectorNode.CornerAt(new Position(10, 10, 0))],
                IsClosed = true,
            }],
        };
        var a = VectorPathSceneFactory.Create(closedPath, RgbColor.Black, "Uzavřená");
        var b = MakeOpenLineObject(new Position(20, 0, 0), new Position(30, 0, 0));
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        var joined = viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);

        Assert.False(joined);
        Assert.Equal(2, viewModel.Objects.Count);
    }

    [Fact]
    public void JoinObjectEndpointsWorksWhenTheDraggedObjectIsRotatedAndScaled()
    {
        var viewModel = new SceneViewModel();
        // A's own local endpoint is NOT at world (10,0,0) -- its Transform is what puts it there, so
        // this only passes if JoinObjectEndpoints truly works in world space (GetWorldVectorPath)
        // rather than naively joining local-space nodes.
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(1, 0, 0));
        a.Transform = a.Transform with { RotationDeg = 0, ScaleX = 10, ScaleY = 10 };
        Assert.Equal(new Position(10, 0, 0), a.GetWorldVectorPath()!.Subpaths[0].Nodes[1].Anchor);
        var b = MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 0, 0));
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        var joined = viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);

        Assert.True(joined);
        var result = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(result);
        var anchors = result.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(n => n.Anchor).ToList();
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(0, 0, 0)));
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(10, 0, 0)));
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(20, 0, 0)));
    }

    [Fact]
    public void JoinObjectEndpointsWorksWhenTheTargetObjectIsRotated()
    {
        var viewModel = new SceneViewModel();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        // B's local geometry runs straight up the Y axis; a 90 degree rotation puts its own start at
        // world (10,0,0), matching A's end, without the test needing to hand-compute rotated points.
        var b = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        b.Transform = b.Transform with { RotationDeg = 90 };
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);
        var bWorldStart = b.GetWorldVectorPath()!.Subpaths[0].Nodes[0].Anchor;
        // Re-anchor B so its rotated world start lands exactly on A's end (10,0,0) — isolates
        // "does rotation carry through the join" from "did the test line up the fixture by hand".
        b.Transform = b.Transform with { X = b.Transform.X + (10 - bWorldStart.X), Y = b.Transform.Y + (0 - bWorldStart.Y) };

        var joined = viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);

        Assert.True(joined);
        var result = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(result);
        var anchors = result.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(n => n.Anchor).ToList();
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(0, 0, 0)));
        Assert.Contains(anchors, p => PositionsApproximatelyEqual(p, new Position(10, 0, 0)));
    }

    [Fact]
    public void JoinObjectEndpointsAdoptsTheDraggedObjectsGeometrySetIdOverTheTargets()
    {
        var viewModel = new SceneViewModel();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        var draggedGeometrySetId = Guid.NewGuid();
        a.LocalShapes = a.LocalShapes.Select(shape => shape with { GeometrySetId = draggedGeometrySetId }).ToList();
        var b = MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 0, 0));
        b.LocalShapes = b.LocalShapes.Select(shape => shape with { GeometrySetId = Guid.NewGuid() }).ToList();
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);

        var result = Assert.Single(viewModel.Objects);
        Assert.All(result.LocalShapes, shape => Assert.Equal(draggedGeometrySetId, shape.GeometrySetId));
    }

    [Fact]
    public void RedoJoinObjectEndpointsReappliesTheMerge()
    {
        var viewModel = new SceneViewModel();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        var b = MakeOpenLineObject(new Position(10, 0, 0), new Position(20, 0, 0));
        viewModel.Objects.Add(a);
        viewModel.Objects.Add(b);

        viewModel.JoinObjectEndpoints(a, draggedAtStart: false, b, targetAtStart: true);
        Assert.Single(viewModel.Objects);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(2, viewModel.Objects.Count);

        viewModel.RedoCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(result);
        Assert.Equal(3, result.VectorPath!.Subpaths[0].Nodes.Count);
    }

    [Fact]
    public void JoinObjectEndpointsIsANoOpWhenTheSameObjectIsPassedForBothSides()
    {
        var viewModel = new SceneViewModel();
        var a = MakeOpenLineObject(new Position(0, 0, 0), new Position(10, 0, 0));
        viewModel.Objects.Add(a);

        var joined = viewModel.JoinObjectEndpoints(a, draggedAtStart: false, a, targetAtStart: true);

        Assert.False(joined);
        Assert.Single(viewModel.Objects);
    }

    [Fact]
    public void UniteSelectionDisabledReasonIsNullWhenTwoClosedVectorsAreSelected()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        Assert.Null(viewModel.UniteSelectionDisabledReason);
        Assert.True(viewModel.CanUniteSelection);
    }

    [Fact]
    public void UniteSelectionDisabledReasonExplainsEmptySelection()
    {
        var viewModel = new SceneViewModel();

        Assert.Equal("Nic není vybráno.", viewModel.UniteSelectionDisabledReason);
        Assert.False(viewModel.CanUniteSelection);
    }

    [Fact]
    public void UniteSelectionDisabledReasonExplainsASingleShape()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var single = MakeClosedObject(layer);
        viewModel.Objects.Add(single);
        viewModel.SelectedObjects.Add(single);

        Assert.Equal("Vyberte alespoň dva tvary.", viewModel.UniteSelectionDisabledReason);
    }

    [Fact]
    public void UniteSelectionDisabledReasonExplainsARasterSelection()
    {
        var viewModel = new SceneViewModel();
        var raster = MakeRasterObject();
        viewModel.Objects.Add(raster);
        viewModel.SelectedObjects.Add(raster);

        Assert.Equal("Bitmapu nelze sjednotit ani kombinovat — vyberte pouze vektory.", viewModel.UniteSelectionDisabledReason);
    }

    [Fact]
    public void UniteSelectionDisabledReasonExplainsALockedSelection()
    {
        var (viewModel, back, _) = MakeOverlappingSquaresFixture();
        back.IsLocked = true;

        Assert.Equal("Zamknuté objekty nelze sjednotit ani kombinovat — nejprve je odemkněte.", viewModel.UniteSelectionDisabledReason);
    }

    [Fact]
    public void UniteSelectionDisabledReasonExplainsAnOpenSubpath()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var closed = MakeClosedObject(layer);
        var open = MakeClosedObject(layer, 30);
        open.LocalShapes = open.LocalShapes.Select(shape => shape with { IsClosed = false }).ToList();
        viewModel.Objects.Add(closed);
        viewModel.Objects.Add(open);
        viewModel.SelectedObjects.Add(closed);
        viewModel.SelectedObjects.Add(open);

        Assert.Equal(
            "Výběr obsahuje otevřenou dráhu — booleovské operace vyžadují uzavřené tvary.",
            viewModel.UniteSelectionDisabledReason);
    }

    [Fact]
    public void OffsetSelectionDisabledReasonIsNullForASingleClosedVector()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var single = MakeClosedObject(layer);
        viewModel.Objects.Add(single);
        viewModel.SelectedObjects.Add(single);

        Assert.Null(viewModel.OffsetSelectionDisabledReason);
        Assert.True(viewModel.CanOffsetSelection);
    }

    [Fact]
    public void OffsetSelectionDisabledReasonExplainsARasterSelection()
    {
        var viewModel = new SceneViewModel();
        var raster = MakeRasterObject();
        viewModel.Objects.Add(raster);
        viewModel.SelectedObjects.Add(raster);

        Assert.Equal("Bitmapu nelze posunout offsetem — vyberte vektor.", viewModel.OffsetSelectionDisabledReason);
    }

    [Fact]
    public void OffsetSelectionDisabledReasonExplainsALockedSelection()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var single = MakeClosedObject(layer);
        single.IsLocked = true;
        viewModel.Objects.Add(single);
        viewModel.SelectedObjects.Add(single);

        Assert.Equal("Zamknuté objekty nelze upravit offsetem — nejprve je odemkněte.", viewModel.OffsetSelectionDisabledReason);
    }

    [Fact]
    public void UniteSelectionProducesANodeEditableVectorPathResult()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(result);
    }

    [Fact]
    public void SubtractSelectionProducesANodeEditableVectorPathResult()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        viewModel.SubtractSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        AssertVectorPathMatchesLocalShapes(result);
    }

    [Fact]
    public void GroupThenUniteDoesNotCancelOverlappingObjects()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var originals = Enumerable.Range(0, 4)
            .Select(_ => MakeClosedObject(layer))
            .ToList();
        foreach (var item in originals)
        {
            viewModel.Objects.Add(item);
            viewModel.SelectedObjects.Add(item);
        }

        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.Objects);
        Assert.Equal(4, group.LocalShapes.Select(shape => shape.GeometrySetId).Distinct().Count());

        string? explanation = null;
        viewModel.VectorOperationRejected += message => explanation = message;
        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Null(explanation);
        Assert.Equal(20, result.WorldBounds().Width, precision: 6);
        Assert.Equal(10, result.WorldBounds().Height, precision: 6);
        Assert.Single(result.LocalShapes.Select(shape => shape.GeometrySetId).Distinct());
    }

    [Fact]
    public void UngroupGroupAndUnitePreservesTextCounter()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create(
            "e",
            Position.Zero,
            40,
            new RgbColor(52, 52, 52));
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        viewModel.UngroupSelectionCommand.Execute(null);

        var parts = viewModel.Objects.ToList();
        Assert.Equal(2, parts.Count);
        var compoundId = Assert.Single(parts
            .SelectMany(part => part.LocalShapes)
            .Select(shape => shape.GeometrySetId)
            .Distinct());
        Assert.NotEqual(Guid.Empty, compoundId);

        viewModel.SelectedObjects.Clear();
        foreach (var part in parts)
            viewModel.SelectedObjects.Add(part);
        viewModel.GroupSelectionCommand.Execute(null);
        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Equal(2, result.LocalShapes.Count);
    }

    [Fact]
    public void UniteRepairsCounterInLegacyTextGroupWithSeparateGeometryIds()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create(
            "e",
            Position.Zero,
            40,
            new RgbColor(52, 52, 52));
        text.LocalShapes = text.LocalShapes
            .Select(shape => shape with { GeometrySetId = Guid.NewGuid() })
            .ToList();
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Equal(2, result.LocalShapes.Count);
    }

    // Shared fixture for the three tests below: two 20x10 squares, back at x=0 (world x:[0,20]),
    // front at x=10 (world x:[10,30]) — a 10x10 overlap. Added to Objects back-then-front, matching
    // CombineSelection's own "process by scene z-order, back to front" convention.
    private static (SceneViewModel ViewModel, SceneObject Back, SceneObject Front) MakeOverlappingSquaresFixture()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var back = MakeClosedObject(layer);
        var front = MakeClosedObject(layer, 10);
        viewModel.Objects.Add(back);
        viewModel.Objects.Add(front);
        viewModel.SelectedObjects.Add(back);
        viewModel.SelectedObjects.Add(front);
        return (viewModel, back, front);
    }

    private static void AssertBoundsApproximately(BoundingBox2D expected, BoundingBox2D actual)
    {
        // Geometry.Combine flattens through a 0.002mm tolerance (BooleanGeometryToleranceMm), so an
        // exact comparison is too strict here — precision: 2 (0.01mm) comfortably absorbs that while
        // still catching a real shape error.
        Assert.Equal(expected.MinX, actual.MinX, precision: 2);
        Assert.Equal(expected.MinY, actual.MinY, precision: 2);
        Assert.Equal(expected.MaxX, actual.MaxX, precision: 2);
        Assert.Equal(expected.MaxY, actual.MaxY, precision: 2);
    }

    [Fact]
    public void SubtractSelectionRemovesTheFrontShapesAreaFromTheBackShape()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        viewModel.SubtractSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Odečtený vektor", result.Name);
        AssertBoundsApproximately(new BoundingBox2D(0, 0, 10, 10), result.WorldBounds());
    }

    [Fact]
    public void IntersectSelectionKeepsOnlyTheOverlappingArea()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        viewModel.IntersectSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Průnik vektorů", result.Name);
        AssertBoundsApproximately(new BoundingBox2D(10, 0, 20, 10), result.WorldBounds());
    }

    [Fact]
    public void IntersectSelectionTreatsDisconnectedFiguresInOneObjectAsOneSource()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);

        ImportedShape Rectangle(double minX, double minY, double maxX, double maxY) => new()
        {
            GeometrySetId = Guid.NewGuid(),
            LayerId = layer.Id,
            LayerColor = layer.Color,
            PreferredMode = LayerMode.Cut,
            IsClosed = true,
            Points =
            [
                new Position(minX, minY, 0), new Position(maxX, minY, 0),
                new Position(maxX, maxY, 0), new Position(minX, maxY, 0),
                new Position(minX, minY, 0),
            ],
        };

        var disconnectedBack = new SceneObject
        {
            Name = "Dvě části",
            LocalShapes = [Rectangle(0, 0, 10, 10), Rectangle(20, 0, 30, 10)],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(0, 0, 30, 10),
            Transform = ObjectTransform.Identity,
        };
        var front = new SceneObject
        {
            Name = "Pruh",
            LocalShapes = [Rectangle(0, 2, 30, 8)],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(0, 2, 30, 8),
            Transform = ObjectTransform.Identity,
        };
        viewModel.Objects.Add(disconnectedBack);
        viewModel.Objects.Add(front);
        viewModel.SelectedObjects.Add(disconnectedBack);
        viewModel.SelectedObjects.Add(front);

        viewModel.IntersectSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal(2, result.LocalShapes.Count);
        AssertBoundsApproximately(new BoundingBox2D(0, 2, 30, 8), result.WorldBounds());
    }

    [Fact]
    public void ExcludeSelectionKeepsBothNonOverlappingPartsAsSeparateContours()
    {
        var (viewModel, _, _) = MakeOverlappingSquaresFixture();

        viewModel.ExcludeSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Vyloučený vektor", result.Name);
        Assert.Equal(2, result.LocalShapes.Count);
        AssertBoundsApproximately(new BoundingBox2D(0, 0, 30, 10), result.WorldBounds());
    }

    private static double SignedArea(IReadOnlyList<Position> points)
    {
        if (points.Count < 3) return 0;
        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2;
    }

    private static ImportedShape Rectangle(Guid geometrySetId, LayerSettings layer, double minX, double minY, double maxX, double maxY) => new()
    {
        GeometrySetId = geometrySetId,
        LayerId = layer.Id,
        LayerColor = layer.Color,
        PreferredMode = LayerMode.Cut,
        IsClosed = true,
        Points =
        [
            new Position(minX, minY, 0), new Position(maxX, minY, 0),
            new Position(maxX, maxY, 0), new Position(minX, maxY, 0),
        ],
    };

    [Fact]
    public void UniteSelectionPreservesAnExistingHoleInOneOfTheSources()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);

        // A 20x20 square with a 4x4 hole punched in its centre — one GeometrySetId, two rings.
        var donutGroupId = Guid.NewGuid();
        var donut = new SceneObject
        {
            Name = "Mezikruží",
            LocalShapes =
            [
                Rectangle(donutGroupId, layer, 0, 0, 20, 20),
                Rectangle(donutGroupId, layer, 8, 8, 12, 12),
            ],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(0, 0, 20, 20),
            Transform = ObjectTransform.Identity,
        };
        // A separate, non-overlapping square far to the right.
        var square = new SceneObject
        {
            Name = "Čtverec",
            LocalShapes = [Rectangle(Guid.NewGuid(), layer, 30, 0, 50, 20)],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(30, 0, 50, 20),
            Transform = ObjectTransform.Identity,
        };
        viewModel.Objects.Add(donut);
        viewModel.Objects.Add(square);
        viewModel.SelectedObjects.Add(donut);
        viewModel.SelectedObjects.Add(square);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal(3, result.LocalShapes.Count);
        // Summed as individual ring magnitudes (the same convention IsSafeUnionResult/
        // IsSafeBooleanResult already use elsewhere in SceneViewModel.cs — a hole ring's area adds
        // to this total rather than netting out of it): outer 400 + hole 16 + untouched square 400 =
        // 816. If the hole had silently been filled in instead of preserved, only 2 shapes would come
        // back at all (800, and Assert.Equal(3, ...) above would already have failed).
        var totalArea = result.LocalShapes.Sum(shape => Math.Abs(SignedArea(shape.Points)));
        Assert.Equal(816, totalArea, precision: 2);
        // The hole ring and its parent must wind in opposite directions — that is the only signal
        // (besides nesting) that tells a consumer which ring is a hole.
        var areas = result.LocalShapes.Select(shape => SignedArea(shape.Points)).OrderBy(area => Math.Abs(area)).ToList();
        Assert.True(Math.Sign(areas[0]) != Math.Sign(areas[1]), "The hole ring must wind opposite its parent ring.");
    }

    [Fact]
    public void SubtractSelectionCreatesAHoleWhenTheFrontShapeIsFullyInsideTheBackShape()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);

        var back = new SceneObject
        {
            Name = "Pozadí",
            LocalShapes = [Rectangle(Guid.NewGuid(), layer, 0, 0, 20, 20)],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(0, 0, 20, 20),
            Transform = ObjectTransform.Identity,
        };
        var front = new SceneObject
        {
            Name = "Popředí",
            LocalShapes = [Rectangle(Guid.NewGuid(), layer, 8, 8, 12, 12)],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(8, 8, 12, 12),
            Transform = ObjectTransform.Identity,
        };
        viewModel.Objects.Add(back);
        viewModel.Objects.Add(front);
        viewModel.SelectedObjects.Add(back);
        viewModel.SelectedObjects.Add(front);

        viewModel.SubtractSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        // The inner square never touches the outer square's boundary, so subtracting it must punch
        // a hole (two rings) rather than merely shrinking one contour (one ring) or being ignored.
        Assert.Equal(2, result.LocalShapes.Count);
        var totalArea = result.LocalShapes.Sum(shape => Math.Abs(SignedArea(shape.Points)));
        Assert.Equal(400 + 16, totalArea, precision: 2);
        var areas = result.LocalShapes.Select(shape => SignedArea(shape.Points)).OrderBy(area => Math.Abs(area)).ToList();
        Assert.True(Math.Sign(areas[0]) != Math.Sign(areas[1]), "The hole ring must wind opposite its parent ring.");
        AssertBoundsApproximately(new BoundingBox2D(0, 0, 20, 20), result.WorldBounds());
    }

    [Fact]
    public void UniteSelectionMergesTwoUnrelatedOverlappingGroupsWithinOneSourceInsteadOfCancellingViaEvenOdd()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);

        // Two 20x20 squares overlapping in a 10x10 corner, each its own GeometrySetId and neither
        // containing the other — NormalizeNestedCompoundPaths must not (and does not) link them, so
        // they reach the boolean pipeline as two independent, unrelated rings that merely happen to
        // overlap on the canvas. If they were folded together as one flat EvenOdd set instead of each
        // being resolved on its own first and then explicitly unioned, the overlap would read as
        // "covered twice" and get treated as uncovered (EvenOdd parity), silently punching a
        // fake hole where the two shapes overlap even though nothing asked for a hole there.
        var source = new SceneObject
        {
            Name = "Dva čtverce",
            LocalShapes =
            [
                Rectangle(Guid.NewGuid(), layer, 0, 0, 20, 20),
                Rectangle(Guid.NewGuid(), layer, 10, 10, 30, 30),
            ],
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(0, 0, 30, 30),
            Transform = ObjectTransform.Identity,
        };
        viewModel.Objects.Add(source);
        viewModel.SelectedObjects.Add(source);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        AssertBoundsApproximately(new BoundingBox2D(0, 0, 30, 30), result.WorldBounds());
        // Correct union: 20*20 + 20*20 - 10*10 (the shared corner counted once) = 700. The wrong,
        // flat-EvenOdd behaviour this test guards against would instead read 600 (the overlap
        // excluded from both squares rather than merged).
        var totalArea = result.LocalShapes.Sum(shape => Math.Abs(SignedArea(shape.Points)));
        Assert.Equal(700, totalArea, precision: 2);
    }

    [Fact]
    public void DrawingEllipseCreatesAClosedCurveInsideTheDraggedBounds()
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(
            DesignerTool.Ellipse,
            new Position(10, 20, 0),
            new Position(50, 40, 0));

        var ellipse = Assert.Single(viewModel.Objects);
        var shape = Assert.Single(ellipse.LocalShapes);
        Assert.Equal("Elipsa", ellipse.Name);
        Assert.True(shape.IsClosed);
        Assert.True(shape.Points.Count >= 32);
        Assert.Equal(new BoundingBox2D(10, 20, 50, 40), ellipse.WorldBounds());
    }

    [Theory]
    [InlineData(DesignerTool.Rectangle)]
    [InlineData(DesignerTool.Ellipse)]
    [InlineData(DesignerTool.Triangle)]
    [InlineData(DesignerTool.Pentagon)]
    [InlineData(DesignerTool.Hexagon)]
    [InlineData(DesignerTool.Octagon)]
    [InlineData(DesignerTool.Star)]
    [InlineData(DesignerTool.DoubleStar)]
    public void ShiftConstraintGivesShapesEqualWidthAndHeight(DesignerTool tool)
    {
        var start = new Position(20, 30, 0);
        var end = DesignerPrimitiveFactory.ConstrainEnd(tool, start, new Position(50, 40, 0));

        Assert.Equal(30, Math.Abs(end.X - start.X), precision: 6);
        Assert.Equal(30, Math.Abs(end.Y - start.Y), precision: 6);
    }

    [Fact]
    public void ShiftConstraintPreservesTheDragDirection()
    {
        var start = new Position(20, 30, 0);
        var end = DesignerPrimitiveFactory.ConstrainEnd(
            DesignerTool.Rectangle,
            start,
            new Position(5, 20, 0));

        Assert.Equal(new Position(5, 15, 0), end);
    }

    [Fact]
    public void ShiftConstraintSnapsLinesToFortyFiveDegreeAngles()
    {
        var start = new Position(10, 10, 0);
        var end = DesignerPrimitiveFactory.ConstrainEnd(
            DesignerTool.Line,
            start,
            new Position(30, 18, 0));

        Assert.Equal(start.Y, end.Y, precision: 6);
        Assert.True(end.X > start.X);
    }

    [Theory]
    [InlineData(DesignerTool.Line, "Čára", false)]
    [InlineData(DesignerTool.Triangle, "Trojúhelník", true)]
    [InlineData(DesignerTool.Pentagon, "Pětiúhelník", true)]
    [InlineData(DesignerTool.Hexagon, "Šestiúhelník", true)]
    [InlineData(DesignerTool.Octagon, "Osmiúhelník", true)]
    [InlineData(DesignerTool.Star, "Hvězda", true)]
    [InlineData(DesignerTool.DoubleStar, "Dvojitá hvězda", true)]
    public void DrawingEachPaletteVectorToolCreatesRealGeometry(DesignerTool tool, string expectedName, bool isClosed)
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(tool, new Position(5, 10, 0), new Position(35, 40, 0));

        var item = Assert.Single(viewModel.Objects);
        var shape = Assert.Single(item.LocalShapes);
        Assert.Equal(expectedName, item.Name);
        Assert.Equal(isClosed, shape.IsClosed);
        Assert.True(shape.Points.Count >= 2);
    }

    [Fact]
    public void AddingTextCreatesEditableVectorContoursOnAnEngravingLayer()
    {
        var viewModel = new SceneViewModel();

        viewModel.AddText("LASERO", new Position(10, 40, 0), 12);

        var text = Assert.Single(viewModel.Objects);
        Assert.Equal("LASERO", text.Name);
        Assert.NotEmpty(text.LocalShapes);
        Assert.All(text.LocalShapes, shape => Assert.True(shape.IsClosed));
        var layer = Assert.Single(viewModel.Layers);
        Assert.Equal(LayerMode.Fill, layer.Mode);
        Assert.Same(text, viewModel.Selected);
        var bounds = text.WorldBounds();
        Assert.Equal((bounds.MinX + bounds.MaxX) / 2, viewModel.SelectedX, precision: 6);
        Assert.Equal((bounds.MinY + bounds.MaxY) / 2, viewModel.SelectedY, precision: 6);
    }

    // The Text tool's canvas click handler (MainWindow.OnTextPlacementRequested) calls this same
    // AddText overload and deliberately never resets ActiveTool afterwards, matching the shape tools
    // staying active after a draw. AddText/AddDrawingObject not touching ActiveTool is the behavior
    // that makes that possible - this pins it so a future change to AddDrawingObject cannot silently
    // reintroduce the old "creates one object, then reverts to Select" dialog-era behavior.
    [Fact]
    public void AddingTextDoesNotChangeTheActiveTool()
    {
        var viewModel = new SceneViewModel { ActiveTool = DesignerTool.Text };

        viewModel.AddText("TEXT", new Position(0, 0, 0), 12);

        Assert.Equal(DesignerTool.Text, viewModel.ActiveTool);
        Assert.Single(viewModel.Objects);
    }

    // Creating text and committing an inline edit to its wording are two separate undo steps - one
    // Ctrl+Z each - because they are two separate user actions (place the object, then finish typing
    // into it), the same granularity every other canvas edit already commits at.
    [Fact]
    public void CommitTextEditPushesOneUndoableReplacementSeparateFromCreation()
    {
        var viewModel = new SceneViewModel();
        viewModel.AddText("TEXT", new Position(5, 5, 0), 12);
        var created = Assert.Single(viewModel.Objects);

        viewModel.CommitTextEdit(created, "Hello");

        var edited = Assert.Single(viewModel.Objects);
        Assert.Equal("Hello", edited.Text!.Text);
        Assert.Same(edited, viewModel.Selected);
        Assert.True(viewModel.CanUndo);

        viewModel.UndoCommand.Execute(null);
        var afterFirstUndo = Assert.Single(viewModel.Objects);
        Assert.Equal("TEXT", afterFirstUndo.Text!.Text);
        Assert.True(viewModel.CanUndo);

        viewModel.UndoCommand.Execute(null);
        Assert.Empty(viewModel.Objects);
    }

    // TextSource/VectorTextFactory has no such thing as a valid empty text object (Rebuild throws on
    // empty wording), so committing the inline editor empty removes the object instead - through the
    // same DeleteObjectsCommand the Delete key uses, which is why undoing it brings the object back.
    [Fact]
    public void CommitTextEditWithEmptyWordingRemovesTheObject()
    {
        var viewModel = new SceneViewModel();
        viewModel.AddText("TEXT", new Position(0, 0, 0), 12);
        var created = Assert.Single(viewModel.Objects);
        Assert.Contains(created, viewModel.SelectedObjects);

        viewModel.CommitTextEdit(created, "   ");

        Assert.Empty(viewModel.Objects);
        Assert.Empty(viewModel.SelectedObjects);

        viewModel.UndoCommand.Execute(null);
        var restored = Assert.Single(viewModel.Objects);
        Assert.Equal("TEXT", restored.Text!.Text);
    }

    // Losing focus with nothing actually typed (e.g. the caret was placed and then clicked away
    // without editing) must not push a no-op command onto the undo stack.
    [Fact]
    public void CommitTextEditWithUnchangedWordingIsANoOp()
    {
        var viewModel = new SceneViewModel();
        viewModel.AddText("TEXT", new Position(0, 0, 0), 12);
        var created = Assert.Single(viewModel.Objects);
        var undoDepthBefore = viewModel.CanUndo;

        viewModel.CommitTextEdit(created, "TEXT");

        // Same instance, not a replacement - proves no ReplaceObjectsCommand was pushed for a no-op edit.
        Assert.Same(created, Assert.Single(viewModel.Objects));
        Assert.Equal(undoDepthBefore, viewModel.CanUndo);
    }

    // Background removal swaps which file RasterFilePath points at as one ReplaceObjectsCommand step
    // -- the same "content changed" contract text and vector-path edits use -- so Ctrl+Z restores the
    // original file and Ctrl+Y reapplies the removal, matching CommitTextEditPushesOneUndoableReplacementSeparateFromCreation.
    [Fact]
    public void CommitBackgroundRemovalPushesOneUndoableReplacement()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);

        viewModel.CommitBackgroundRemoval(bitmap, "photo.nobg.png");

        var replaced = Assert.Single(viewModel.Objects);
        Assert.Equal("photo.nobg.png", replaced.RasterFilePath);
        Assert.Equal("photo.png", replaced.OriginalRasterFilePath);
        Assert.True(replaced.HasBackgroundRemoved);
        Assert.Same(replaced, viewModel.Selected);
        Assert.True(viewModel.CanUndo);

        viewModel.UndoCommand.Execute(null);
        var restored = Assert.Single(viewModel.Objects);
        Assert.Equal("photo.png", restored.RasterFilePath);
        Assert.False(restored.HasBackgroundRemoved);

        viewModel.RedoCommand.Execute(null);
        var redone = Assert.Single(viewModel.Objects);
        Assert.Equal("photo.nobg.png", redone.RasterFilePath);
        Assert.True(redone.HasBackgroundRemoved);
    }

    [Fact]
    public void CommitBackgroundRemovalIgnoresAnObjectThatAlreadyHasItRemoved()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        viewModel.Objects.Add(bitmap);
        viewModel.CommitBackgroundRemoval(bitmap, "photo.nobg.png");
        var alreadyRemoved = Assert.Single(viewModel.Objects);
        var undoDepthBefore = viewModel.CanUndo;

        viewModel.CommitBackgroundRemoval(alreadyRemoved, "photo.nobg2.png");

        Assert.Same(alreadyRemoved, Assert.Single(viewModel.Objects));
        Assert.Equal(undoDepthBefore, viewModel.CanUndo);
    }

    [Fact]
    public void RestoreSelectedBackgroundPointsRasterFilePathBackAtTheOriginal()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);
        viewModel.CommitBackgroundRemoval(bitmap, "photo.nobg.png");

        viewModel.RestoreSelectedBackground();

        var restored = Assert.Single(viewModel.Objects);
        Assert.Equal("photo.png", restored.RasterFilePath);
        Assert.False(restored.HasBackgroundRemoved);
        Assert.Same(restored, viewModel.Selected);

        viewModel.UndoCommand.Execute(null);
        var afterUndo = Assert.Single(viewModel.Objects);
        Assert.Equal("photo.nobg.png", afterUndo.RasterFilePath);
        Assert.True(afterUndo.HasBackgroundRemoved);
    }

    [Fact]
    public void RestoreSelectedBackgroundIsANoOpWhenBackgroundWasNeverRemoved()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);
        var undoDepthBefore = viewModel.CanUndo;

        viewModel.RestoreSelectedBackground();

        Assert.Same(bitmap, Assert.Single(viewModel.Objects));
        Assert.Equal(undoDepthBefore, viewModel.CanUndo);
    }

    [Fact]
    public void CanRemoveAndRestoreSelectedBackgroundReflectSelectionState()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);

        Assert.True(viewModel.IsSelectedRaster);
        Assert.True(viewModel.CanRemoveSelectedBackground);
        Assert.False(viewModel.CanRestoreSelectedBackground);

        viewModel.CommitBackgroundRemoval(bitmap, "photo.nobg.png");

        Assert.False(viewModel.CanRemoveSelectedBackground);
        Assert.True(viewModel.CanRestoreSelectedBackground);
    }

    [Fact]
    public void IsSelectedRasterIsFalseForAVectorObject()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(0, 0, 0), new Position(10, 10, 0));
        Assert.Single(viewModel.Objects);

        Assert.False(viewModel.IsSelectedRaster);
        Assert.False(viewModel.CanRemoveSelectedBackground);
    }

    [Fact]
    public void CommitTextEditIgnoresAnObjectThatIsNotText()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(0, 0, 0), new Position(10, 10, 0));
        var rectangle = Assert.Single(viewModel.Objects);
        var undoDepthBefore = viewModel.CanUndo;

        viewModel.CommitTextEdit(rectangle, "Hello");

        Assert.Equal(undoDepthBefore, viewModel.CanUndo);
        Assert.Same(rectangle, Assert.Single(viewModel.Objects));
    }

    [Fact]
    public void ChangingVectorLayerFromLineToFillChangesGeneratedToolpath()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(
            DesignerTool.Rectangle,
            new Position(0, 0, 0),
            new Position(20, 10, 0));

        var layer = Assert.Single(viewModel.Layers);
        layer.Mode = LayerMode.Cut;
        var lineToolpath = ToolpathBuilder.BuildGCode(viewModel.Scene.ToImportedDocument(), 100);

        layer.Mode = LayerMode.Fill;
        layer.FillLineIntervalMm = 2;
        var fillToolpath = ToolpathBuilder.BuildGCode(viewModel.Scene.ToImportedDocument(), 100);

        Assert.Contains(lineToolpath, line => line.Contains("(Cut)", StringComparison.Ordinal));
        Assert.Contains(fillToolpath, line => line.Contains("(Fill)", StringComparison.Ordinal));
        Assert.NotEqual(lineToolpath, fillToolpath);
        Assert.True(fillToolpath.Count > lineToolpath.Count);
    }

    [Fact]
    public void ImportingBitmapCreatesAnEditableGrayscaleEngravingLayer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-raster-layer-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(2, 1))
            {
                bitmap.SetPixel(0, 0, System.Drawing.Color.Red);
                bitmap.SetPixel(1, 0, System.Drawing.Color.Blue);
                bitmap.Save(path, ImageFormat.Png);
            }

            var viewModel = new SceneViewModel();
            var options = new RasterImportOptions
            {
                TargetWidthMm = 20,
                FeedRatePerMinute = 1234,
                MaxPower = 42,
                Passes = 2,
                Dpi = 254,
            };

            viewModel.ImportRasterFile(path, options);

            var layer = Assert.Single(viewModel.Layers);
            Assert.Equal("Bitmapa", layer.Name);
            Assert.True(layer.IsRaster);
            Assert.Equal(LayerMode.Fill, layer.Mode);
            Assert.Equal(1234, layer.Speed);
            Assert.Equal(42, layer.Power);
            Assert.Equal(2, layer.Passes);
            Assert.Equal(0.1, layer.FillLineIntervalMm, precision: 6);
            var importedObject = Assert.Single(viewModel.Objects);
            Assert.True(importedObject.IsRaster);
            Assert.All(importedObject.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeletingLastBitmapRemovesItsLayerAndUndoRestoresBoth()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        var layer = SceneObjectFactory.CreateRasterLayer(bitmap.RasterOptions!);
        bitmap.AssignToLayer(layer);
        viewModel.Layers.Add(layer);
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);

        viewModel.DeleteCommand.Execute(null);

        Assert.Empty(viewModel.Objects);
        Assert.Empty(viewModel.Layers);

        viewModel.UndoCommand.Execute(null);

        Assert.Same(bitmap, Assert.Single(viewModel.Objects));
        Assert.Same(layer, Assert.Single(viewModel.Layers));
    }

    [Fact]
    public void ReplacingBitmapWithColorTraceAssignsEditablePathsToFillLayersAndUndoRestoresBitmap()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        var bitmapLayer = SceneObjectFactory.CreateRasterLayer(bitmap.RasterOptions!);
        bitmap.AssignToLayer(bitmapLayer);
        viewModel.Layers.Add(bitmapLayer);
        viewModel.Objects.Add(bitmap);

        var redLayer = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Fill, "Červená");
        var blue = new RgbColor(0, 0, 255);
        var blueLayer = LayerSettings.CreateDefault(blue, LayerMode.Fill, "Modrá");
        static VectorPath Rectangle(double x) => new()
        {
            Subpaths = [new VectorSubpath
            {
                Nodes =
                [
                    VectorNode.CornerAt(new Position(x, 0, 0)),
                    VectorNode.CornerAt(new Position(x + 4, 0, 0)),
                    VectorNode.CornerAt(new Position(x + 4, 4, 0)),
                    VectorNode.CornerAt(new Position(x, 4, 0)),
                ],
                IsClosed = true,
            }],
        };
        var redPath = Rectangle(1);
        var bluePath = Rectangle(10);
        var result = new BitmapTraceResult
        {
            Document = new ImportedDocument
            {
                Shapes = [],
                Layers = [redLayer, blueLayer],
                BoundingBox = new BoundingBox2D(0, 0, 20, 10),
            },
            VectorPaths =
            [
                new TracedVectorObject(redPath, RgbColor.Red),
                new TracedVectorObject(bluePath, blue),
            ],
            PixelWidth = 20,
            PixelHeight = 10,
            ContourCount = 2,
            PointCount = 8,
            NodeCount = 8,
        };

        viewModel.ReplaceRasterWithTrace(bitmap, result);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.All(viewModel.Objects, item => Assert.NotNull(item.VectorPath));
        Assert.Contains(viewModel.Objects, item => item.LocalShapes.All(shape => shape.LayerId == redLayer.Id));
        Assert.Contains(viewModel.Objects, item => item.LocalShapes.All(shape => shape.LayerId == blueLayer.Id));
        Assert.Equal(LayerMode.Fill, Assert.Single(viewModel.Layers, layer => layer.Id == redLayer.Id).Mode);
        Assert.Equal(LayerMode.Fill, Assert.Single(viewModel.Layers, layer => layer.Id == blueLayer.Id).Mode);

        viewModel.UndoCommand.Execute(null);
        Assert.Same(bitmap, Assert.Single(viewModel.Objects));
        Assert.DoesNotContain(viewModel.Layers, layer => layer.Id == redLayer.Id || layer.Id == blueLayer.Id);

        viewModel.RedoCommand.Execute(null);
        Assert.Equal(2, viewModel.Objects.Count);
        Assert.All(viewModel.Objects, item => Assert.NotNull(item.VectorPath));

        var saved = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(viewModel.CreateProject()));
        var reloaded = new SceneViewModel();
        reloaded.LoadProject(saved);
        Assert.Equal(2, reloaded.Objects.Count);
        Assert.All(reloaded.Objects, item =>
        {
            Assert.NotNull(item.VectorPath);
            Assert.All(item.LocalShapes, shape =>
                Assert.Contains(reloaded.Layers, layer => layer.Id == shape.LayerId && layer.Mode == LayerMode.Fill));
        });
    }

    [Fact]
    public void LayerVisibilityControlsCanvasWithoutDisablingMachineOutput()
    {
        var viewModel = new SceneViewModel();
        var layer = new LayerSettings
        {
            Color = RgbColor.Red,
            Name = "Řez",
            Mode = LayerMode.Cut,
            IsEnabled = true,
            IsVisible = false,
        };
        viewModel.Layers.Add(layer);

        Assert.False(viewModel.IsLayerVisible(RgbColor.Red));
        Assert.True(layer.IsEnabled);
    }

    [Fact]
    public void LayerVisibilityIsPreservedInProjectRoundTrip()
    {
        var source = new SceneViewModel();
        source.Layers.Add(new LayerSettings
        {
            Color = RgbColor.Red,
            Name = "Řez",
            Mode = LayerMode.Cut,
            IsEnabled = true,
            IsVisible = false,
        });

        var restored = new SceneViewModel();
        restored.LoadProject(source.CreateProject());

        var layer = Assert.Single(restored.Layers);
        Assert.False(layer.IsVisible);
        Assert.True(layer.IsEnabled);
    }

    [Fact]
    public void FirstLayerBecomesSelectedForCompactLayerInspector()
    {
        var viewModel = new SceneViewModel();
        var layer = new LayerSettings { Color = RgbColor.Red, Name = "Řez" };

        viewModel.Layers.Add(layer);

        Assert.Same(layer, viewModel.SelectedLayer);
    }

    [Fact]
    public void PasteAfterSelectionChangesStillUsesTheCopiedObjects()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.CopyCommand.Execute(null);
        viewModel.SelectedObjects.Clear(); // selection changes before paste happens

        viewModel.PasteCommand.Execute(null);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.Single(viewModel.SelectedObjects);
        Assert.NotSame(obj, viewModel.SelectedObjects[0]);
    }

    [Fact]
    public void PasteCanRunMultipleTimesFromTheSameCopy()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.CopyCommand.Execute(null);
        viewModel.PasteCommand.Execute(null);
        viewModel.PasteCommand.Execute(null);

        Assert.Equal(3, viewModel.Objects.Count);
    }

    [Fact]
    public void AlignLeftMovesAllSelectedObjectsToTheLeftmostEdge()
    {
        var viewModel = new SceneViewModel();
        var left = MakeSquareObject(x: 0);
        var right = MakeSquareObject(x: 50);
        viewModel.Objects.Add(left);
        viewModel.Objects.Add(right);
        viewModel.SelectedObjects.Add(left);
        viewModel.SelectedObjects.Add(right);

        var expectedMinX = left.WorldBounds().MinX;

        viewModel.AlignLeftCommand.Execute(null);

        Assert.Equal(expectedMinX, left.WorldBounds().MinX, precision: 6);
        Assert.Equal(expectedMinX, right.WorldBounds().MinX, precision: 6);
    }

    [Fact]
    public void AlignCommandsAreDisabledWithFewerThanTwoSelectedObjects()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.False(viewModel.AlignLeftCommand.CanExecute(null));
    }

    [Fact]
    public void ChangingWidthWithAspectLockScalesBothDimensionsAndCanBeUndone()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.SelectedWidth = 40;

        Assert.Equal(40, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(20, viewModel.SelectedHeight, precision: 6);

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(10, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void ChangingHeightWithoutAspectLockPreservesWidth()
    {
        var viewModel = new SceneViewModel { LockAspectRatio = false };
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.SelectedHeight = 30;

        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(30, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void RasterCanBeResizedButNotRotatedAndResizeIsUndoable()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeRasterObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.True(viewModel.CanTransformSelectedObject);
        Assert.False(viewModel.CanRotateSelectedObject);

        viewModel.SelectedWidth = 40;
        Assert.Equal(40, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(20, viewModel.SelectedHeight, precision: 6);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(10, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void FlipHorizontalIsUndoable()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.FlipHorizontalCommand.Execute(null);
        Assert.Equal(-1, obj.Transform.ScaleX);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(1, obj.Transform.ScaleX);
    }

    [Fact]
    public void LockedObjectCanBeUnlockedBySelectionCommand()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        obj.IsLocked = true;
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.False(viewModel.RotateRightCommand.CanExecute(null));
        viewModel.ToggleLockCommand.Execute(null);

        Assert.False(obj.IsLocked);
        Assert.True(viewModel.RotateRightCommand.CanExecute(null));
    }

    // -------------------------------------------------------------------------------------------
    // Offset Path (SceneViewModel.ApplyOffset) — the dialog itself (OffsetPathViewModel) computes
    // its own result via VectorOffsetPlanner and is exercised separately; these tests call
    // ApplyOffset directly with a planner-computed result, matching what MainWindow's
    // OnOffsetRequested handler does after the dialog closes with OK.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ApplyOffsetOnASingleRectangleProducesAVectorPathResultAtTheExpectedSizeAndPlacement()
    {
        var viewModel = new SceneViewModel();
        var rectangle = MakeSquareObject();
        viewModel.Objects.Add(rectangle);
        viewModel.SelectedObjects.Add(rectangle);

        var path = VectorOffsetPlanner.ComputeOffset(
            rectangle, Clipper2VectorOffsetService.Default, 2, VectorJoinType.Miter, 4);
        Assert.NotNull(path);

        viewModel.ApplyOffset([rectangle], new Dictionary<SceneObject, VectorPath> { [rectangle] = path! });

        var result = Assert.Single(viewModel.Objects);
        Assert.True(result.IsVectorPath);
        var bounds = result.WorldBounds();
        Assert.Equal(-2, bounds.MinX, precision: 3);
        Assert.Equal(-2, bounds.MinY, precision: 3);
        Assert.Equal(22, bounds.MaxX, precision: 3);
        Assert.Equal(12, bounds.MaxY, precision: 3);
        Assert.Same(result, Assert.Single(viewModel.SelectedObjects));
    }

    [Fact]
    public void ApplyOffsetOnTwoSelectedObjectsReplacesBothInOneUndoStep()
    {
        var viewModel = new SceneViewModel();
        var first = MakeSquareObject();
        var second = MakeSquareObject(x: 40);
        viewModel.Objects.Add(first);
        viewModel.Objects.Add(second);
        viewModel.SelectedObjects.Add(first);
        viewModel.SelectedObjects.Add(second);
        Assert.False(viewModel.CanUndo);

        var service = Clipper2VectorOffsetService.Default;
        var results = new Dictionary<SceneObject, VectorPath>
        {
            [first] = VectorOffsetPlanner.ComputeOffset(first, service, 2, VectorJoinType.Round, 2)!,
            [second] = VectorOffsetPlanner.ComputeOffset(second, service, 2, VectorJoinType.Round, 2)!,
        };

        viewModel.ApplyOffset([first, second], results);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.DoesNotContain(first, viewModel.Objects);
        Assert.DoesNotContain(second, viewModel.Objects);
        Assert.True(viewModel.CanUndo);

        // One ReplaceObjectsCommand covering both sources means exactly one Undo restores both.
        viewModel.UndoCommand.Execute(null);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.Contains(first, viewModel.Objects);
        Assert.Contains(second, viewModel.Objects);
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void UndoAfterOffsetRestoresTheOriginalObjectExactly()
    {
        var viewModel = new SceneViewModel();
        var rectangle = MakeSquareObject();
        viewModel.Objects.Add(rectangle);
        viewModel.SelectedObjects.Add(rectangle);
        var originalBounds = rectangle.WorldBounds();

        var path = VectorOffsetPlanner.ComputeOffset(
            rectangle, Clipper2VectorOffsetService.Default, -2, VectorJoinType.Round, 2);
        Assert.NotNull(path);
        viewModel.ApplyOffset([rectangle], new Dictionary<SceneObject, VectorPath> { [rectangle] = path! });
        Assert.NotSame(rectangle, Assert.Single(viewModel.Objects));

        viewModel.UndoCommand.Execute(null);

        var restored = Assert.Single(viewModel.Objects);
        Assert.Same(rectangle, restored);
        Assert.Equal(originalBounds, restored.WorldBounds());
        Assert.False(restored.IsVectorPath);
    }
}
