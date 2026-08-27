using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

public sealed class DocumentLifecycleTests
{
    [Fact]
    public void CommandStack_ClearRemovesUndoAndRedoHistory()
    {
        var stack = new SceneCommandStack();
        var command = new RecordingCommand();
        stack.Execute(command);
        stack.Undo();

        stack.Clear();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void LoadingProjectCannotUndoIntoPreviousDocument()
    {
        var viewModel = new SceneViewModel();
        var previousObject = new SceneObject
        {
            LocalShapes = [],
            LocalPivot = Position.Zero,
            LocalBounds = BoundingBox2D.Empty,
            Name = "Předchozí objekt",
        };
        viewModel.Execute(new AddObjectCommand(viewModel.Scene, previousObject, []));
        Assert.True(viewModel.CanUndo);

        viewModel.LoadProject(new LaseroProjectFile { Name = "Nový dokument" });

        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
        Assert.Empty(viewModel.Objects);
    }

    private sealed class RecordingCommand : ISceneCommand
    {
        public void Do() { }
        public void Undo() { }
    }
}
