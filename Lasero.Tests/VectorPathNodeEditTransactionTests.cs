using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Regression coverage for the P0.1 node-edit transactional fix: SceneCanvas.VectorPathTool's
/// RenderVectorPathLive used to mutate the live SceneObject's LocalShapes in place during a
/// node/handle drag, stripping LayerId/GeometrySetId, and that same mutable object reference was
/// later handed to ReplaceObjectsCommand as the "removed" (pre-edit) object — so Undo restored
/// whatever the drag last mutated it to, not the true pre-drag geometry.
///
/// These tests exercise the real production commit path (SceneViewModel.CommitVectorPathEdit ->
/// VectorPathSceneFactory.Rebuild -> ReplaceObjectsCommand) the same way SceneCanvas.VectorPathTool.cs
/// now does: build live-preview frames through VectorPathDragSession, then restore
/// VectorPathDragSession.OriginalShapes onto the object immediately before commit/cancel — see
/// FinishNodeEditDrag/CancelNodeEditDrag/ExitNodeEditMode in that file for the caller.
/// </summary>
public sealed class VectorPathNodeEditTransactionTests
{
    private static readonly RgbColor DrawColor = new(18, 18, 18);

    [Fact]
    public void NodeDragCommitUndoRedoPreservesGeometryLayerAndGeometrySetIdForCompoundPathWithHole()
    {
        var (viewModel, obj, layer, originalShapes, originalGeometrySetId) = SetUpCompoundPathWithHoleOnNonDefaultLayer();

        // Simulate the drag: a live-preview frame is rendered through the same session-based path
        // RenderVectorPathLive now uses, mutating obj.LocalShapes exactly like a real pointer-move would.
        var session = new VectorPathDragSession(originalShapes);
        var editedPath = MoveFirstNodeOfOuterSubpath(obj.VectorPath!, dx: 3, dy: -2);
        obj.LocalShapes = session.BuildPreviewShapes(editedPath, DrawColor);

        // Live-preview frame must already keep the assigned layer/geometry-set, not just the commit.
        Assert.All(obj.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.All(obj.LocalShapes, shape => Assert.Equal(originalGeometrySetId, shape.GeometrySetId));

        // Mouse-up: restore the pre-drag snapshot before touching the command stack (what
        // FinishNodeEditDrag does), then commit exactly one ReplaceObjectsCommand.
        obj.LocalShapes = session.OriginalShapes;
        viewModel.CommitVectorPathEdit(obj, editedPath);

        var committed = Assert.Single(viewModel.Objects);
        Assert.NotSame(obj, committed); // Rebuild always returns a new SceneObject instance
        Assert.Equal(obj.Id, committed.Id);
        Assert.All(committed.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.Equal(2, committed.LocalShapes.Count); // outer + hole both survive
        Assert.NotEqual(originalShapes[0].Points, committed.LocalShapes[0].Points); // geometry actually moved
        Assert.True(viewModel.CanUndo);

        viewModel.UndoCommand.Execute(null);

        var undone = Assert.Single(viewModel.Objects);
        Assert.Same(obj, undone); // the exact original instance, not a mutated one
        Assert.Same(originalShapes, undone.LocalShapes); // byte-for-byte the pre-drag snapshot
        Assert.Equal(2, undone.LocalShapes.Count);
        Assert.All(undone.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.All(undone.LocalShapes, shape => Assert.Equal(originalGeometrySetId, shape.GeometrySetId));

        viewModel.RedoCommand.Execute(null);

        var redone = Assert.Single(viewModel.Objects);
        Assert.Equal(obj.Id, redone.Id);
        Assert.All(redone.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.NotEqual(originalShapes[0].Points, redone.LocalShapes[0].Points);
    }

    [Fact]
    public void NodeDragCancelRestoresExactPreDragObjectWithoutTouchingUndoHistory()
    {
        var (viewModel, obj, layer, originalShapes, originalGeometrySetId) = SetUpCompoundPathWithHoleOnNonDefaultLayer();

        var session = new VectorPathDragSession(originalShapes);
        var editedPath = MoveFirstNodeOfOuterSubpath(obj.VectorPath!, dx: 10, dy: 10);
        obj.LocalShapes = session.BuildPreviewShapes(editedPath, DrawColor); // live preview frame(s)
        obj.LocalShapes = session.BuildPreviewShapes(
            MoveFirstNodeOfOuterSubpath(obj.VectorPath!, dx: -5, dy: 1), DrawColor); // another frame

        // Escape: CancelNodeEditDrag restores the snapshot directly, without ever calling CommitVectorPathEdit.
        obj.LocalShapes = session.OriginalShapes;

        Assert.Same(obj, Assert.Single(viewModel.Objects));
        Assert.Same(originalShapes, obj.LocalShapes);
        Assert.All(obj.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.All(obj.LocalShapes, shape => Assert.Equal(originalGeometrySetId, shape.GeometrySetId));
        Assert.False(viewModel.CanUndo); // cancelling never pushed a command
    }

    [Fact]
    public void NodeDragPreservesNonIdentityTransformThroughCommitAndUndo()
    {
        var (viewModel, obj, _, originalShapes, _) = SetUpCompoundPathWithHoleOnNonDefaultLayer();
        var transform = new ObjectTransform(X: 25, Y: -14, RotationDeg: 33, ScaleX: 1.4, ScaleY: 0.8);
        obj.Transform = transform;

        var session = new VectorPathDragSession(originalShapes);
        var editedPath = MoveFirstNodeOfOuterSubpath(obj.VectorPath!, dx: 4, dy: 4);
        obj.LocalShapes = session.BuildPreviewShapes(editedPath, DrawColor);
        obj.LocalShapes = session.OriginalShapes;

        viewModel.CommitVectorPathEdit(obj, editedPath);
        var committed = Assert.Single(viewModel.Objects);
        Assert.Equal(transform, committed.Transform);

        viewModel.UndoCommand.Execute(null);
        var undone = Assert.Single(viewModel.Objects);
        Assert.Equal(transform, undone.Transform);
    }

    [Fact]
    public void BuildPreviewShapesCarriesLayerAndGeometrySetIdEvenAsSubpathCountChanges()
    {
        var layer = LayerSettings.CreateDefault(new RgbColor(200, 30, 30), LayerMode.FillAndCut, "Gravírování");
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
        ]);
        var obj = VectorPathSceneFactory.Create(path, DrawColor, "Cesta");
        obj.AssignToLayer(layer);
        var geometrySetId = obj.LocalShapes[0].GeometrySetId;
        var original = obj.LocalShapes;

        var session = new VectorPathDragSession(original);

        // A second subpath appears mid-drag (e.g. a segment split) — every shape must still carry the
        // original layer/geometry-set identity, not just the first one.
        var twoSubpaths = new VectorPath
        {
            Subpaths =
            [
                path.Subpaths[0],
                new VectorSubpath
                {
                    Nodes = [VectorNode.CornerAt(new Position(0, 5, 0)), VectorNode.CornerAt(new Position(10, 5, 0))],
                    IsClosed = false,
                },
            ],
        };

        var preview = session.BuildPreviewShapes(twoSubpaths, DrawColor);

        Assert.Equal(2, preview.Count);
        Assert.All(preview, shape => Assert.Equal(layer.Id, shape.LayerId));
        Assert.All(preview, shape => Assert.Equal(geometrySetId, shape.GeometrySetId));
        Assert.All(preview, shape => Assert.Equal(layer.Color, shape.LayerColor));
    }

    // -----------------------------------------------------------------------------------------

    private static (SceneViewModel ViewModel, SceneObject Object, LayerSettings Layer,
        IReadOnlyList<ImportedShape> OriginalShapes, Guid GeometrySetId) SetUpCompoundPathWithHoleOnNonDefaultLayer()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(new RgbColor(0, 140, 60), LayerMode.Fill, "Vlastní vrstva");
        viewModel.Layers.Add(layer);

        var outer = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(20, 0, 0)),
                VectorNode.CornerAt(new Position(20, 20, 0)),
                VectorNode.CornerAt(new Position(0, 20, 0)),
            ],
            IsClosed = true,
        };
        var hole = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(5, 5, 0)),
                VectorNode.CornerAt(new Position(15, 5, 0)),
                VectorNode.CornerAt(new Position(15, 15, 0)),
                VectorNode.CornerAt(new Position(5, 15, 0)),
            ],
            IsClosed = true,
        };
        var path = new VectorPath { Subpaths = [outer, hole] };

        var obj = VectorPathSceneFactory.Create(path, DrawColor, "Rámeček s dírou");
        obj.AssignToLayer(layer);
        viewModel.Objects.Add(obj);

        var originalShapes = obj.LocalShapes;
        var geometrySetId = originalShapes[0].GeometrySetId;
        return (viewModel, obj, layer, originalShapes, geometrySetId);
    }

    private static VectorPath MoveFirstNodeOfOuterSubpath(VectorPath path, double dx, double dy)
    {
        var outer = path.Subpaths[0];
        var nodes = outer.Nodes.ToList();
        nodes[0] = nodes[0].Translated(dx, dy);
        return path.ReplaceSubpath(0, outer with { Nodes = nodes });
    }
}
