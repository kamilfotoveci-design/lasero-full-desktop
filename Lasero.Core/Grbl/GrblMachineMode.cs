namespace Lasero.Core.Grbl;

/// <summary>
/// The machine's top-level state as reported in the leading field of a GRBL
/// status report (e.g. "&lt;Idle|MPos:...&gt;"). Gates which UI actions are safe.
/// </summary>
public enum GrblMachineMode
{
    Unknown,
    Idle,
    Run,
    Hold,
    Jog,
    Alarm,
    Door,
    Check,
    Home,
    Sleep,
}
