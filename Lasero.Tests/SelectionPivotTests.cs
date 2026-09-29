using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Regression for "why are these points outside the shape, this is random": a pen-tool path was
/// selected and its dashed box was right, but the edge-midpoint grips, the pivot marker and the
/// rotate grip were drawn on the document's X=0 / Y=0 axes, far from the shape.
///
/// Cause: SceneObject documents its pivot as the geometry's bounding-box centre, and everything that
/// draws or drags a selection relies on that (ObjectTransform.HandleLocalPoint takes the pivot's
/// coordinate for the axis an edge handle does not move; ComputeResize mirrors the anchor through
/// the pivot; rotate, flip and the inspector size all scale/rotate about it). VectorPathSceneFactory
/// built objects with LocalPivot = (0,0) while LocalBounds sat wherever the operator clicked.
/// </summary>
public class SelectionPivotTests
{
    private static readonly RgbColor Color = RgbColor.Black;

    /// <summary>A closed, curved teardrop nowhere near the document origin, like the one in the report.</summary>
    private static VectorPath Teardrop() => new()
    {
        Subpaths =
        [
            new VectorSubpath
            {
                IsClosed = true,
                Nodes =
                [
                    VectorNode.CornerAt(new Position(120, 150, 0)),
                    new VectorNode(new Position(190, 190, 0), new Position(150, 175, 0), new Position(230, 205, 0), VectorNodeType.Smooth),
                    new VectorNode(new Position(240, 140, 0), new Position(240, 180, 0), new Position(240, 100, 0), VectorNodeType.Smooth),
                    new VectorNode(new Position(180, 100, 0), new Position(220, 95, 0), new Position(150, 105, 0), VectorNodeType.Smooth),
                ],
            },
        ],
    };

    /// <summary>Where SceneCanvas.DrawSingleObjectHandles puts a grip: local handle point through the transform.</summary>
    private static Position HandleWorld(SceneObject obj, ResizeHandle handle) =>
        obj.Transform.Apply(ObjectTransform.HandleLocalPoint(obj.LocalBounds, obj.LocalPivot, handle), obj.LocalPivot);

    /// <summary>Where SceneCanvas draws the pivot marker (and measures a rotate drag from).</summary>
    private static Position PivotWorld(SceneObject obj) => obj.Transform.Apply(obj.LocalPivot, obj.LocalPivot);

    private static Position Midpoint(Position a, Position b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0);

    private static void AssertNear(Position expected, Position actual, string because)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) < 1e-6 && Math.Abs(expected.Y - actual.Y) < 1e-6,
            $"{because}: expected ({expected.X:0.###}, {expected.Y:0.###}) but was ({actual.X:0.###}, {actual.Y:0.###})");
    }

    [Fact]
    public void EveryResizeGripOfADrawnPathSitsOnItsOwnBoundingBox()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var box = obj.WorldBounds();
        var midX = (box.MinX + box.MaxX) / 2;
        var midY = (box.MinY + box.MaxY) / 2;

        // ResizeHandle names the minimum-Y edge "Top" (document Y grows up), so Top is MinY.
        AssertNear(new Position(box.MinX, box.MinY, 0), HandleWorld(obj, ResizeHandle.TopLeft), "TopLeft");
        AssertNear(new Position(midX, box.MinY, 0), HandleWorld(obj, ResizeHandle.Top), "Top");
        AssertNear(new Position(box.MaxX, box.MinY, 0), HandleWorld(obj, ResizeHandle.TopRight), "TopRight");
        AssertNear(new Position(box.MinX, midY, 0), HandleWorld(obj, ResizeHandle.Left), "Left");
        AssertNear(new Position(box.MaxX, midY, 0), HandleWorld(obj, ResizeHandle.Right), "Right");
        AssertNear(new Position(box.MinX, box.MaxY, 0), HandleWorld(obj, ResizeHandle.BottomLeft), "BottomLeft");
        AssertNear(new Position(midX, box.MaxY, 0), HandleWorld(obj, ResizeHandle.Bottom), "Bottom");
        AssertNear(new Position(box.MaxX, box.MaxY, 0), HandleWorld(obj, ResizeHandle.BottomRight), "BottomRight");
    }

    [Fact]
    public void PivotMarkerAndRotationCentreOfADrawnPathIsTheCentreOfItsBounds()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var box = obj.WorldBounds();

        AssertNear(Midpoint(new Position(box.MinX, box.MinY, 0), new Position(box.MaxX, box.MaxY, 0)), PivotWorld(obj), "pivot marker");
    }

    [Fact]
    public void GripsFollowARotatedAndScaledDrawnPathInsteadOfStayingOnTheAxes()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        obj.Transform = new ObjectTransform(15, -8, 30, 2, 0.5);

        var topLeft = HandleWorld(obj, ResizeHandle.TopLeft);
        var topRight = HandleWorld(obj, ResizeHandle.TopRight);
        var bottomLeft = HandleWorld(obj, ResizeHandle.BottomLeft);
        var bottomRight = HandleWorld(obj, ResizeHandle.BottomRight);

        AssertNear(Midpoint(topLeft, topRight), HandleWorld(obj, ResizeHandle.Top), "Top is the middle of the top edge");
        AssertNear(Midpoint(bottomLeft, bottomRight), HandleWorld(obj, ResizeHandle.Bottom), "Bottom is the middle of the bottom edge");
        AssertNear(Midpoint(topLeft, bottomLeft), HandleWorld(obj, ResizeHandle.Left), "Left is the middle of the left edge");
        AssertNear(Midpoint(topRight, bottomRight), HandleWorld(obj, ResizeHandle.Right), "Right is the middle of the right edge");
        AssertNear(Midpoint(topLeft, bottomRight), PivotWorld(obj), "the pivot marker is the middle of the box");
    }

    [Fact]
    public void ResizingADrawnPathByAnEdgeGripKeepsTheOppositeEdgeFixed()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var box = obj.WorldBounds();
        var leftEdgeBefore = obj.Transform.Apply(new Position(box.MinX, (box.MinY + box.MaxY) / 2, 0), obj.LocalPivot);

        // Pull the right-edge grip 40 mm further right.
        var handle = HandleWorld(obj, ResizeHandle.Right);
        var resized = obj.Transform.ComputeResize(
            obj.LocalPivot, obj.LocalBounds, ResizeHandle.Right, new Position(handle.X + 40, handle.Y, 0));

        var leftEdgeAfter = resized.Apply(new Position(box.MinX, (box.MinY + box.MaxY) / 2, 0), obj.LocalPivot);
        var rightEdgeAfter = resized.Apply(new Position(box.MaxX, (box.MinY + box.MaxY) / 2, 0), obj.LocalPivot);

        AssertNear(leftEdgeBefore, leftEdgeAfter, "the anchor edge did not move");
        Assert.Equal(box.MaxX + 40, rightEdgeAfter.X, precision: 6);
    }

    [Fact]
    public void FlippingADrawnPathMirrorsItAboutItsOwnCentreNotAboutTheDocumentOrigin()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var before = obj.WorldBounds();

        obj.Transform = obj.Transform with { ScaleX = -1 };
        var after = obj.WorldBounds();

        Assert.Equal(before.MinX, after.MinX, precision: 6);
        Assert.Equal(before.MaxX, after.MaxX, precision: 6);
    }

    [Fact]
    public void RotatingADrawnPathHalfATurnLeavesItOnTheSameSpot()
    {
        var obj = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var before = obj.WorldBounds();

        obj.Transform = obj.Transform with { RotationDeg = 180 };
        var after = obj.WorldBounds();

        Assert.Equal(before.MinX, after.MinX, precision: 6);
        Assert.Equal(before.MaxX, after.MaxX, precision: 6);
        Assert.Equal(before.MinY, after.MinY, precision: 6);
        Assert.Equal(before.MaxY, after.MaxY, precision: 6);
    }

    [Fact]
    public void ANodeEditDoesNotMoveARotatedAndScaledDrawnPath()
    {
        var path = Teardrop();
        var source = VectorPathSceneFactory.Create(path, Color, "Křivka");
        source.Transform = new ObjectTransform(15, -8, 30, 2, 0.5);

        AssertNodeEditLeavesUntouchedNodesInPlace(source, path);
    }

    [Fact]
    public void ANodeEditDoesNotMoveARotatedAndScaledImportedShape()
    {
        // Rebuild replaces the object after every node edit. It used to reset the pivot to (0,0)
        // while carrying the old Transform over, which moves any rotated or scaled object whose pivot
        // was not already there: imported, traced and grouped shapes all have their own pivot.
        var path = Teardrop();
        var drawn = VectorPathSceneFactory.Create(path, Color, "Import");
        var source = new SceneObject
        {
            LocalShapes = drawn.LocalShapes,
            LocalBounds = drawn.LocalBounds,
            LocalPivot = new Position(37, -11, 0), // wherever the source file put it
            VectorPath = path,
            Transform = new ObjectTransform(15, -8, 30, 2, 0.5),
        };

        AssertNodeEditLeavesUntouchedNodesInPlace(source, path);
    }

    /// <summary>A node edit that also moves the bounds (and so their centre): the first node is dragged far away.</summary>
    private static VectorPath PullFirstNodeAway(VectorPath path) => path.ReplaceSubpath(0, path.Subpaths[0] with
    {
        Nodes = path.Subpaths[0].Nodes.Select((node, index) => index == 0 ? node.Translated(-60, 45) : node).ToList(),
    });

    [Fact]
    public void UndoAndRedoOfANodeEditRestoreTheExactPlacementOfARotatedPath()
    {
        var viewModel = new SceneViewModel();
        var path = Teardrop();
        viewModel.AddVectorPath(path);
        var original = Assert.Single(viewModel.Objects);
        original.Transform = new ObjectTransform(15, -8, 30, 2, 0.5);
        var pivotBefore = original.LocalPivot;
        var transformBefore = original.Transform;
        var worldBefore = original.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();

        viewModel.CommitVectorPathEdit(original, PullFirstNodeAway(path));
        var edited = Assert.Single(viewModel.Objects);
        Assert.NotSame(original, edited);
        var worldEdited = edited.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();

        viewModel.UndoCommand.Execute(null);

        // Undo hands back the very object that was replaced, untouched: same pivot, same transform.
        Assert.Same(original, Assert.Single(viewModel.Objects));
        Assert.Equal(pivotBefore, original.LocalPivot);
        Assert.Equal(transformBefore, original.Transform);
        var worldUndone = original.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();
        for (var index = 0; index < worldBefore.Count; index++)
            AssertNear(worldBefore[index], worldUndone[index], $"undo: node {index}");

        viewModel.RedoCommand.Execute(null);

        var redone = Assert.Single(viewModel.Objects);
        var worldRedone = redone.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();
        for (var index = 0; index < worldEdited.Count; index++)
            AssertNear(worldEdited[index], worldRedone[index], $"redo: node {index}");
        AssertPivotIsBoundsCentre(redone);
    }

    private static void AssertNodeEditLeavesUntouchedNodesInPlace(SceneObject source, VectorPath path)
    {
        var anchorsBefore = source.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();

        var rebuilt = VectorPathSceneFactory.Rebuild(source, PullFirstNodeAway(path));
        var anchorsAfter = rebuilt.GetWorldVectorPath()!.Subpaths[0].Nodes.Select(node => node.Anchor).ToList();

        // Every node except the one the user dragged stays exactly where it was on the canvas.
        for (var index = 1; index < anchorsBefore.Count; index++)
            AssertNear(anchorsBefore[index], anchorsAfter[index], $"node {index} did not move");
        AssertPivotIsBoundsCentre(rebuilt);
    }

    [Fact]
    public void ANodeEditKeepsThePivotAtTheCentreOfTheNewBounds()
    {
        var path = Teardrop();
        var source = VectorPathSceneFactory.Create(path, Color, "Křivka");
        var edited = path.ReplaceSubpath(0, VectorPathEditor.AppendNode(
            path.Subpaths[0] with { IsClosed = false }, new Position(400, 20, 0), null));

        var rebuilt = VectorPathSceneFactory.Rebuild(source, edited);

        AssertPivotIsBoundsCentre(rebuilt);
    }

    [Fact]
    public void EveryObjectFactoryKeepsThePivotAtTheCentreOfTheBounds()
    {
        var start = new Position(130, 60, 0);
        var end = new Position(210, 130, 0);

        AssertPivotIsBoundsCentre(ScenePrimitiveFactory.CreateRectangle(start, end, Color, "Obdélník"));
        AssertPivotIsBoundsCentre(ScenePrimitiveFactory.CreateEllipse(start, end, Color, "Elipsa"));
        AssertPivotIsBoundsCentre(ScenePrimitiveFactory.CreateLine(start, end, Color, "Čára"));
        AssertPivotIsBoundsCentre(ScenePrimitiveFactory.CreatePolygon(start, end, 6, Color, "Šestiúhelník"));
        AssertPivotIsBoundsCentre(ScenePrimitiveFactory.CreateStar(start, end, 5, 0.5, Color, "Hvězda"));
        AssertPivotIsBoundsCentre(VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka"));
    }

    [Fact]
    public void APenPathAddedThroughTheViewModelHasWorkingGrips()
    {
        var viewModel = new SceneViewModel();

        viewModel.AddVectorPath(Teardrop());

        var obj = Assert.Single(viewModel.Objects);
        var box = obj.WorldBounds();
        AssertPivotIsBoundsCentre(obj);
        AssertNear(
            new Position((box.MinX + box.MaxX) / 2, box.MinY, 0),
            HandleWorld(obj, ResizeHandle.Top),
            "Top grip is on the top edge of the drawn path");
    }

    [Fact]
    public void GroupedShapesKeepThePivotAtTheCentreOfTheBounds()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(130, 60, 0), new Position(210, 130, 0));
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(190, 100, 0), new Position(300, 170, 0));
        viewModel.SelectedObjects.Clear();
        foreach (var obj in viewModel.Objects) viewModel.SelectedObjects.Add(obj);

        viewModel.GroupSelectionCommand.Execute(null);

        AssertPivotIsBoundsCentre(Assert.Single(viewModel.Objects));
    }

    [Fact]
    public void TextKeepsThePivotAtTheCentreOfTheBounds()
    {
        var text = VectorTextFactory.Create(
            new TextSource { Text = "Lasero", HeightMm = 24 }, new Position(130, 60, 0), Color);

        AssertPivotIsBoundsCentre(text);
        AssertPivotIsBoundsCentre(VectorTextFactory.Rebuild(text, new TextSource { Text = "Laser", HeightMm = 30 }));
    }

    [Fact]
    public void ProjectsSavedWithAnOriginPivotLoadWithWorkingGripsAndTheSameGeometry()
    {
        // Exactly what the pen tool used to save: pivot at the document origin, bounds where the
        // path was drawn, plus a rotate/scale the operator applied afterwards.
        var drawn = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var transform = new ObjectTransform(15, -8, 30, 2, 0.5);
        var project = new LaseroProjectFile
        {
            Layers = [],
            Objects =
            [
                new ProjectObject
                {
                    Name = "Křivka",
                    Shapes = drawn.LocalShapes.Select(shape => new ProjectShape
                    {
                        Points = shape.Points.ToList(),
                        IsClosed = shape.IsClosed,
                        LayerColor = shape.LayerColor,
                        PreferredMode = shape.PreferredMode,
                    }).ToList(),
                    LocalPivot = Position.Zero,
                    LocalBounds = drawn.LocalBounds,
                    Transform = transform,
                    VectorPath = Teardrop(),
                },
            ],
        };
        var expectedWorld = drawn.LocalShapes[0].Points.Select(point => transform.Apply(point, Position.Zero)).ToList();

        var viewModel = new SceneViewModel();
        viewModel.LoadProject(project);

        var loaded = Assert.Single(viewModel.Objects);
        AssertPivotIsBoundsCentre(loaded);
        var actualWorld = loaded.GetWorldShapes()[0].Points;
        Assert.Equal(expectedWorld.Count, actualWorld.Count);
        for (var index = 0; index < expectedWorld.Count; index++)
            AssertNear(expectedWorld[index], actualWorld[index], $"point {index} did not move on load");
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(30, 2, 0.5)]
    [InlineData(-110, -1.5, 0.75)]
    [InlineData(90, 1, -1)]
    public void MovingThePivotKeepsEveryLocalPointWhereItWas(double rotationDeg, double scaleX, double scaleY)
    {
        var transform = new ObjectTransform(15, -8, rotationDeg, scaleX, scaleY);
        var from = new Position(0, 0, 0);
        var to = new Position(180, 146.5, 0);

        var moved = transform.WithPivotMoved(from, to);

        foreach (var local in new[] { new Position(120, 150, 0), new Position(240, 100, 0), new Position(-5, 12, 0) })
            AssertNear(transform.Apply(local, from), moved.Apply(local, to), $"local ({local.X}, {local.Y})");
    }

    [Fact]
    public void MovingThePivotOfAnUnrotatedUnscaledObjectLeavesTranslationUntouched()
    {
        var transform = new ObjectTransform(5.3, 7.1, 0, 1, 1);

        Assert.Equal(transform, transform.WithPivotMoved(new Position(1.1, 2.2, 0), new Position(180.7, 146.9, 0)));
    }

    [Fact]
    public void CentringThePivotKeepsIdentityDataAndWorldGeometry()
    {
        var drawn = VectorPathSceneFactory.Create(Teardrop(), Color, "Křivka");
        var offCentre = new SceneObject
        {
            Id = Guid.NewGuid(),
            Name = "Křivka",
            LocalShapes = drawn.LocalShapes,
            LocalBounds = drawn.LocalBounds,
            LocalPivot = Position.Zero,
            VectorPath = drawn.VectorPath,
            Transform = new ObjectTransform(15, -8, 30, 2, 0.5),
            IsLocked = true,
            IncludeInOutput = false,
        };

        var centred = offCentre.WithPivotAtBoundsCenter();

        Assert.NotSame(offCentre, centred);
        Assert.Equal(offCentre.Id, centred.Id);
        Assert.Equal(offCentre.Name, centred.Name);
        Assert.True(centred.IsLocked);
        Assert.False(centred.IncludeInOutput);
        Assert.Same(offCentre.VectorPath, centred.VectorPath);
        AssertPivotIsBoundsCentre(centred);
        var before = offCentre.WorldBounds();
        var after = centred.WorldBounds();
        Assert.Equal(before.MinX, after.MinX, precision: 6);
        Assert.Equal(before.MaxX, after.MaxX, precision: 6);
        Assert.Equal(before.MinY, after.MinY, precision: 6);
        Assert.Equal(before.MaxY, after.MaxY, precision: 6);
        Assert.Same(centred, centred.WithPivotAtBoundsCenter());
    }

    private static void AssertPivotIsBoundsCentre(SceneObject obj)
    {
        var centre = new Position(
            (obj.LocalBounds.MinX + obj.LocalBounds.MaxX) / 2,
            (obj.LocalBounds.MinY + obj.LocalBounds.MaxY) / 2,
            0);
        AssertNear(centre, obj.LocalPivot, $"{obj.Name}: LocalPivot is the centre of LocalBounds");
    }
}
