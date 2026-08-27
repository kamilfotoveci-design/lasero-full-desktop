namespace Lasero.Core.Jobs;

public enum JobRunState
{
    Idle,
    Preparing,
    Ready,
    Framing,
    Running,
    Paused,
    Completed,
    Cancelled,
    Error,
    Aborted,
    Faulted,
}
