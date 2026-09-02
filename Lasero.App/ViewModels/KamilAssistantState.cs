namespace Lasero.App.ViewModels;

/// <summary>
/// The assistant's one visual state machine. Three shapes of the same object, not three views —
/// the conversation, the session and the composer text survive every transition between them.
/// </summary>
public enum KamilAssistantState
{
    /// <summary>A compact pill in the corner of the workspace. The idle state.</summary>
    Minimized,

    /// <summary>The pill grown into a composer: ask something without leaving the screen you are on.</summary>
    QuickAsk,

    /// <summary>The full assistant panel, opened upward from the composer's anchor.</summary>
    Expanded,

    /// <summary>Dismissed for this session. The conversation is kept; only the surface is gone.</summary>
    Hidden,
}
