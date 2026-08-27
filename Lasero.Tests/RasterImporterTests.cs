using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Lasero.Core.Import;

namespace Lasero.Tests;

public sealed class RasterImporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-raster-import-tests", Guid.NewGuid().ToString("N"));

    public RasterImporterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void PassesRepeatTheExactToolpathUsedByExecutionAndTimeEstimation()
    {
        var path = Path.Combine(_directory, "sample.png");
        using (var bitmap = new Bitmap(2, 2))
        {
            bitmap.SetPixel(0, 0, Color.Black);
            bitmap.SetPixel(1, 0, Color.White);
            bitmap.SetPixel(0, 1, Color.White);
            bitmap.SetPixel(1, 1, Color.Black);
            bitmap.Save(path, ImageFormat.Png);
        }

        var baseOptions = new RasterImportOptions
        {
            TargetWidthMm = 2,
            Dpi = 25.4,
            MaxPower = 20,
            FeedRatePerMinute = 1000,
            Passes = 1,
        };
        var onePass = RasterImporter.BuildGCode(path, baseOptions);
        var threePasses = RasterImporter.BuildGCode(path, baseOptions with { Passes = 3 });

        Assert.Equal(onePass.Count * 3, threePasses.Count);
        Assert.Equal(onePass, threePasses.Take(onePass.Count));
        Assert.Equal(onePass, threePasses.Skip(onePass.Count).Take(onePass.Count));
        Assert.Equal(onePass, threePasses.Skip(onePass.Count * 2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
