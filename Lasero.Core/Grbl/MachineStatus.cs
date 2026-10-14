namespace Lasero.Core.Grbl;

/// <summary>
/// A fully parsed GRBL status report, e.g.
/// "&lt;Idle|MPos:0.000,0.000,0.000|FS:0,0|WCO:0.000,0.000,0.000&gt;".
/// Only one of MachinePosition/WorkPosition is guaranteed present on the wire
/// (depends on the $10 status report mask) — GrblStatusParser fills in the
/// other from the work coordinate offset whenever it can.
/// </summary>
public sealed record MachineStatus
{
    public required GrblMachineMode Mode { get; init; }
    public Position MachinePosition { get; init; }
    public Position WorkPosition { get; init; }
    public Position WorkCoordinateOffset { get; init; }

    /// <summary>
    /// True when <see cref="WorkCoordinateOffset"/> is a real offset (reported on this line, or
    /// remembered by GrblConnection from an earlier one). False means the offset is unknown and
    /// the Zero value must not be trusted.
    /// </summary>
    public bool WorkCoordinateOffsetKnown { get; init; }

    /// <summary>True when the wire report carried MPos (as opposed to it being derived).</summary>
    public bool ReportedMachinePosition { get; init; }

    /// <summary>True when the wire report carried WPos (as opposed to it being derived).</summary>
    public bool ReportedWorkPosition { get; init; }

    public double FeedRate { get; init; }
    public double SpindleSpeed { get; init; }
    public int? FeedOverridePercent { get; init; }
    public int? RapidOverridePercent { get; init; }
    public int? SpindleOverridePercent { get; init; }

    /// <summary>Triggered pins from the Pn: field, e.g. "XYZPDHRS" — raw GRBL letters.</summary>
    public string? TriggeredPins { get; init; }

    /// <summary>Planner buffer blocks available / RX bytes available, from Bf:.</summary>
    public (int PlannerAvailable, int RxAvailable)? Buffer { get; init; }

    /// <summary>Active work coordinate system number reported by $G/WCS query, if known separately.</summary>
    public int? ActiveWorkCoordinateSystem { get; init; }

    public string RawLine { get; init; } = string.Empty;
}
