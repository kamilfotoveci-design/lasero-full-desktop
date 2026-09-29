using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lasero.Core.Layers;
using Lasero.Core.Raster;

namespace Lasero.Core.Trace;

/// <summary>
/// Optional adapter to a separately installed Potrace executable. LASERO does not distribute
/// Potrace: the public Potrace release is GPL licensed and the author offers a separate
/// proprietary integration license. Set LASERO_POTRACE_PATH to an absolute executable path.
/// </summary>
public static class PotraceCliTracer
{
    private const int MaxPixels = 16_000_000;
    private const long MaxSvgBytes = 64L * 1024 * 1024;
    private const int TimeoutSeconds = 45;

    public static bool IsAvailable => FindExecutable() is not null;

    public static IReadOnlyList<TracedVectorObject> Trace(
        GrayscaleImage image,
        BitmapTraceOptions options,
        RgbColor color,
        double scaleMmPerPixel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (image.Width <= 0 || image.Height <= 0 ||
            (long)image.Width * image.Height > MaxPixels ||
            image.Luminance.Count != (long)image.Width * image.Height)
            throw new ArgumentException("Invalid or oversized tracing image.", nameof(image));
        if (!double.IsFinite(scaleMmPerPixel) || scaleMmPerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleMmPerPixel));

        var executable = FindExecutable() ?? throw new FileNotFoundException(
            "Potrace is not installed. Set LASERO_POTRACE_PATH to a licensed potrace.exe.");
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(Path.GetTempPath(), "LaseroTrace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "input.pbm");
        var outputPath = Path.Combine(directory, "output.svg");
        try
        {
            WritePbm(inputPath, image, options, cancellationToken);
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
            // SVG retains cubic control points. Grouping keeps outer contours with their holes;
            // width is explicit so SVG user units map predictably back to source pixels.
            foreach (var argument in new[]
            {
                "--svg", "--group", "--width", image.Width.ToString(CultureInfo.InvariantCulture) + "pt",
                "--turdsize", Math.Max(0, options.MinimumFeaturePixels).ToString(CultureInfo.InvariantCulture),
                // Potrace's lower alphamax retains more corners; LASERO's higher Detail
                // likewise requests more corners.
                "--alphamax", (1.45 - options.Detail * 0.9).ToString("0.###", CultureInfo.InvariantCulture),
                "--opttolerance", (0.08 + options.Smoothness * 0.34).ToString("0.###", CultureInfo.InvariantCulture),
                "--output", outputPath, inputPath,
            }) process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start()) throw new IOException("Could not start Potrace.");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Potrace exceeded the tracing time limit.");
            }
            if (process.ExitCode != 0)
                throw new InvalidDataException("Potrace failed: " + stderr.GetAwaiter().GetResult());
            _ = stdout.GetAwaiter().GetResult();
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length > MaxSvgBytes)
                throw new InvalidDataException("Potrace produced no SVG or exceeded the SVG size limit.");

            return PotraceSvgParser.Parse(outputPath, image.Width, image.Height, scaleMmPerPixel, color);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* A still-exiting CLI may briefly hold a temp file. */ }
            catch (UnauthorizedAccessException) { /* The tracing error remains primary. */ }
        }
    }

    /// <summary>Only explicit local installation is trusted; no download or PATH executable lookup.</summary>
    public static string? FindExecutable()
    {
        var path = Environment.GetEnvironmentVariable("LASERO_POTRACE_PATH");
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path)
            ? path : null;
    }

    private static void WritePbm(string path, GrayscaleImage image, BitmapTraceOptions options, CancellationToken token)
    {
        var threshold = options.ThresholdMode == ThresholdMode.Auto
            ? OtsuThreshold(image.Luminance) : options.Threshold;
        var contrasted = new byte[image.Luminance.Count];
        for (var i = 0; i < contrasted.Length; i++)
            contrasted[i] = (byte)Math.Clamp((int)Math.Round(
                (image.Luminance[i] - 128) * options.ContrastAlpha + 128), 0, 255);
        var adaptive = options.ThresholdMode == ThresholdMode.Adaptive
            ? AdaptiveThresholds(contrasted, image.Width, image.Height) : null;
        var stride = (image.Width + 7) / 8;
        var row = new byte[stride];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = Encoding.ASCII.GetBytes($"P4\n{image.Width} {image.Height}\n");
        stream.Write(header);
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(row);
            for (var x = 0; x < image.Width; x++)
            {
                var luma = contrasted[y * image.Width + x];
                var cutoff = adaptive is null ? threshold : adaptive[y * image.Width + x];
                // PBM: one is ink. Invert is relative to the normal dark-on-light convention.
                var ink = options.Invert ? luma >= cutoff : luma < cutoff;
                if (ink) row[x / 8] |= (byte)(0x80 >> (x % 8));
            }
            stream.Write(row);
        }
    }

    private static byte OtsuThreshold(IReadOnlyList<byte> pixels)
    {
        Span<long> histogram = stackalloc long[256];
        foreach (var pixel in pixels) histogram[pixel]++;
        long total = pixels.Count, sum = 0, background = 0, backgroundSum = 0;
        for (var i = 0; i < 256; i++) sum += i * histogram[i];
        double best = -1;
        byte threshold = 128;
        for (var i = 0; i < 256; i++)
        {
            background += histogram[i];
            if (background == 0 || background == total) continue;
            backgroundSum += i * histogram[i];
            var foreground = total - background;
            var meanDelta = (double)backgroundSum / background - (double)(sum - backgroundSum) / foreground;
            var variance = background * (double)foreground * meanDelta * meanDelta;
            if (variance <= best) continue;
            best = variance;
            threshold = (byte)i;
        }
        return threshold;
    }

    private static byte[] AdaptiveThresholds(byte[] pixels, int width, int height)
    {
        // Local mean with an integral image keeps adaptive threshold O(pixels), even for large
        // logos photographed under uneven light. Subtract a small bias to avoid paper grain.
        var stride = width + 1;
        var sums = new long[checked((width + 1) * (height + 1))];
        for (var y = 0; y < height; y++)
        {
            long row = 0;
            for (var x = 0; x < width; x++)
            {
                row += pixels[y * width + x];
                sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + row;
            }
        }
        var radius = Math.Clamp(Math.Min(width, height) / 16, 2, 32);
        var output = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(height, y + radius + 1);
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(width, x + radius + 1);
                var total = sums[bottom * stride + right] - sums[top * stride + right] -
                            sums[bottom * stride + left] + sums[top * stride + left];
                var area = (right - left) * (bottom - top);
                output[y * width + x] = (byte)Math.Clamp(total / area - 4, 0, 255);
            }
        }
        return output;
    }
}
