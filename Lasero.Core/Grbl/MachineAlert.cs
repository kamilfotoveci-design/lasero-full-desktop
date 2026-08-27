namespace Lasero.Core.Grbl;

public enum MachineAlertKind
{
    Error,
    Alarm,
}

/// <summary>
/// One active alarm/error the operator hasn't cleared yet. GrblConnection tracks at most one at
/// a time (the most recent), set alongside its existing ErrorReceived/AlarmReceived events — see
/// GrblConnection.ActiveAlert / AlertChanged. Errors clear on the next "ok" (GRBL errors are
/// per-command, not sticky); alarms clear once a status report shows the controller left Alarm
/// mode (i.e. a real unlock/reset took effect), or either can be dismissed manually.
/// </summary>
public sealed record MachineAlert
{
    public required MachineAlertKind Kind { get; init; }
    public required int Code { get; init; }
    public required string Message { get; init; }
    public required DateTime OccurredUtc { get; init; }
}
