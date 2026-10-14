using System.Windows.Threading;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

public sealed class DirtyAfterUndoTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static SceneObject MakeObject() => new()
    {
        Name = "Obj",
        LocalShapes =
        [
            new ImportedShape
            {
                Points = new[] { new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0) },
                IsClosed = false,
                LayerColor = RgbColor.Red,
                PreferredMode = LayerMode.Cut,
            },
        ],
        LocalPivot = new Position(5, 5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 10, 10),
    };

    [Fact]
    public void StackSavePointSurvivesUndoRedoAndIsLostWhenTheRedoBranchIsReplaced()
    {
        var scene = new SceneDocument();
        var stack = new SceneCommandStack();
        stack.Execute(new AddObjectCommand(scene, MakeObject(), scene.Layers));
        stack.MarkSaved();
        stack.Undo();
        Assert.False(stack.IsAtSavePoint);
        stack.Redo();
        Assert.True(stack.IsAtSavePoint);

        stack.Undo();
        stack.Execute(new AddObjectCommand(scene, MakeObject(), scene.Layers));
        Assert.False(stack.IsAtSavePoint); // same depth, different history
    }

    [Fact]
    public void UndoBackToTheSavedPointClearsDirtyAndRedoSetsItAgain() => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(1366, 768);
        var vm = shell.ViewModel;
        vm.MarkDocumentSaved();
        Assert.False(vm.IsDirty);

        vm.Scene.Execute(new AddObjectCommand(vm.Scene.Scene, MakeObject(), vm.Scene.Scene.Layers));
        Assert.True(vm.IsDirty);

        vm.Scene.UndoCommand.Execute(null);
        Assert.False(vm.IsDirty);

        vm.Scene.RedoCommand.Execute(null);
        Assert.True(vm.IsDirty);
    });

    [Fact]
    public void SavingAfterAnEditMakesUndoDirtyAndRedoCleanAgain() => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(1366, 768);
        var vm = shell.ViewModel;
        vm.Scene.Execute(new AddObjectCommand(vm.Scene.Scene, MakeObject(), vm.Scene.Scene.Layers));
        vm.MarkDocumentSaved();
        Assert.False(vm.IsDirty);

        vm.Scene.UndoCommand.Execute(null);
        Assert.True(vm.IsDirty);
        vm.Scene.RedoCommand.Execute(null);
        Assert.False(vm.IsDirty);
    });

    [Fact]
    public void AnEditOutsideTheHistoryKeepsTheDocumentDirtyEvenAfterUndoReachesTheSavePoint() => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(1366, 768);
        var vm = shell.ViewModel;
        vm.Scene.Execute(new AddObjectCommand(vm.Scene.Scene, MakeObject(), vm.Scene.Scene.Layers));
        vm.MarkDocumentSaved();
        vm.Scene.Execute(new AddObjectCommand(vm.Scene.Scene, MakeObject(), vm.Scene.Scene.Layers));

        vm.Scene.SelectedObjects.Clear();
        vm.Scene.SelectedObjects.Add(vm.Scene.Objects[0]);
        vm.Scene.ToggleLockCommand.Execute(null); // not an undoable command

        vm.Scene.UndoCommand.Execute(null);
        Assert.True(vm.IsDirty, "a lock toggle is not covered by the history, so the document differs from the saved one");
    });
}
