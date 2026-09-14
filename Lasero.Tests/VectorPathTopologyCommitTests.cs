using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// End-to-end coverage (LB-VEC-024, LB-VEC-040) for the new break-at-node / delete-segment topology
/// operations going through the real commit path — SceneViewModel.CommitVectorPathEdit ->
/// VectorPathSceneFactory.Rebuild -> ReplaceObjectsCommand — the same way SceneCanvas's
/// BreakSelectedNode/DeleteHoveredSegment now do, confirming metadata/undo survive an operation that
/// changes the object's subpath COUNT, not just its geometry.
/// </summary>
public sealed class VectorPathTopologyCommitTests
{
    private static readonly RgbColor DrawColor = new(18, 18, 18);

    [Fact]
    public void BreakAtNodeCommitPreservesLayerAndUndoRestoresTheClosedLoop()
    {
        var (viewModel, obj, layer) = SetUpClosedSquareOnNonDefaultLayer();

        var broken = VectorPathEditor.BreakAtNode(obj.VectorPath!, 0, nodeIndex: 1);
        viewModel.CommitVectorPathEdit(obj, broken);

        var committed = Assert.Single(viewModel.Objects);
        var resultSubpath = Assert.Single(committed.VectorPath!.Subpaths);
        Assert.False(resultSubpath.IsClosed);
        Assert.All(committed.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));

        viewModel.UndoCommand.Execute(null);
        var undone = Assert.Single(viewModel.Objects);
        Assert.Same(obj, undone);
        Assert.True(undone.VectorPath!.Subpaths[0].IsClosed);

        viewModel.RedoCommand.Execute(null);
        var redone = Assert.Single(viewModel.Objects);
        Assert.False(redone.VectorPath!.Subpaths[0].IsClosed);
    }

    [Fact]
    public void DeleteSegmentCommitOnClosedPathOpensItAndPreservesMetadataThroughUndoRedo()
    {
        var (viewModel, obj, layer) = SetUpClosedSquareOnNonDefaultLayer();

        var opened = VectorPathEditor.DeleteSegment(obj.VectorPath!, 0, segmentIndex: 0);
        viewModel.CommitVectorPathEdit(obj, opened);

        var committed = Assert.Single(viewModel.Objects);
        var resultSubpath = Assert.Single(committed.VectorPath!.Subpaths);
        Assert.False(resultSubpath.IsClosed);
        Assert.Equal(4, resultSubpath.Nodes.Count);
        Assert.All(committed.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));

        viewModel.UndoCommand.Execute(null);
        Assert.True(Assert.Single(viewModel.Objects).VectorPath!.Subpaths[0].IsClosed);

        viewModel.RedoCommand.Execute(null);
        Assert.False(Assert.Single(viewModel.Objects).VectorPath!.Subpaths[0].IsClosed);
    }

    [Fact]
    public void DeleteInternalSegmentOnOpenPathCommitsTwoSubpathsInOneObject()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(20, 0, 0)),
                VectorNode.CornerAt(new Position(30, 0, 0)),
            ],
            IsClosed = false,
        };
        var path = new VectorPath { Subpaths = [subpath] };
        var viewModel = new SceneViewModel();
        var obj = VectorPathSceneFactory.Create(path, DrawColor, "Otevřená cesta");
        viewModel.Objects.Add(obj);

        var split = VectorPathEditor.DeleteSegment(path, 0, segmentIndex: 1);
        viewModel.CommitVectorPathEdit(obj, split);

        var committed = Assert.Single(viewModel.Objects);
        Assert.Equal(2, committed.VectorPath!.Subpaths.Count);
        Assert.Equal(2, committed.LocalShapes.Count); // one ImportedShape per surviving subpath
    }

    private static (SceneViewModel ViewModel, SceneObject Object, LayerSettings Layer) SetUpClosedSquareOnNonDefaultLayer()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(new RgbColor(10, 90, 200), LayerMode.Cut, "Vlastní vrstva");
        viewModel.Layers.Add(layer);

        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
                VectorNode.CornerAt(new Position(0, 10, 0)),
            ],
            IsClosed = true,
        };
        var path = new VectorPath { Subpaths = [subpath] };
        var obj = VectorPathSceneFactory.Create(path, DrawColor, "Čtverec");
        obj.AssignToLayer(layer);
        viewModel.Objects.Add(obj);
        return (viewModel, obj, layer);
    }
}
