using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Lasero.App;

namespace Lasero.Tests;

/// <summary>The canvas preview of a raster is decoded off the UI thread at display resolution.</summary>
public sealed class RasterPreviewLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-preview-" + Guid.NewGuid().ToString("N"));

    public RasterPreviewLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        RasterPreviewLoader.Clear();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Make(int width, int height, string name = "p.png")
    {
        var path = Path.Combine(_directory, name);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 0, 0, width / 3, height / 2);
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public async Task LargeImageIsDecodedAtDisplayResolutionKeepingTheAspectRatio()
    {
        var path = Make(4000, 3000);

        var preview = await RasterPreviewLoader.GetAsync(path);

        Assert.NotNull(preview);
        Assert.True(preview!.IsFrozen);
        Assert.Equal(RasterPreviewLoader.MaxLongSidePixels, preview.PixelWidth);
        Assert.Equal(1536, preview.PixelHeight);
    }

    [Fact]
    public async Task PortraitImageIsLimitedOnItsLongSide()
    {
        var preview = await RasterPreviewLoader.GetAsync(Make(2500, 5000));

        Assert.NotNull(preview);
        Assert.Equal(RasterPreviewLoader.MaxLongSidePixels, preview!.PixelHeight);
        Assert.Equal(1024, preview.PixelWidth);
    }

    [Fact]
    public async Task SmallImageKeepsItsOwnSize()
    {
        var preview = await RasterPreviewLoader.GetAsync(Make(640, 480));

        Assert.Equal(640, preview!.PixelWidth);
        Assert.Equal(480, preview.PixelHeight);
    }

    [Fact]
    public async Task SecondRequestForTheSameFileIsServedFromTheCacheAndChangedFilesAreDecodedAgain()
    {
        var path = Make(3000, 2000);
        var first = await RasterPreviewLoader.GetAsync(path);
        var second = RasterPreviewLoader.GetAsync(path);

        Assert.True(second.IsCompletedSuccessfully);
        Assert.Same(first, second.Result);

        File.Delete(path);
        Make(1000, 500);
        File.Move(Path.Combine(_directory, "p.png"), path, overwrite: true);
        var third = await RasterPreviewLoader.GetAsync(path);
        Assert.NotSame(first, third);
        Assert.Equal(1000, third!.PixelWidth);
    }

    [Fact]
    public async Task MissingOrCorruptFilesGiveNullInsteadOfThrowing()
    {
        Assert.Null(await RasterPreviewLoader.GetAsync(Path.Combine(_directory, "missing.png")));

        var corrupt = Path.Combine(_directory, "corrupt.png");
        File.WriteAllText(corrupt, "not an image");
        Assert.Null(await RasterPreviewLoader.GetAsync(corrupt));
    }

    [Fact]
    public void RequestingAPreviewReturnsToTheCallerWithoutWaitingForTheDecode()
    {
        var path = Make(6000, 4500, "big.png");
        var watch = Stopwatch.StartNew();

        var pending = RasterPreviewLoader.GetAsync(path);

        // Generous: the point is that the call itself does no decoding (a 27 MP decode takes far longer).
        Assert.True(watch.ElapsedMilliseconds < 150, $"GetAsync blocked for {watch.ElapsedMilliseconds} ms");
        Assert.NotNull(pending.GetAwaiter().GetResult());
    }
}
