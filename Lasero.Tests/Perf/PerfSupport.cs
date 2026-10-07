using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace Lasero.Tests.Perf;

/// <summary>
/// The performance harness is opt-in. Normal `dotnet test` skips every <see cref="PerfFactAttribute"/>
/// test; run the whole table with
///
///     set LASERO_PERF=1
///     dotnet test Lasero.Tests --filter Category=Perf
///
/// Results are appended to the markdown file named by LASERO_PERF_OUT (default: perf-results.md in the
/// temp folder). The harness never sends input to the desktop and never touches the real
/// %LOCALAPPDATA%\Lasero: it drives the WPF canvas in-process on an STA dispatcher thread inside an
/// off-screen window, and generates its assets under the temp folder.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (!PerfEnvironment.Enabled)
            Skip = "Performance harness is opt-in: set LASERO_PERF=1 and run with --filter Category=Perf.";
    }
}

public static class PerfEnvironment
{
    public static bool Enabled => Environment.GetEnvironmentVariable("LASERO_PERF") == "1";

    /// <summary>Which timing set is being recorded: "before" / "after" / free text. Shown in the table.</summary>
    public static string Label => Environment.GetEnvironmentVariable("LASERO_PERF_LABEL") ?? "run";

    public static string AssetsDirectory
    {
        get
        {
            var directory = Environment.GetEnvironmentVariable("LASERO_PERF_ASSETS")
                ?? Path.Combine(Path.GetTempPath(), "lasero-perf-assets");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string ReportPath =>
        Environment.GetEnvironmentVariable("LASERO_PERF_OUT") ?? Path.Combine(Path.GetTempPath(), "perf-results.md");
}

/// <summary>One measured operation: wall-clock per iteration plus managed allocation per iteration.</summary>
public sealed record PerfResult(
    string Scenario, string Operation, int Iterations,
    double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    double AllocKbPerIteration, int Gen2Collections, string Note = "")
{
    public string ToMarkdownRow() => string.Create(CultureInfo.InvariantCulture,
        $"| {Scenario} | {Operation} | {Iterations} | {MeanMs:0.00} | {P50Ms:0.00} | {P95Ms:0.00} | {P99Ms:0.00} | {MaxMs:0.00} | {AllocKbPerIteration:0} | {Gen2Collections} | {Note} |");

    public static string Header =>
        "| Scenario | Operation | N | mean ms | p50 ms | p95 ms | p99 ms | max ms | alloc KB/iter | gen2 | note |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|";
}

public static class Perf
{
    private static readonly object Gate = new();

    /// <summary>
    /// Runs <paramref name="body"/> <paramref name="iterations"/> times after <paramref name="warmup"/> untimed
    /// runs and records mean/p50/p95/p99/max. <paramref name="between"/> runs after every iteration but is not
    /// timed (used to drain the dispatcher so queued render work from one frame is not billed to the next).
    /// Allocation is read from the calling thread, so call this on the thread that does the work.
    /// </summary>
    public static PerfResult Measure(
        string scenario, string operation, int iterations, Action<int> body,
        int warmup = 2, Action? between = null, string note = "", ITestOutputHelper? output = null)
    {
        for (var i = 0; i < warmup; i++)
        {
            body(-1 - i);
            between?.Invoke();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var gen2Before = GC.CollectionCount(2);
        var samples = new double[iterations];
        long allocated = 0;

        for (var i = 0; i < iterations; i++)
        {
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            body(i);
            var elapsed = Stopwatch.GetElapsedTime(start);
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            samples[i] = elapsed.TotalMilliseconds;
            between?.Invoke();
        }

        var gen2 = GC.CollectionCount(2) - gen2Before;
        var result = Summarize(scenario, operation, samples, allocated / 1024.0 / Math.Max(1, iterations), gen2, note);
        Record(result, output);
        return result;
    }

    public static PerfResult Summarize(
        string scenario, string operation, double[] samplesMs, double allocKbPerIteration, int gen2, string note = "")
    {
        var sorted = samplesMs.OrderBy(value => value).ToArray();
        double Percentile(double fraction)
        {
            if (sorted.Length == 0) return 0;
            var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        return new PerfResult(
            scenario, operation, sorted.Length,
            sorted.Length == 0 ? 0 : sorted.Average(),
            Percentile(0.50), Percentile(0.95), Percentile(0.99),
            sorted.Length == 0 ? 0 : sorted[^1],
            allocKbPerIteration, gen2, note);
    }

    /// <summary>Progress marker appended to the report immediately, so a hang shows where it stopped.</summary>
    public static void Note(string message)
    {
        lock (Gate)
            File.AppendAllText(PerfEnvironment.ReportPath, $"<!-- {DateTime.Now:HH:mm:ss} {message} -->\n");
    }

    public static void Record(PerfResult result, ITestOutputHelper? output = null)
    {
        lock (Gate)
        {
            var path = PerfEnvironment.ReportPath;
            var isNew = !File.Exists(path);
            using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
            if (isNew)
            {
                writer.WriteLine($"<!-- label: {PerfEnvironment.Label} -->");
                writer.WriteLine(PerfResult.Header);
            }

            writer.WriteLine(result.ToMarkdownRow());
        }

        output?.WriteLine(result.ToMarkdownRow());
    }
}
