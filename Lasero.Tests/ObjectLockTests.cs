using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Lock is an undoable edit, and a locked object is never out of reach: it cannot be selected,
/// moved or edited, but right-click offers "Odemknout" and "Odemknout vše" releases everything.</summary>
public class ObjectLockTests
{
    private static SceneObject Rectangle(SceneViewModel scene, double x)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x, 0, 0), new Position(x + 20, 10, 0));
        return scene.Objects[^1];
    }

    private static void Select(SceneViewModel scene, params SceneObject[] objects) =>
        scene.SelectedObjects.ReplaceWith(objects);

    [Fact]
    public void LockIsOneUndoStepAndUndoRedoRestoreIt()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var b = Rectangle(scene, 30);
        Select(scene, a, b);

        scene.ToggleLockCommand.Execute(null);
        Assert.True(a.IsLocked);
        Assert.True(b.IsLocked);
        Assert.False(scene.CanSelectObject(a));

        scene.UndoCommand.Execute(null);
        Assert.False(a.IsLocked);
        Assert.False(b.IsLocked);
        Assert.Contains(a, scene.Objects); // the drawing itself was not undone

        scene.RedoCommand.Execute(null);
        Assert.True(a.IsLocked);
        Assert.True(b.IsLocked);
    }

    [Fact]
    public void ALockedObjectIsUnreachableBySelectionButUnlockAllReleasesItInOneStep()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var b = Rectangle(scene, 30);
        Select(scene, a);
        scene.ToggleLockCommand.Execute(null);
        scene.SelectedObjects.Clear();

        scene.SelectAllCommand.Execute(null);
        Assert.Equal([b], scene.SelectedObjects);
        Assert.True(scene.UnlockAllCommand.CanExecute(null));

        scene.UnlockAllCommand.Execute(null);

        Assert.False(a.IsLocked);
        Assert.Equal([a], scene.SelectedObjects);
        Assert.False(scene.UnlockAllCommand.CanExecute(null));

        scene.UndoCommand.Execute(null);
        Assert.True(a.IsLocked);
        Assert.False(b.IsLocked);
    }

    [Fact]
    public void UnlockingARightClickedLockedObjectUnlocksItsGroupAndSelectsTheGroup()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var b = Rectangle(scene, 30);
        Select(scene, a, b);
        scene.GroupSelectionCommand.Execute(null);
        Select(scene, a, b);
        scene.ToggleLockCommand.Execute(null);
        scene.SelectedObjects.Clear();

        Assert.True(scene.IsUnlockableObject(a));
        scene.UnlockObject(a);

        Assert.False(a.IsLocked);
        Assert.False(b.IsLocked);
        Assert.Equal(new[] { a, b }, scene.SelectedObjects.OrderBy(item => scene.Objects.IndexOf(item)));
        scene.UndoCommand.Execute(null);
        Assert.True(a.IsLocked && b.IsLocked);
    }

    [Fact]
    public void LockingWhatIsAlreadyLockedRecordsNothing()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var revision = scene.CommandRevision;

        Assert.False(scene.UnlockAllCommand.CanExecute(null));
        scene.UnlockAllCommand.Execute(null);
        Assert.Equal(revision, scene.CommandRevision);

        a.IsLocked = true;
        Assert.False(new Lasero.Core.Scene.Commands.SetObjectsLockedCommand([a], locked: true).ChangesAnything);
    }

    [Fact]
    public void TheEmptyCanvasMenuOffersUnlockAllOnlyWhileSomethingIsLocked()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);

        var none = CanvasContextMenuBuilder.ForEmptyCanvas(CanvasContextMenuStates.ForEmptyCanvas(scene, false, false));
        Assert.DoesNotContain(none, row => row.Action == ContextAction.UnlockAll);

        Select(scene, a);
        scene.ToggleLockCommand.Execute(null);
        var some = CanvasContextMenuBuilder.ForEmptyCanvas(CanvasContextMenuStates.ForEmptyCanvas(scene, false, false));
        var unlockAll = Assert.Single(some, row => row.Action == ContextAction.UnlockAll);
        Assert.Equal("Odemknout vše", unlockAll.Header);
        Assert.True(unlockAll.IsEnabled);
    }

    [Fact]
    public void TheLockedObjectMenuLeadsWithUnlock()
    {
        var rows = CanvasContextMenuBuilder.ForLockedObject(new EmptyCanvasContextState { LockedCount = 1, ObjectCount = 1 });

        Assert.Equal(ContextAction.UnlockObject, rows[0].Action);
        Assert.Equal("Odemknout", rows[0].Header);
        Assert.Contains(rows, row => row.Action == ContextAction.UnlockAll);
    }

    [Fact]
    public void AnObjectOnALockedLayerIsNotOfferedForUnlocking()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        Select(scene, a);
        scene.ToggleLockCommand.Execute(null);
        var layer = scene.Layers.First(candidate => candidate.Id == a.LocalShapes[0].LayerId);
        layer.IsLocked = true;

        Assert.False(scene.IsUnlockableObject(a));
    }
}
