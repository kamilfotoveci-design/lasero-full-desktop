namespace Lasero.Core.Raster;

/// <summary>
/// The tonal adjustments applied to a bitmap before it becomes laser dots.
///
/// This is the same set, in the same order, with the same formulas as the web app's photo tool
/// (index.html, applyFilters). A photograph prepared in either place has to reach the machine
/// identically - an owner who set up an image in the browser and then opened it on the desktop was
/// getting a different burn from the same file and the same numbers.
/// </summary>
public sealed record ImageProcessingOptions
{
    /// <summary>Midtone curve. 1 = no change; below 1 darkens, above 1 lightens.</summary>
    public double Gamma { get; init; } = 1;

    /// <summary>Stops of exposure, -100..100 on the operator's scale. 0 = no change.</summary>
    public double Exposure { get; init; }

    /// <summary>Additive, -255..255. 0 = no change.</summary>
    public double Brightness { get; init; }

    /// <summary>-255..255. 0 = no change, matching the standard photographic contrast-correction formula.</summary>
    public double Contrast { get; init; }

    /// <summary>-100..100. Pulls the bright half of the range up or down and leaves the dark half alone.</summary>
    public double Highlights { get; init; }

    /// <summary>-100..100. Lifts or deepens the dark half of the range and leaves the bright half alone.</summary>
    public double Shadows { get; init; }

    /// <summary>Input level mapped to black, 0..255. Together with WhitePoint this is a levels remap.</summary>
    public double BlackPoint { get; init; }

    /// <summary>Input level mapped to white, 0..255.</summary>
    public double WhitePoint { get; init; } = 255;

    public bool Invert { get; init; }

    /// <summary>0..100. A 3x3 box blur blended in by this much, to stop sensor grain from becoming
    /// dither noise. Runs before sharpening, as it does in the web app.</summary>
    public double NoiseReduction { get; init; }

    /// <summary>0..100 unsharp mask.</summary>
    public double Sharpen { get; init; }

    /// <summary>Radius in pixels for unsharp masking. One preserves the original 3x3 kernel exactly.</summary>
    public int SharpenRadius { get; init; } = 1;

    /// <summary>0..100 Laplacian edge add, for fine structure that would otherwise dissolve into the
    /// dither.</summary>
    public double EdgeEnhance { get; init; }

    /// <summary>Tone-mapping precedence when multiple are set: dithering &gt; threshold &gt; continuous grayscale.</summary>
    public bool UseDithering { get; init; }
    public DitheringAlgorithm DitheringAlgorithm { get; init; } = DitheringAlgorithm.Stucki;
    public bool UseThreshold { get; init; }
    public byte ThresholdValue { get; init; } = 128;
}

/// <summary>Pure pixel data — no disk/GDI+ dependency. PowerFraction is 0..1 per pixel, row-major:
/// 0 = no burn, 1 = full power (before RasterPlanner maps it into the job's actual Min..Max power range).</summary>
public sealed record ProcessedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<double> PowerFraction { get; init; }

    public double At(int x, int y) => PowerFraction[y * Width + x];
}

public static class ImageProcessor
{
    public static ProcessedImage Process(GrayscaleImage source, ImageProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var width = source.Width;
        var height = source.Height;
        var tone = AdjustTone(source, options);

        // Spatial filters run after the per-pixel curve and in this order, matching the web app:
        // grain is smoothed before anything sharpens it, and edge enhancement lands last so it acts
        // on the already-sharpened image rather than competing with it.
        if (options.NoiseReduction > 0) tone = BoxBlurBlend(tone, width, height, options.NoiseReduction / 100.0);
        if (options.Sharpen > 0) tone = UnsharpMask(tone, width, height, options.Sharpen / 100.0 * 2.5,
            Math.Clamp(options.SharpenRadius, 1, 4));
        if (options.EdgeEnhance > 0) tone = LaplacianAdd(tone, width, height, options.EdgeEnhance / 100.0 * 1.2);

        // Dithering still wins over threshold, which is the one place this deliberately does not follow
        // the web app: there, threshold binarises inside the per-pixel loop and dithering then runs on
        // the result. Dithering an already-binary image reproduces it exactly, so the two orders agree
        // whenever the operator uses one or the other - which is the only way either is used, since
        // threshold is off by default in both.
        var power = options.UseDithering
            ? Dither(tone, width, height, options.DitheringAlgorithm)
            : ToneMapContinuous(tone, options);

        return new ProcessedImage { Width = width, Height = height, PowerFraction = power };
    }

    /// <summary>
    /// The per-pixel curve. Order matters and is the web app's: gamma, exposure, brightness, contrast,
    /// highlights, shadows, levels, clamp, invert. Contrast after brightness rather than before, for
    /// instance, is why the same two numbers produce the same picture in both places.
    /// </summary>
    private static double[] AdjustTone(GrayscaleImage source, ImageProcessingOptions options)
    {
        var tone = new double[source.Width * source.Height];
        var contrastFactor = ContrastFactor(options.Contrast);
        var gamma = double.IsFinite(options.Gamma) && options.Gamma > 0 ? options.Gamma : 1;
        var exposureFactor = options.Exposure == 0 ? 1 : Math.Pow(2, options.Exposure / 60.0);
        var blackPoint = Math.Clamp(options.BlackPoint, 0, 255);
        var whitePoint = Math.Clamp(options.WhitePoint, 0, 255);
        var remapsLevels = blackPoint > 0 || whitePoint < 255;
        var levelRange = Math.Max(1, whitePoint - blackPoint);

        for (var i = 0; i < tone.Length; i++)
        {
            double value = source.Luminance[i];

            if (gamma != 1) value = 255 * Math.Pow(value / 255.0, 1 / gamma);
            if (exposureFactor != 1) value *= exposureFactor;
            value += options.Brightness;
            value = contrastFactor * (value - 128) + 128;

            if (options.Highlights != 0 && value > 128)
                value *= 1 + options.Highlights / 100.0 * (value - 128) / 127.0;
            if (options.Shadows != 0 && value < 128)
                value += options.Shadows * (128 - value) / 128.0;

            if (remapsLevels) value = (value - blackPoint) / levelRange * 255;

            value = Math.Clamp(value, 0, 255);
            if (options.Invert) value = 255 - value;
            tone[i] = value;
        }

        return tone;
    }

    /// <summary>Classic photographic contrast-correction formula: contrast=0 -&gt; factor=1 (no change).</summary>
    private static double ContrastFactor(double contrast)
    {
        contrast = Math.Clamp(contrast, -255, 255);
        return (259.0 * (contrast + 255.0)) / (255.0 * (259.0 - contrast));
    }

    /// <summary>3x3 box blur blended in at <paramref name="strength"/>, 0..1.</summary>
    private static double[] BoxBlurBlend(double[] tone, int width, int height, double strength) =>
        Convolve(tone, width, height, (source, x, y) =>
        {
            var sum = 0.0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                sum += source[(y + dy) * width + (x + dx)];
            var centre = source[y * width + x];
            return centre * (1 - strength) + sum / 9.0 * strength;
        });

    /// <summary>Unsharp mask: [0,-s,0; -s,1+4s,-s; 0,-s,0].</summary>
    private static double[] UnsharpMask(double[] tone, int width, int height, double s, int radius)
    {
        if (radius == 1)
            return Convolve(tone, width, height, (source, x, y) =>
                source[(y - 1) * width + x] * -s +
                source[y * width + x - 1] * -s +
                source[y * width + x] * (1 + 4 * s) +
                source[y * width + x + 1] * -s +
                source[(y + 1) * width + x] * -s);

        return Convolve(tone, width, height, (source, x, y) =>
        {
            var sum = 0.0;
            var count = 0;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var sampleX = Math.Clamp(x + dx, 0, width - 1);
                var sampleY = Math.Clamp(y + dy, 0, height - 1);
                sum += source[sampleY * width + sampleX];
                count++;
            }
            var blur = sum / count;
            return source[y * width + x] + (source[y * width + x] - blur) * s;
        });
    }

    /// <summary>Laplacian edge add: the original plus its own second derivative.</summary>
    private static double[] LaplacianAdd(double[] tone, int width, int height, double k) =>
        Convolve(tone, width, height, (source, x, y) =>
        {
            var centre = source[y * width + x];
            var laplacian =
                -source[(y - 1) * width + x]
                - source[y * width + x - 1] + 4 * centre - source[y * width + x + 1]
                - source[(y + 1) * width + x];
            return centre + laplacian * k;
        });

    /// <summary>
    /// Runs a 3x3 kernel over the interior. The one-pixel border is copied through untouched, which is
    /// what the web app does too — a wrapped or clamped edge would put a visible frame around every
    /// engraved photo.
    /// </summary>
    private static double[] Convolve(double[] tone, int width, int height, Func<double[], int, int, double> kernel)
    {
        if (width < 3 || height < 3) return tone;

        var result = (double[])tone.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
                result[y * width + x] = Math.Clamp(kernel(tone, x, y), 0, 255);
        }
        return result;
    }

    private static double[] ToneMapContinuous(double[] tone, ImageProcessingOptions options)
    {
        var power = new double[tone.Length];
        for (var i = 0; i < tone.Length; i++)
        {
            power[i] = options.UseThreshold
                ? (tone[i] < options.ThresholdValue ? 1.0 : 0.0)
                : 1.0 - tone[i] / 255.0;
        }
        return power;
    }

    /// <summary>
    /// Error-diffusion weights as (dx, dy, weight). Written out rather than derived so each kernel can
    /// be read against its published form.
    /// </summary>
    private static (int Dx, int Dy, double Weight)[] KernelFor(DitheringAlgorithm algorithm) => algorithm switch
    {
        DitheringAlgorithm.Stucki =>
        [
            (1, 0, 8 / 42.0), (2, 0, 4 / 42.0),
            (-2, 1, 2 / 42.0), (-1, 1, 4 / 42.0), (0, 1, 8 / 42.0), (1, 1, 4 / 42.0), (2, 1, 2 / 42.0),
            (-2, 2, 1 / 42.0), (-1, 2, 2 / 42.0), (0, 2, 4 / 42.0), (1, 2, 2 / 42.0), (2, 2, 1 / 42.0),
        ],
        DitheringAlgorithm.Jarvis =>
        [
            (1, 0, 7 / 48.0), (2, 0, 5 / 48.0),
            (-2, 1, 3 / 48.0), (-1, 1, 5 / 48.0), (0, 1, 7 / 48.0), (1, 1, 5 / 48.0), (2, 1, 3 / 48.0),
            (-2, 2, 1 / 48.0), (-1, 2, 3 / 48.0), (0, 2, 5 / 48.0), (1, 2, 3 / 48.0), (2, 2, 1 / 48.0),
        ],
        DitheringAlgorithm.Sierra =>
        [
            (1, 0, 5 / 32.0), (2, 0, 3 / 32.0),
            (-2, 1, 2 / 32.0), (-1, 1, 4 / 32.0), (0, 1, 5 / 32.0), (1, 1, 4 / 32.0), (2, 1, 2 / 32.0),
            (-1, 2, 2 / 32.0), (0, 2, 3 / 32.0), (1, 2, 2 / 32.0),
        ],
        // Atkinson spreads only 6/8 of the error and drops the rest. That loss is the point: it is what
        // makes the result lighter and cleaner on flat graphics.
        DitheringAlgorithm.Atkinson =>
        [
            (1, 0, 1 / 8.0), (2, 0, 1 / 8.0),
            (-1, 1, 1 / 8.0), (0, 1, 1 / 8.0), (1, 1, 1 / 8.0),
            (0, 2, 1 / 8.0),
        ],
        _ =>
        [
            (1, 0, 7 / 16.0),
            (-1, 1, 3 / 16.0), (0, 1, 5 / 16.0), (1, 1, 1 / 16.0),
        ],
    };

    /// <summary>4x4 Bayer matrix, the standard ordered-dither threshold pattern.</summary>
    private static readonly int[,] BayerMatrix =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    private static double[] Dither(double[] tone, int width, int height, DitheringAlgorithm algorithm)
    {
        if (algorithm == DitheringAlgorithm.Ordered) return OrderedDither(tone, width, height);

        var kernel = KernelFor(algorithm);
        var darkness = new double[tone.Length];
        for (var i = 0; i < tone.Length; i++)
            darkness[i] = 1.0 - tone[i] / 255.0;

        var power = new double[tone.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var burn = darkness[index] >= 0.5;
                power[index] = burn ? 1.0 : 0.0;
                var error = darkness[index] - (burn ? 1.0 : 0.0);

                foreach (var (dx, dy, weight) in kernel)
                    Diffuse(darkness, width, height, x + dx, y + dy, error * weight);
            }
        }
        return power;
    }

    /// <summary>No error is carried, so every pixel is decided against the matrix alone.</summary>
    private static double[] OrderedDither(double[] tone, int width, int height)
    {
        var power = new double[tone.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var threshold = BayerMatrix[y % 4, x % 4] / 16.0 * 255.0;
                power[index] = tone[index] > threshold ? 0.0 : 1.0;
            }
        }
        return power;
    }

    private static void Diffuse(double[] darkness, int width, int height, int x, int y, double amount)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        darkness[y * width + x] += amount;
    }
}
