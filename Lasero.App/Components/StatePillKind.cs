namespace Lasero.App.Components;

/// <summary>
/// Semantic state a <see cref="StatusBadge"/> reports. Kinds map onto the palette's meaning rules,
/// not onto individual screens: green is only ever "ready", orange only "caution", red only
/// "error", and cobalt is the same interaction colour used for selection and the active tool.
/// </summary>
public enum StatePillKind
{
    /// <summary>Nothing is connected or the state is not yet known. Muted, not alarming.</summary>
    Neutral,

    /// <summary>The machine is connected, idle and safe to start.</summary>
    Ready,

    /// <summary>Work is in progress — running, framing or preparing.</summary>
    Busy,

    /// <summary>Paused or cancelled: recoverable, and waiting on the operator.</summary>
    Warning,

    /// <summary>An alarm, fault or aborted job. Needs attention before anything else runs.</summary>
    Error,
}
