namespace Lasero.Core.Raster;

public sealed record ImageProcessingOptions
{
    /// <summary>Additive, -255..255. 0 = no change.</summary>
    public double Brightness { get; init; }

    /// <summary>-255..255. 0 = no change, matching the standard photographic contrast-correction formula.</summary>
    public double Contrast { get; init; }

    public bool Invert { get; init; }

    /// <summary>Tone-mapping precedence when multiple are set: dithering &gt; threshold &gt; continuous grayscale.</summary>
    public bool UseDithering { get; init; }
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
        var width = source.Width;
        var height = source.Height;
        var tone = new double[width * height]; // adjusted 0..255 luminance, pre tone-mapping
        var contrastFactor = ContrastFactor(options.Contrast);

        for (int i = 0; i < tone.Length; i++)
        {
            var value = source.Luminance[i] + options.Brightness;
            value = contrastFactor * (value - 128) + 128;
            value = Math.Clamp(value, 0, 255);
            if (options.Invert) value = 255 - value;
            tone[i] = value;
        }

        var power = options.UseDithering
            ? Dither(tone, width, height)
            : ToneMapContinuous(tone, options);

        return new ProcessedImage { Width = width, Height = height, PowerFraction = power };
    }

    /// <summary>Classic photographic contrast-correction formula: contrast=0 -&gt; factor=1 (no change).</summary>
    private static double ContrastFactor(double contrast)
    {
        contrast = Math.Clamp(contrast, -255, 255);
        return (259.0 * (contrast + 255.0)) / (255.0 * (259.0 - contrast));
    }

    private static double[] ToneMapContinuous(double[] tone, ImageProcessingOptions options)
    {
        var power = new double[tone.Length];
        for (int i = 0; i < tone.Length; i++)
        {
            power[i] = options.UseThreshold
                ? (tone[i] < options.ThresholdValue ? 1.0 : 0.0)
                : 1.0 - tone[i] / 255.0;
        }
        return power;
    }

    /// <summary>Floyd-Steinberg error diffusion, binarizing darkness (1 - luminance/255) against a 0.5
    /// threshold. Deterministic — no RNG — so it's a stable, testable transform for a given input.</summary>
    private static double[] Dither(double[] tone, int width, int height)
    {
        var darkness = new double[tone.Length];
        for (int i = 0; i < tone.Length; i++)
            darkness[i] = 1.0 - tone[i] / 255.0;

        var power = new double[tone.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var old = darkness[idx];
                var burn = old >= 0.5;
                power[idx] = burn ? 1.0 : 0.0;
                var error = old - (burn ? 1.0 : 0.0);

                Diffuse(darkness, width, height, x + 1, y, error * 7.0 / 16.0);
                Diffuse(darkness, width, height, x - 1, y + 1, error * 3.0 / 16.0);
                Diffuse(darkness, width, height, x, y + 1, error * 5.0 / 16.0);
                Diffuse(darkness, width, height, x + 1, y + 1, error * 1.0 / 16.0);
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
