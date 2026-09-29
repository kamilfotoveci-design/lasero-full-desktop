using System.Windows.Data;
using System.Windows.Input;

namespace Lasero.App.Input;

/// <summary>What one Esc press does inside the canvas, innermost layer first.</summary>
public enum CanvasEscapeAction
{
    /// <summary>Nothing temporary is open, so the key is not the canvas's to consume.</summary>
    None,
    CancelGesture,
    ExitInlineTextEdit,
    ExitNodeEdit,
    CancelPathNode,
    ResetTool,
    ClearSelection,
}

/// <summary>Canvas state that decides an Esc press. Plain data so the table can be tested.</summary>
public readonly record struct CanvasEscapeState(
    bool GestureActive,
    bool InlineTextEditing,
    bool NodeEditActive,
    bool PathInProgress,
    bool NonSelectToolActive,
    bool HasSelection);

/// <summary>What an unhandled Esc does once it has bubbled up to the main window.</summary>
public enum WindowEscapeAction
{
    None,
    /// <summary>A text field had nothing to revert: hand the keyboard back to the canvas.</summary>
    LeaveTextField,
    /// <summary>The canvas has a temporary state to peel, whatever currently holds focus.</summary>
    CanvasLayers,
    MinimizeAssistant,
}

/// <summary>What a key does in a single-line field whose binding waits for LostFocus.</summary>
public enum FieldKeyAction
{
    None,
    Commit,
    Revert,
}

/// <summary>
/// The key-routing tables behind docs/interaction-rules.md. Everything here is pure: the WPF glue in
/// <see cref="InteractionBehaviors"/> and the canvas only ask these functions what to do, so the
/// rules can be pinned by tests without a window.
/// </summary>
public static class InteractionRules
{
    // ------------------------------------------------------------------ Esc, canvas layers

    public static CanvasEscapeAction ForCanvasEscape(CanvasEscapeState state)
    {
        if (state.GestureActive) return CanvasEscapeAction.CancelGesture;
        if (state.InlineTextEditing) return CanvasEscapeAction.ExitInlineTextEdit;
        if (state.NodeEditActive) return CanvasEscapeAction.ExitNodeEdit;
        if (state.PathInProgress) return CanvasEscapeAction.CancelPathNode;
        if (state.NonSelectToolActive) return CanvasEscapeAction.ResetTool;
        if (state.HasSelection) return CanvasEscapeAction.ClearSelection;
        return CanvasEscapeAction.None;
    }

    // ------------------------------------------------------------------ Esc, window fallback

    /// <summary>
    /// Runs for an Esc nothing below claimed. Order: a field the user is standing in, then the
    /// canvas's own layers (so Esc works with focus on a button or the inspector), then the
    /// assistant. Off the Designer screen only the assistant rule is moot, so nothing happens.
    /// </summary>
    public static WindowEscapeAction ForWindowEscape(
        bool designerScreen, bool focusInTextInput, bool canvasHasEscapableState, bool assistantOpen)
    {
        if (!designerScreen) return WindowEscapeAction.None;
        if (focusInTextInput) return WindowEscapeAction.LeaveTextField;
        if (canvasHasEscapableState) return WindowEscapeAction.CanvasLayers;
        if (assistantOpen) return WindowEscapeAction.MinimizeAssistant;
        return WindowEscapeAction.None;
    }

    /// <summary>
    /// Secondary windows cancel or close on Esc. The main window, sign-in and first-run onboarding
    /// are exempt because closing them ends or changes the session; the shared dialog shell already
    /// handles Esc itself.
    /// </summary>
    public static bool WindowClosesOnEscape(Type windowType) =>
        windowType != typeof(MainWindow) &&
        windowType != typeof(LoginWindow) &&
        windowType != typeof(OnboardingWindow) &&
        windowType != typeof(LaseroDialogWindow);

    // ------------------------------------------------------------------ deferred numeric fields

    /// <summary>A Text binding commits on losing focus unless it says otherwise. Those are the
    /// fields that can hold an uncommitted edit, so Enter and Esc mean something in them.</summary>
    public static bool IsDeferredTrigger(UpdateSourceTrigger trigger) =>
        trigger is UpdateSourceTrigger.Default or UpdateSourceTrigger.LostFocus or UpdateSourceTrigger.Explicit;

    public static FieldKeyAction ForFieldKey(Key key, bool acceptsReturn, bool deferredBinding, bool hasPendingEdit)
    {
        if (!deferredBinding) return FieldKeyAction.None;
        if (key == Key.Enter && !acceptsReturn) return FieldKeyAction.Commit;
        if (key == Key.Escape && hasPendingEdit) return FieldKeyAction.Revert;
        return FieldKeyAction.None;
    }

    // ------------------------------------------------------------------ scene shortcuts

    /// <summary>Every key that acts on the design: bound at the window (Delete, clipboard, undo,
    /// duplicate, select all, trace, boolean ops, offset) or routed in MainWindow (group, ungroup).</summary>
    public static bool IsSceneShortcut(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.Delete, ModifierKeys.None) => true,
        (Key.D or Key.Z or Key.Y or Key.C or Key.X or Key.V or Key.A or Key.G, ModifierKeys.Control) => true,
        (Key.Z or Key.G, ModifierKeys.Control | ModifierKeys.Shift) => true,
        (Key.U or Key.O, ModifierKeys.Control | ModifierKeys.Shift) => true,
        (Key.T, ModifierKeys.Alt) => true,
        _ => false,
    };

    /// <summary>The subset of scene shortcuts a text field already owns. Suppressing these would
    /// break typing, so they are never touched while a field has the keyboard.</summary>
    public static bool TextFieldOwnsKey(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.Delete, ModifierKeys.None) => true,
        (Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y, ModifierKeys.Control) => true,
        (Key.Z, ModifierKeys.Control | ModifierKeys.Shift) => true,
        _ => false,
    };

    /// <summary>Scene shortcuts must not reach the hidden design from Home, Device or Chat.</summary>
    public static bool SuppressSceneShortcut(bool designerScreen, Key key, ModifierKeys modifiers, bool focusInTextInput)
    {
        if (designerScreen || !IsSceneShortcut(key, modifiers)) return false;
        return !(focusInTextInput && TextFieldOwnsKey(key, modifiers));
    }

    /// <summary>Shortcuts that change the design. They are refused while a pointer gesture is in
    /// progress: Delete mid-move would commit a transform for objects that no longer exist, and
    /// Undo mid-drag would rewind history under a live preview. Copy and select-all are harmless.</summary>
    public static bool IsMutatingSceneShortcut(Key key, ModifierKeys modifiers) =>
        IsSceneShortcut(key, modifiers) &&
        !(key is Key.C or Key.A && modifiers == ModifierKeys.Control);
}
