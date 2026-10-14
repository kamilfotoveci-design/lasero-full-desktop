namespace Lasero.Core.Scene.Commands;

/// <summary>
/// Standard undo/redo command stack. A document replacement must call Clear so
/// commands from the previous project can never mutate the newly loaded scene.
/// </summary>
public sealed class SceneCommandStack
{
    private readonly Stack<ISceneCommand> _undoStack = new();
    private readonly Stack<ISceneCommand> _redoStack = new();

    // Undo depth at which the document was last saved; null once that state can no longer be
    // reached (a new command discarded the redo branch that contained it).
    private int? _savePointDepth = 0;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>True when the command history is exactly where it was at <see cref="MarkSaved"/> (or
    /// at the last <see cref="Clear"/>): undoing back to the saved state counts as unchanged. It only
    /// speaks for undoable commands; edits made outside the stack are the caller's to track.</summary>
    public bool IsAtSavePoint => _savePointDepth == _undoStack.Count;

    /// <summary>Records the current position in history as the saved state.</summary>
    public void MarkSaved() => _savePointDepth = _undoStack.Count;

    public event Action? Changed;

    public void Execute(ISceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        // The saved state lived in the redo branch that is about to be thrown away.
        if (_savePointDepth is { } saved && saved > _undoStack.Count) _savePointDepth = null;
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
        _savePointDepth = 0;
        if (_undoStack.Count == 0 && _redoStack.Count == 0) return;
        _undoStack.Clear();
        _redoStack.Clear();
        Changed?.Invoke();
    }
}
