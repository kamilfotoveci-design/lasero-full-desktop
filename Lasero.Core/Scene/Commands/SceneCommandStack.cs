namespace Lasero.Core.Scene.Commands;

/// <summary>
/// Standard undo/redo command stack. A document replacement must call Clear so
/// commands from the previous project can never mutate the newly loaded scene.
/// </summary>
public sealed class SceneCommandStack
{
    private readonly Stack<ISceneCommand> _undoStack = new();
    private readonly Stack<ISceneCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? Changed;

    public void Execute(ISceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        _undoStack.Push(command);
        _redoStack.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var command = _redoStack.Pop();
        command.Do();
        _undoStack.Push(command);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_undoStack.Count == 0 && _redoStack.Count == 0) return;
        _undoStack.Clear();
        _redoStack.Clear();
        Changed?.Invoke();
    }
}
