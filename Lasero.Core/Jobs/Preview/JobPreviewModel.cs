using Lasero.Core.GCode;
using Lasero.Core.Layers;

namespace Lasero.Core.Jobs;

/// <summary>One manufacturing operation as the preview legend names it: a swatch, a name and a detail line.</summary>
public sealed record PreviewOperation(string Name, string Detail, RgbColor Color);

/// <summary>
/// The operation that produced a run of generated G-code lines. Line numbers are zero based, the same numbering
/// <see cref="GCodeSegment.SourceLine"/> uses, so a parsed segment can be traced back to its operation.
/// </summary>
public readonly record struct PreviewLineSpan(int OperationIndex, int FirstLine, int LineCount);

/// <summary>
/// Everything the job preview window draws, computed once from the parsed toolpath: the segments, the timing
/// model, which operation each segment belongs to, how strong the beam is on it and the flattened polylines.
/// Purely derived data; the preview never reads from or writes to a machine.
/// </summary>
public sealed class JobPreviewModel
{
    public const int NoOperation = -1;

    public required GCodeDocument Document { get; init; }
    public required JobTimeEstimate Estimate { get; init; }
    public required JobPreviewStats Stats { get; init; }
    public required IReadOnlyList<PreviewOperation> Operations { get; init; }

    /// <summary>Per segment: index into <see cref="Operations"/>, or <see cref="NoOperation"/>.</summary>
    public required int[] SegmentOperation { get; init; }

    /// <summary>Per segment: true for rapid moves and moves with the laser off (the legend's "Přesunout").</summary>
    public required bool[] SegmentIsTravel { get; init; }

    /// <summary>Per segment: beam strength bucket 1..<see cref="PowerShading.Levels"/>, see <see cref="PowerShading"/>.</summary>
    public required byte[] SegmentPowerLevel { get; init; }

    /// <summary>Per operation: whether any cutting segment uses it. Unused operations stay out of the legend.</summary>
    public required bool[] OperationUsed { get; init; }

    public required double MaxPower { get; init; }
    public required PreviewPathGeometry Path { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>Non-blocking remarks shown with the preview, for example a job that leaves the work area.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool HasTravel => Stats.RapidDistanceMm > 0;

    /// <summary>The bounds the preview fits to: the cutting extent, or all moves when nothing cuts.</summary>
    public BoundingBox2D FitBounds => Document.BoundingBox.IsEmpty ? Path.Bounds : Document.BoundingBox;

    public static bool IsTravel(GCodeSegment segment) => segment.IsRapid || !segment.LaserOn;

    public static JobPreviewModel Create(
        GCodeDocument document,
        IReadOnlyList<PreviewOperation> operations,
        IReadOnlyList<PreviewLineSpan> spans,
        double maxPower,
        IReadOnlyList<string>? notes = null,
        MachineMotionProfile? profile = null,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(spans);

        var estimate = JobTimeEstimator.Estimate(document, profile);
        var count = document.Segments.Count;
        var segmentOperation = new int[count];
        var travel = new bool[count];
        var level = new byte[count];
        var used = new bool[operations.Count];

        for (var i = 0; i < count; i++)
        {
            var segment = document.Segments[i];
            var operation = OperationForLine(spans, segment.SourceLine);
            if (operation >= operations.Count) operation = NoOperation;
            segmentOperation[i] = operation;
            travel[i] = IsTravel(segment);
            level[i] = (byte)PowerShading.Level(PowerShading.Fraction(segment.Power, maxPower));
            if (!travel[i] && operation >= 0) used[operation] = true;
        }

        return new JobPreviewModel
        {
            Document = document,
            Estimate = estimate,
            Stats = JobPreviewStats.Compute(document, estimate),
            Operations = operations,
            SegmentOperation = segmentOperation,
            SegmentIsTravel = travel,
            SegmentPowerLevel = level,
            OperationUsed = used,
            MaxPower = maxPower,
            Path = PreviewPathGeometry.Build(document.Segments),
            Notes = notes ?? [],
            Title = title ?? document.SourceFileName ?? string.Empty,
        };
    }

    /// <summary>
    /// The operation owning a G-code line, from spans ordered by first line (the order they were generated in).
    /// Lines outside every span, such as a header, belong to no operation.
    /// </summary>
    public static int OperationForLine(IReadOnlyList<PreviewLineSpan> spans, int line)
    {
        var low = 0;
        var high = spans.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (spans[middle].FirstLine <= line)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0) return NoOperation;
        var span = spans[found];
        return line < span.FirstLine + span.LineCount ? span.OperationIndex : NoOperation;
    }
}
