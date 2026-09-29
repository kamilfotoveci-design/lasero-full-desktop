using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Lasero.Core.Grbl;
using Lasero.Core.Import.Svg;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>
/// Runs the app-bundled, MIT-licensed VTracer CLI. Its SVG is an interchange format only:
/// every path is converted into an editable VectorPath without flattening its cubic controls.
/// </summary>
public static class VTracerColorTracer
{
    private const long MaxSvgBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static bool IsAvailable => FindExecutable() is not null;

    /// <param name="color">True for quantized color regions; false for binary foreground contours.</param>
    /// <param name="executablePath">An explicit full path for controlled installations and tests.</param>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<TracedVectorObject> Trace(
        string filePath, BitmapTraceOptions options, bool color,
        CancellationToken cancellationToken = default, string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!File.Exists(filePath)) throw new FileNotFoundException("Trace image was not found.", filePath);
        var executable = FindExecutable(executablePath) ?? throw new FileNotFoundException("Bundled VTracer executable was not found.");
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(Path.GetTempPath(), "LaseroVTracer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var svgPath = Path.Combine(directory, "trace.svg");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            var args = process.StartInfo.ArgumentList;
            args.Add("--input"); args.Add(Path.GetFullPath(filePath));
            args.Add("--output"); args.Add(svgPath);
            args.Add("--clustering"); args.Add(color ? "color-cluster" : "bw");
            args.Add("--mode"); args.Add("spline");
            args.Add("--optimize"); args.Add("0"); // keep SVG path data simple and explicit
            args.Add("--filter-speckle");
            args.Add(Math.Clamp((int)Math.Round(Math.Max(options.MinimumFeaturePixels, options.NoiseRemoval * 60)), 0, 128)
                .ToString(CultureInfo.InvariantCulture));
            args.Add("--corner-threshold");
            args.Add(options.CornerAngleThresholdDegrees.ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("--simplify");
            args.Add(Math.Clamp(options.SimplificationPixels, 0, 4).ToString("0.###", CultureInfo.InvariantCulture));
            if (color)
            {
                args.Add("--hierarchical"); args.Add("cutout");
                args.Add("--max-colors"); args.Add("8");
            }
            else if (options.ThresholdMode == ThresholdMode.Adaptive)
            {
                args.Add("--adaptive");
            }
            else
            {
                args.Add("--threshold"); args.Add(options.Threshold.ToString(CultureInfo.InvariantCulture));
            }

            if (!process.Start()) throw new IOException("Could not start VTracer.");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("VTracer exceeded the tracing time limit.");
            }
            if (process.ExitCode != 0)
                throw new InvalidDataException("VTracer failed: " + stderr.GetAwaiter().GetResult());
            _ = stdout.GetAwaiter().GetResult();
            if (!File.Exists(svgPath) || new FileInfo(svgPath).Length is 0 or > MaxSvgBytes)
                throw new InvalidDataException("VTracer produced no SVG or exceeded the SVG size limit.");
            return ParseSvg(svgPath, options.TargetWidthMm, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* A still-exiting CLI may briefly hold a temporary file. */ }
            catch (UnauthorizedAccessException) { /* Keep the original tracing error. */ }
        }
    }

    public static string? FindExecutable(string? explicitPath = null)
    {
        if (explicitPath is not null)
            return Path.IsPathFullyQualified(explicitPath) && File.Exists(explicitPath) ? explicitPath : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "vtracer.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    private static IReadOnlyList<TracedVectorObject> ParseSvg(
        string svgPath, double targetWidthMm, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(svgPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSvgBytes,
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("VTracer SVG is empty.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("VTracer output is not SVG.");
        var width = ParseDimension(root, "width");
        var height = ParseDimension(root, "height");
        var mmPerPixel = targetWidthMm / width;
        var heightMm = height * mmPerPixel;
        var output = new List<TracedVectorObject>();
        foreach (var element in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name.LocalName != "path" || element.HasElements || element.Attribute("transform") is not null)
                throw new InvalidDataException("VTracer SVG contains an unsupported element or transform.");
            var fill = (string?)element.Attribute("fill");
            if (!RgbColor.TryParse(fill, out var rgb) && !TryParseWhite(fill, out rgb))
                throw new InvalidDataException("VTracer SVG contains an unsupported fill color.");
            var data = (string?)element.Attribute("d") ?? throw new InvalidDataException("VTracer SVG path has no geometry.");
            // The pinned CLI with --optimize 0 emits explicit M/L/C/Z path data. Reject a future
            // format change rather than accepting SvgPathParser's intentionally partial parse result.
            if (data.Any(c => char.IsLetter(c) && c is not ('M' or 'L' or 'C' or 'Z')))
                throw new InvalidDataException("VTracer SVG contains an unsupported path command.");
            var parsed = SvgPathParser.ParseToVectorSubpaths(data);
            if (parsed.Count != data.Count(c => c == 'M'))
                throw new InvalidDataException("VTracer SVG path was only partially parsed.");
            var subpaths = new List<VectorSubpath>();
            foreach (var source in parsed)
            {
                if (!source.IsClosed || source.Nodes.Count < 3) continue;
                var nodes = source.Nodes.Select(node => new VectorNode(
                    ToMm(node.Anchor),
                    node.HandleIn is { } handleIn ? ToMm(handleIn) : null,
                    node.HandleOut is { } handleOut ? ToMm(handleOut) : null,
                    node.Type)).ToList();
                // VTracer often writes an explicit final node coincident with M before Z. Transfer
                // its incoming handle to the first node and remove the duplicate closure node.
                if (nodes.Count > 3 && SamePosition(nodes[0].Anchor, nodes[^1].Anchor))
                {
                    nodes[0] = nodes[0] with { HandleIn = nodes[^1].HandleIn };
                    nodes.RemoveAt(nodes.Count - 1);
                }
                if (nodes.Count >= 3)
                {
                    CollapseStraightCubics(nodes);
                    subpaths.Add(new VectorSubpath { Nodes = nodes, IsClosed = true });
                }
            }
            if (subpaths.Count > 0)
                output.Add(new TracedVectorObject(new VectorPath { Subpaths = subpaths }, rgb));
        }
        return output;

        Position ToMm(Position point)
        {
            var x = point.X * mmPerPixel;
            var y = heightMm - point.Y * mmPerPixel;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new InvalidDataException("VTracer SVG contains non-finite geometry.");
            return new Position(x, y, 0);
        }
    }

    private static double ParseDimension(XElement root, string name)
    {
        if (!double.TryParse((string?)root.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value <= 0 || value > 100_000)
            throw new InvalidDataException("VTracer SVG has invalid image dimensions.");
        return value;
    }

    private static bool TryParseWhite(string? fill, out RgbColor color)
    {
        color = new RgbColor(255, 255, 255);
        return string.Equals(fill, "#FFFFFF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePosition(Position a, Position b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    /// <summary>
    /// VTracer sometimes serializes a straight rectangle side as a cubic whose controls lie on
    /// the side. Keep its corner anchors but discard those redundant controls, so Node Edit and
    /// the curve renderer agree that the side is an exact line. Controls outside the side's
    /// endpoints are retained: a collinear cubic can still overshoot and would not be a line.
    /// </summary>
    private static void CollapseStraightCubics(List<VectorNode> nodes)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var next = (index + 1) % nodes.Count;
            var a = nodes[index];
            var b = nodes[next];
            if (a.HandleOut is null && b.HandleIn is null) continue;
            var from = a.Anchor;
            var to = b.Anchor;
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-18) continue;
            var epsilon = Math.Max(1e-8, Math.Sqrt(lengthSquared) * 1e-8);
            if (!OnSegment(a.HandleOut ?? from) || !OnSegment(b.HandleIn ?? to)) continue;
            nodes[index] = a with { HandleOut = null };
            nodes[next] = b with { HandleIn = null };

            bool OnSegment(Position point)
            {
                var px = point.X - from.X;
                var py = point.Y - from.Y;
                var distance = Math.Abs(dx * py - dy * px) / Math.Sqrt(lengthSquared);
                var projection = (px * dx + py * dy) / lengthSquared;
                return distance <= epsilon && projection >= -1e-8 && projection <= 1 + 1e-8;
            }
        }
    }
}
