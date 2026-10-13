namespace Lasero.App.Views.Kamil;

/// <summary>
/// The character's expression, independent of the assistant panel's size and position.
/// Blink is a transient request; it never replaces the current conversational state.
/// </summary>
public enum KamilAvatarState
{
    Idle,
    Blink,
    Attentive,
    Thinking,
    Responding,
    Happy,
    Error,
    Hidden,
}
