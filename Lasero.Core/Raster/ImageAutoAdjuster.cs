namespace Lasero.Core.Raster;

/// <summary>Locally-derived photographic settings. No network service is required: the histogram
/// is analysed on-device so importing remains immediate and available offline.</summary>
public sealed record ImageAutoAdjustment
{
    public double Gamma { get; init; } = 1;
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double BlackPoint { get; init; }
    public double WhitePoint { get; init; } = 255;
    public double NoiseReduction { get; init; }
    public double Sharpen { get; init; } = 30;
}

public static class ImageAutoAdjuster
{
    public static ImageAutoAdjustment Recommend(GrayscaleImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Luminance.Count == 0) return new ImageAutoAdjustment { Brightness = 5, Contrast = 20 };

        Span<int> histogram = stackalloc int[256];
        long sum = 0;
        long sumSquares = 0;
        foreach (var value in image.Luminance)
        {
            histogram[value]++;
            sum += value;
            sumSquares += value * value;
        }

        var count = image.Luminance.Count;
        var mean = sum / (double)count;
        var variance = Math.Max(0, sumSquares / (double)count - mean * mean);
        var deviation = Math.Sqrt(variance);
        var low = Percentile(histogram, count, 0.01);
        var high = Percentile(histogram, count, 0.99);

        // Preserve a little headroom around the useful histogram and avoid stretching a nearly-flat
        // image into harsh black/white noise. Low-contrast photos receive more contrast; already
        // punchy artwork is left closer to neutral.
        var usableRange = high - low;
        var blackPoint = usableRange >= 48 ? Math.Clamp(low, 0, 48) : 0;
        var whitePoint = usableRange >= 48 ? Math.Clamp(high, 207, 255) : 255;
        var contrast = deviation switch
        {
            < 28 => 32,
            < 42 => 24,
            < 60 => 16,
            _ => 8,
        };
        var brightness = Math.Clamp(132 - mean, -18, 18);
        var gamma = mean switch
        {
            < 90 => 1.18,
            < 115 => 1.08,
            > 180 => 0.88,
            > 155 => 0.95,
            _ => 1,
        };

        return new ImageAutoAdjustment
        {
            Gamma = gamma,
            Brightness = Math.Round(brightness),
            Contrast = contrast,
            Highlights = high >= 250 ? -8 : 0,
            Shadows = low <= 5 ? 5 : 0,
            BlackPoint = blackPoint,
            WhitePoint = whitePoint,
            NoiseReduction = deviation < 20 ? 6 : 0,
            Sharpen = deviation < 35 ? 36 : 30,
        };
    }

    private static int Percentile(ReadOnlySpan<int> histogram, int count, double percentile)
    {
        var target = Math.Max(1, (int)Math.Ceiling(count * percentile));
        var cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target) return value;
        }
        return 255;
    }
}
