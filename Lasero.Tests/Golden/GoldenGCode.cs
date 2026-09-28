using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lasero.Tests.Golden;

/// <summary>
/// Exact-comparison golden-file harness for every G-code emitter in LASERO.
///
/// The point of these files is to freeze physical machine behaviour before the G-code architecture
/// is refactored. A golden file is therefore compared <em>line for line, byte for byte</em> — the
/// only normalisation applied is the line separator, because that is decided by whichever text
/// editor last touched the file on disk and is provably irrelevant to the machine (every emitter
/// returns a <c>List&lt;string&gt;</c> of individual lines; nothing in LASERO ever emits a literal
/// CR or LF inside one of those strings, and the serial transport appends its own terminator).
///
/// Nothing else is normalised. Whitespace inside a line, argument order, decimal formatting, the
/// number of preamble repetitions and the exact M-code sequence are all part of what is being
/// protected: each of them changes what the machine physically does, or is a load-bearing signal
/// that something upstream changed.
///
/// To regenerate every golden file after an *intentional* behaviour change:
///     $env:LASERO_UPDATE_GOLDEN = "1"; dotnet test Lasero.Tests/Lasero.Tests.csproj --filter Golden
/// then read the resulting diff line by line before committing it. Regeneration is deliberately not
/// automatic on a miss — a silent rewrite would defeat the entire purpose of the suite.
/// </summary>
internal static class GoldenGCode
{
    private const string UpdateEnvironmentVariable = "LASERO_UPDATE_GOLDEN";

    internal static bool IsUpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) is "1" or "true" or "TRUE";

    /// <summary>
    /// Asserts <paramref name="actualLines"/> matches <c>Golden/gcode/{name}.gcode</c> exactly.
    /// </summary>
    internal static void Verify(string name, IEnumerable<string> actualLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(actualLines);

        var actual = actualLines.ToList();
        AssertNoEmbeddedLineBreaks(name, actual);

        var path = PathFor(name);

        if (!File.Exists(path))
        {
            if (!IsUpdateRequested)
                Assert.Fail(
                    $"Golden file '{name}.gcode' does not exist yet at {path}.{Environment.NewLine}" +
                    $"Re-run with {UpdateEnvironmentVariable}=1 to create it, then review the generated " +
                    "G-code line by line before committing it.");

            Write(path, actual);
            return;
        }

        var expected = Read(path);
        if (expected.SequenceEqual(actual, StringComparer.Ordinal)) return;

        if (IsUpdateRequested)
        {
            Write(path, actual);
            return;
        }

        Assert.Fail(BuildFailureMessage(name, path, expected, actual));
    }

    private static void AssertNoEmbeddedLineBreaks(string name, IReadOnlyList<string> lines)
    {
        // If an emitter ever starts returning multi-line strings, the per-line golden comparison
        // silently stops protecting the line structure. Fail loudly instead.
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains('\n') || lines[i].Contains('\r'))
                Assert.Fail($"Golden '{name}': emitted line {i} contains an embedded line break: {lines[i]}");
        }
    }

    private static string BuildFailureMessage(
        string name, string path, IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var message = new StringBuilder();
        message.AppendLine($"G-code output changed for golden '{name}'.");
        message.AppendLine($"Golden file: {path}");
        message.AppendLine($"Expected {expected.Count} lines, got {actual.Count}.");
        message.AppendLine();

        var firstDifference = FirstDifferenceIndex(expected, actual);
        if (firstDifference >= 0)
        {
            message.AppendLine($"First difference at line {firstDifference + 1}:");
            message.AppendLine($"  expected: {Describe(expected, firstDifference)}");
            message.AppendLine($"  actual:   {Describe(actual, firstDifference)}");
            message.AppendLine();
            message.AppendLine("Context (expected | actual):");
            var from = Math.Max(0, firstDifference - 3);
            var to = Math.Min(Math.Max(expected.Count, actual.Count), firstDifference + 4);
            for (var i = from; i < to; i++)
            {
                var marker = i == firstDifference ? ">>" : "  ";
                message.AppendLine($"  {marker} {i + 1,4}: {Describe(expected, i),-40} | {Describe(actual, i)}");
            }
        }

        message.AppendLine();
        message.AppendLine(
            "If this change was intentional, re-run with " +
            $"{UpdateEnvironmentVariable}=1 and review the regenerated file before committing. " +
            "If it was not, the refactor under test changed what the machine will physically do.");
        return message.ToString();
    }

    private static int FirstDifferenceIndex(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var shared = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < shared; i++)
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                return i;

        return expected.Count == actual.Count ? -1 : shared;
    }

    private static string Describe(IReadOnlyList<string> lines, int index) =>
        index < lines.Count ? lines[index] : "<end of file>";

    private static IReadOnlyList<string> Read(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static void Write(string path, IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Resolves the golden directory from this source file's own compile-time location rather than
    /// from the test binary's working directory. That keeps the golden files reviewable next to the
    /// tests in source control and needs no CopyToOutputDirectory entry in the csproj.
    /// </summary>
    private static string PathFor(string name, [CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "gcode", $"{name}.gcode");
}
