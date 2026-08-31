namespace Lasero.App.Components;

/// <summary>
/// What a long-running operation is doing right now. Deliberately the same five meanings as
/// <see cref="StatePillKind"/> plus an explicit "nothing has started" — the two types exist at
/// different sizes, not with different vocabularies, so a pill and a card can never disagree about
/// what green means.
/// </summary>
public enum ProcessStatus
{
    /// <summary>Nothing has been asked for yet. Neutral, no claim either way.</summary>
    Idle,

    /// <summary>Waiting on the operator or on hardware that has not answered. Neutral, not alarming.</summary>
    Waiting,

    /// <summary>Under way. Cobalt, the same interaction colour as selection and the active tool.</summary>
    Progress,

    /// <summary>Finished, and the result is confirmed. The only state that may be green.</summary>
    Success,

    /// <summary>Finished or stalled with something the operator should look at before continuing.</summary>
    Warning,

    /// <summary>Failed. Needs attention before anything else runs.</summary>
    Error,
}
