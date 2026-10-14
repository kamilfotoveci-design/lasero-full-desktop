using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Stacking-order commands act on the whole selection: a multi-selection keeps its relative
/// order, a group moves as one contiguous block, and every move is one undo step.</summary>
public class ZOrderSelectionTests
{
    private static SceneObject Square(string name, double x) =>
        VectorPathSceneFactory.Create(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = true,
                    Nodes =
                    [
                        VectorNode.CornerAt(new Position(x, 0, 0)), VectorNode.CornerAt(new Position(x + 10, 0, 0)),
                        VectorNode.CornerAt(new Position(x + 10, 10, 0)), VectorNode.CornerAt(new Position(x, 10, 0)),
                    ],
                },
            ],
        }, RgbColor.Black, name);

    private static (SceneViewModel Scene, SceneObject[] Items) Stack(int count)
    {
        var scene = new SceneViewModel();
        var items = Enumerable.Range(0, count).Select(index => Square(((char)('A' + index)).ToString(), index * 20)).ToArray();
        foreach (var item in items) scene.Objects.Add(item);
        return (scene, items);
    }

    private static string Order(SceneViewModel scene) => string.Concat(scene.Objects.Select(item => item.Name));

    [Fact]
    public void BringToFrontMovesAMultiSelectionAndKeepsItsOrder()
    {
        var (scene, items) = Stack(5); // ABCDE, E in front
        scene.SelectedObjects.ReplaceWith([items[3], items[1]]);

        Assert.True(scene.BringToFrontCommand.CanExecute(null));
        scene.BringToFrontCommand.Execute(null);
        Assert.Equal("ACEBD", Order(scene));

        scene.UndoCommand.Execute(null);
        Assert.Equal("ABCDE", Order(scene));
        scene.RedoCommand.Execute(null);
        Assert.Equal("ACEBD", Order(scene));
    }

    [Fact]
    public void SendToBackMovesAMultiSelectionAndKeepsItsOrder()
    {
        var (scene, items) = Stack(5);
        scene.SelectedObjects.ReplaceWith([items[4], items[2]]);

        scene.SendToBackCommand.Execute(null);

        Assert.Equal("CEABD", Order(scene));
    }

    [Fact]
    public void OneStepMovesStepEverySelectedObjectPastOneNeighbour()
    {
        var (scene, items) = Stack(5);
        scene.SelectedObjects.ReplaceWith([items[0], items[2]]);

        scene.BringForwardCommand.Execute(null);
        Assert.Equal("BADCE", Order(scene));

        scene.SendBackwardCommand.Execute(null);
        Assert.Equal("ABCDE", Order(scene));
    }

    [Fact]
    public void AGroupMovesAsOneContiguousBlockAndAnUnselectedGroupIsSteppedOverWhole()
    {
        var (scene, items) = Stack(5);
        // Group B and D (not adjacent), then select the group as a click would.
        scene.SelectedObjects.ReplaceWith([items[1], items[3]]);
        scene.GroupSelectionCommand.Execute(null);
        scene.SelectedObjects.ReplaceWith([items[1], items[3]]);

        Assert.True(scene.BringForwardCommand.CanExecute(null));
        scene.BringToFrontCommand.Execute(null);
        Assert.Equal("ACEBD", Order(scene));

        // Now select A; one step forward jumps over C (one object), the next over E.
        scene.SelectedObjects.ReplaceWith([items[0]]);
        scene.BringForwardCommand.Execute(null);
        Assert.Equal("CAEBD", Order(scene));
        scene.BringForwardCommand.Execute(null);
        scene.BringForwardCommand.Execute(null);
        // The grouped B,D pair is passed in one step, never split.
        Assert.Equal("CEBDA", Order(scene));
    }

    [Fact]
    public void ReorderIsOfferedOnlyWhenItWouldChangeSomething()
    {
        var (scene, items) = Stack(3);
        scene.SelectedObjects.ReplaceWith([items[1], items[2]]);

        Assert.False(scene.BringToFrontCommand.CanExecute(null));
        Assert.False(scene.BringForwardCommand.CanExecute(null));
        Assert.True(scene.SendToBackCommand.CanExecute(null));

        var revision = scene.CommandRevision;
        scene.BringToFrontCommand.Execute(null);
        Assert.Equal(revision, scene.CommandRevision);
    }
}
