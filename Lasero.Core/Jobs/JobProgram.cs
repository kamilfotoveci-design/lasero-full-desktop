using System.Globalization;

namespace Lasero.Core.Jobs;

/// <summary>
/// Air assist control. GRBL switches coolant outputs with M7 (mist) and M8 (flood); laser machines
/// wire the air pump to whichever one their controller board exposes. M9 turns both off.
/// </summary>
public enum AirAssistMode
{
    Off = 0,
    /// <summary>M8, the output most GRBL boards enable by default.</summary>
    M8 = 1,
    /// <summary>M7, available only when the firmware was built with mist coolant enabled.</summary>
    M7 = 2,
}

/// <summary>What the operator's device settings add around the job's own G-code.</summary>
public sealed record JobProgramOptions
{
    public AirAssistMode AirAssist { get; init; } = AirAssistMode.Off;

    /// <summary>Work position the head travels to (laser off) after the last job line, or null to stay
    /// where the job ended.</summary>
    public (double X, double Y)? ReturnTo { get; init; }

    public bool IsEmpty => AirAssist == AirAssistMode.Off && ReturnTo is null;
}

/// <summary>The streamed program: <see cref="Prologue"/> lines, then the job, then <see cref="Epilogue"/> lines.</summary>
public sealed record JobProgramLines(IReadOnlyList<string> Lines, int PrologueCount, int EpilogueCount)
{
    public int JobLineCount => Lines.Count - PrologueCount - EpilogueCount;
}

/// <summary>
/// Wraps a prepared job in the device-level commands that do not belong to any layer: switching air
/// assist on before the first line and off after the last, and returning the head to a known position.
///
/// The wrapper is applied to the lines that are streamed, never to the document that is previewed,
/// estimated and checked by preflight, so the return move cannot stretch the job bounds. Every added
/// move is a G0 issued after M5; the laser is never enabled by anything here. A soft reset (Stop,
/// fault) makes GRBL switch coolant off by itself, so no abort path needs its own M9.
/// </summary>
public static class JobProgram
{
    public static IReadOnlyList<string> Prologue(JobProgramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AirAssist switch
        {
            AirAssistMode.M8 => ["M8"],
            AirAssistMode.M7 => ["M7"],
            _ => [],
        };
    }

    public static IReadOnlyList<string> Epilogue(JobProgramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsEmpty) return [];

        // The laser is switched off explicitly first: the return travel must never run with it enabled,
        // whatever state the job's last line left behind.
        var lines = new List<string> { "M5" };
        if (options.AirAssist != AirAssistMode.Off) lines.Add("M9");
        if (options.ReturnTo is { } target)
        {
            if (!double.IsFinite(target.X) || !double.IsFinite(target.Y))
                throw new ArgumentOutOfRangeException(nameof(options), "Cílová poloha návratu musí být konečné číslo.");
            lines.Add("G90");
            lines.Add($"G0 X{Fmt(target.X)} Y{Fmt(target.Y)}");
        }

        return lines;
    }

    public static JobProgramLines Wrap(IReadOnlyList<string> jobLines, JobProgramOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobLines);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsEmpty) return new JobProgramLines(jobLines, 0, 0);

        var prologue = Prologue(options);
        var epilogue = Epilogue(options);
        var all = new List<string>(prologue.Count + jobLines.Count + epilogue.Count);
        all.AddRange(prologue);
        all.AddRange(jobLines);
        all.AddRange(epilogue);
        return new JobProgramLines(all, prologue.Count, epilogue.Count);
    }

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
