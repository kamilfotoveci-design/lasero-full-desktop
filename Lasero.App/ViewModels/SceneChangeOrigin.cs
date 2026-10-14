namespace Lasero.App.ViewModels;

/// <summary>Why <see cref="SceneViewModel.Changed"/> is being raised, for dirty tracking.</summary>
public enum SceneChangeOrigin
{
    /// <summary>Not part of the undo history (layer edit, lock toggle, import, ...). The document can no longer be
    /// proven equal to the saved one by walking the stack.</summary>
    Other,
    /// <summary>Raised from inside a command's Do/Undo; the history position is not final yet.</summary>
    HistoryInProgress,
    /// <summary>The command stack finished an Execute/Undo/Redo/Clear; its save-point answer is valid.</summary>
    HistoryCompleted,
}
