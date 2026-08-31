using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Lasero.Core.Import;

namespace Lasero.Tests;

/// <summary>
/// The canvas draws an imported bitmap by asking <see cref="RasterImporter.LoadProcessedPreview"/>
/// for the same processed pixels the engraver will burn. If that step flattens the image, the
/// operator sees a featureless block where their artwork should be and has no way to tell whether
/// the import worked.
/// </summary>
public sealed class RasterPreviewFidelityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lasero-raster-" + Guid.NewGuid().ToString("N")[..8]);

    public RasterPreviewFidelityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string WriteGradient(int width, int height, bool withAlpha)
    {
        var path = Path.Combine(_directory, $"gradient-{width}x{height}-{withAlpha}.png");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var level = (byte)(x * 255 / Math.Max(1, width - 1));
            bitmap.SetPixel(x, y, withAlpha
                ? Color.FromArgb(255, level, level, level)
                : Color.FromArgb(255, level, level, level));
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void PreviewKeepsTheSourceResolution()
    {
        var path = WriteGradient(64, 48, withAlpha: false);

        var preview = RasterImporter.LoadProcessedPreview(path, new RasterImportOptions());

        Assert.Equal(64, preview.Width);
        Assert.Equal(48, preview.Height);
    }

    /// <summary>
    /// Dithering is on by default and a dithered result is legitimately two-level — that is what a
    /// one-bit diode laser can actually produce. It is the wrong thing to draw on the canvas though:
    /// at scene zoom the dither pattern averages into a featureless block, which is what the
    /// operator was seeing instead of their photo. The canvas asks for the same processing with
    /// dithering off, and that path has to preserve tone.
    /// </summary>
    [Fact]
    public void DitheredOutputIsTwoLevelBecauseTheLaserIsOneBit()
    {
        var path = WriteGradient(64, 48, withAlpha: false);

        var dithered = RasterImporter.LoadProcessedPreview(path, new RasterImportOptions { UseDithering = true });

        var distinct = dithered.PowerFraction.Select(value => Math.Round(value, 2)).Distinct().Count();
        Assert.Equal(2, distinct);
    }

    [Fact]
    public void ContinuousToneCanvasPreviewOfAGradientIsNotAFlatBlock()
    {
        var path = WriteGradient(64, 48, withAlpha: false);

        var preview = RasterImporter.LoadProcessedPreview(
            path, new RasterImportOptions { UseDithering = false, UseThreshold = false });

        var distinct = preview.PowerFraction.Select(value => Math.Round(value, 2)).Distinct().Count();
        Assert.True(distinct > 16,
            $"A left-to-right gradient collapsed to {distinct} distinct power levels — the canvas would draw it as a solid block.");
    }

    /// <summary>
    /// A PNG with an alpha channel is the common case for imported logos and traced artwork. The
    /// loader composites onto an uninitialised 32bpp surface, so fully transparent pixels must not be
    /// read as black — otherwise every logo imports as a filled rectangle.
    /// </summary>
    [Fact]
    public void FullyTransparentPixelsDoNotBecomeSolidInk()
    {
        var path = Path.Combine(_directory, "transparent.png");
        using (var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
            bitmap.Save(path, ImageFormat.Png);
        }

        var preview = RasterImporter.LoadProcessedPreview(path, new RasterImportOptions());

        var burnt = preview.PowerFraction.Count(value => value > 0.5);
        Assert.True(burnt == 0,
            $"{burnt} of {preview.PowerFraction.Count} fully transparent pixels are scheduled to be burnt at over half power.");
    }
}
