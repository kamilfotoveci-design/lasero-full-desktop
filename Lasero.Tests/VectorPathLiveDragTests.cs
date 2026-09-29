using System.IO;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Regression for "it must move in real time": while a node, handle or segment was dragged in Node
/// Edit mode, the node and handle dots followed the pointer but the path outline stayed where the
/// drag began, and only jumped to the new shape on mouse-up.
///
/// Cause: the drag does not replace the object until release (one drag is one undo step), so
/// SceneObject.VectorPath is still the pre-drag curve during it. SceneCanvas.UpdateObjectGeometry
/// began drawing obj.VectorPath (so curves stay smooth at any zoom) and ignored the live frame that
/// RenderVectorPathLive had put into LocalShapes. The outline must be drawn from the canvas's
/// working path while a drag is in flight; VectorPathRenderSource makes that choice.
/// </summary>
public class VectorPathLiveDragTests
{
    private static readonly RgbColor Color = RgbColor.Black;

    private static VectorPath OpenPolyline() => VectorPath.SingleOpen(
    [
        VectorNode.CornerAt(new Position(60, 120, 0)),
        VectorNode.CornerAt(new Position(110, 80, 0)),
        VectorNode.CornerAt(new Position(60, 40, 0)),
    ]);

    private static VectorPath ClosedCurve() => new()
    {
        Subpaths =
        [
            new VectorSubpath
            {
                IsClosed = true,
                Nodes =
                [
                    VectorNode.CornerAt(new Position(60, 60, 0)),
                    new VectorNode(new Position(100, 110, 0), new Position(75, 95, 0), new Position(125, 125, 0), VectorNodeType.Smooth),
                    new VectorNode(new Position(150, 70, 0), new Position(150, 100, 0), new Position(150, 40, 0), VectorNodeType.Smooth),
                    new VectorNode(new Position(100, 30, 0), new Position(125, 30, 0), new Position(80, 32, 0), VectorNodeType.Smooth),
                ],
            },
        ],
    };

    /// <summary>One drag frame, built the way SceneCanvas.UpdateNodeEditDrag does: the pre-drag path with
    /// the selected nodes translated by the pointer delta.</summary>
    private static VectorPath Frame(VectorPath original, IReadOnlyCollection<(int Subpath, int Node)> selected, double dx, double dy)
    {
        var subpaths = original.Subpaths.ToList();
        for (var s = 0; s < subpaths.Count; s++)
        {
            var nodes = subpaths[s].Nodes.ToList();
            for (var n = 0; n < nodes.Count; n++)
                if (selected.Contains((s, n))) nodes[n] = nodes[n].Translated(dx, dy);
            subpaths[s] = subpaths[s] with { Nodes = nodes };
        }
        return original with { Subpaths = subpaths };
    }

    private static readonly (double Dx, double Dy)[] Moves = [(4, -2), (15, -10), (40, -35), (-25, 30), (0, 0)];

    // ---------------------------------------------------------------------------------------
    // Which curve is drawn
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WhileADragIsInFlightTheOutlineIsDrawnFromTheWorkingPath()
    {
        var obj = VectorPathSceneFactory.Create(OpenPolyline(), Color, "Křivka");
        var working = Frame(obj.VectorPath!, [(0, 1)], 30, 20);

        var drawn = VectorPathRenderSource.For(obj, nodeEditObject: obj, working, dragInFlight: true);

        Assert.Same(working, drawn);
    }

    [Fact]
    public void WithoutADragTheOutlineIsTheObjectsOwnPath()
    {
        var obj = VectorPathSceneFactory.Create(OpenPolyline(), Color, "Křivka");
        var working = Frame(obj.VectorPath!, [(0, 1)], 30, 20);

        Assert.Same(obj.VectorPath, VectorPathRenderSource.For(obj, obj, working, dragInFlight: false));
    }

    [Fact]
    public void ADragOnOneObjectDoesNotRedrawAnotherObject()
    {
        var edited = VectorPathSceneFactory.Create(OpenPolyline(), Color, "Upravovaná");
        var other = VectorPathSceneFactory.Create(ClosedCurve(), Color, "Jiná");
        var working = Frame(edited.VectorPath!, [(0, 1)], 30, 20);

        Assert.Same(other.VectorPath, VectorPathRenderSource.For(other, edited, working, dragInFlight: true));
    }

    [Fact]
    public void AnObjectWithoutAVectorModelHasNoCurveToDraw()
    {
        var rectangle = ScenePrimitiveFactory.CreateRectangle(
            new Position(0, 0, 0), new Position(20, 10, 0), Color, "Obdélník");

        Assert.Null(VectorPathRenderSource.For(rectangle, rectangle, ClosedCurve(), dragInFlight: true));
    }

    [Fact]
    public void ADragFrameWithoutAWorkingPathFallsBackToTheObjectsOwnPath()
    {
        var obj = VectorPathSceneFactory.Create(OpenPolyline(), Color, "Křivka");

        Assert.Same(obj.VectorPath, VectorPathRenderSource.For(obj, obj, workingPath: null, dragInFlight: true));
    }

    [Fact]
    public void TheCanvasDrawsItsOutlineThroughTheRenderSource()
    {
        // SceneCanvas is a WPF control with no headless harness in this suite, so guard the wiring
        // itself: UpdateObjectGeometry must not go back to reading obj.VectorPath directly.
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));
        var update = source[
            source.IndexOf("private void UpdateObjectGeometry", StringComparison.Ordinal)..
            source.IndexOf("private bool IsObjectVisibleOnCanvas", StringComparison.Ordinal)];

        Assert.Contains("VectorPathRenderSource.For(obj, _nodeEditObject, _nodeEditWorkingPath, _nodeDragSession is not null)", update, StringComparison.Ordinal);
        Assert.DoesNotContain("obj.VectorPath is { } vectorPath", update, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Every live frame is the working path (the model half of the preview)
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EveryPreviewFrameIsTheWorkingPathThroughTheObjectsTransform(bool closed, bool transformed)
    {
        var path = closed ? ClosedCurve() : OpenPolyline();
        var obj = VectorPathSceneFactory.Create(path, Color, "Křivka");
        if (transformed) obj.Transform = new ObjectTransform(10, -5, 25, 1.3, 0.8);
        var session = new VectorPathDragSession(obj.LocalShapes);
        var selected = new[] { (0, 1), (0, 2) }; // several nodes moved together

        foreach (var (dx, dy) in Moves)
        {
            var frame = Frame(path, selected, dx, dy);
            obj.LocalShapes = session.BuildPreviewShapes(frame, Color); // what RenderVectorPathLive does

            var world = obj.GetWorldShapes();
            Assert.Equal(frame.Subpaths.Count, world.Count);
            for (var s = 0; s < frame.Subpaths.Count; s++)
            {
                var expected = frame.Subpaths[s].Flatten();
                Assert.Equal(expected.Count, world[s].Points.Count);
                for (var p = 0; p < expected.Count; p++)
                {
                    var want = obj.Transform.Apply(expected[p], obj.LocalPivot);
                    Assert.True(
                        Math.Abs(want.X - world[s].Points[p].X) < 1e-9 && Math.Abs(want.Y - world[s].Points[p].Y) < 1e-9,
                        $"frame ({dx}, {dy}), shape {s}, point {p} does not follow the working path");
                }
            }
        }
    }

    [Fact]
    public void ADragOfManyFramesIsOneUndoStep()
    {
        var viewModel = new SceneViewModel();
        var path = ClosedCurve();
        viewModel.AddVectorPath(path);
        var original = Assert.Single(viewModel.Objects);
        var originalShapes = original.LocalShapes;
        var session = new VectorPathDragSession(original.LocalShapes);

        // Mouse-move frames mutate the live object only...
        VectorPath last = path;
        foreach (var (dx, dy) in Moves)
        {
            last = Frame(path, [(0, 2)], dx + 1, dy + 1);
            original.LocalShapes = session.BuildPreviewShapes(last, Color);
        }
        // ...and mouse-up restores the pre-drag shapes, then commits exactly once.
        original.LocalShapes = session.OriginalShapes;
        viewModel.CommitVectorPathEdit(original, last);

        var committed = Assert.Single(viewModel.Objects);
        Assert.NotSame(original, committed);

        viewModel.UndoCommand.Execute(null);

        Assert.Same(original, Assert.Single(viewModel.Objects));
        Assert.Same(originalShapes, original.LocalShapes);
        Assert.Same(path, original.VectorPath);
        Assert.True(viewModel.CanUndo); // the drawing itself is still there to undo: the drag took one step
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Lasero repository root was not found.");
    }
}
