using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Raster;

namespace Lasero.Tests;

public sealed class RasterImporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-raster-import-tests", Guid.NewGuid().ToString("N"));

    public RasterImporterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DefaultBitmapSettingsUseBlackAndWhiteStuckiDithering()
    {
        var options = new RasterImportOptions();

        Assert.True(options.UseDithering);
        Assert.Equal(DitheringAlgorithm.Stucki, options.DitheringAlgorithm);
    }

    [Fact]
    public void ImportFacadeCarriesEveryImageAdjustmentIntoTheProcessor()
    {
        var path = CreateGradientBitmap();
        var options = new RasterImportOptions
        {
            Gamma = 1.35,
            Exposure = 12,
            Brightness = 4,
            Contrast = 11,
            Highlights = -17,
            Shadows = 13,
            BlackPoint = 9,
            WhitePoint = 241,
            NoiseReduction = 18,
            Sharpen = 23,
            EdgeEnhance = 14,
            UseDithering = false,
            UseThreshold = false,
        };

        var actual = RasterImporter.LoadProcessedPreview(path, options);
        var source = BitmapLoader.LoadGrayscale(path);
        var expected = ImageProcessor.Process(source, new ImageProcessingOptions
        {
            Gamma = options.Gamma,
            Exposure = options.Exposure,
            Brightness = options.Brightness,
            Contrast = options.Contrast,
            Highlights = options.Highlights,
            Shadows = options.Shadows,
            BlackPoint = options.BlackPoint,
            WhitePoint = options.WhitePoint,
            NoiseReduction = options.NoiseReduction,
            Sharpen = options.Sharpen,
            EdgeEnhance = options.EdgeEnhance,
        });

        Assert.Equal(expected.PowerFraction, actual.PowerFraction);
    }

    [Fact]
    public void ImportDownsamplesToTheResolutionTheLaserCanReproduce()
    {
        var path = Path.Combine(_directory, "large-for-output.png");
        using (var bitmap = new Bitmap(40, 20))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Gray);
            bitmap.Save(path, ImageFormat.Png);
        }

        var preview = RasterImporter.LoadProcessedPreview(path, new RasterImportOptions
        {
            TargetWidthMm = 1,
            Dpi = 254,
            UseDithering = false,
        });

        Assert.Equal(10, preview.Width);
        Assert.Equal(5, preview.Height);
    }

    [Fact]
    public void EveryLoadedImageStartsWithItsAutomaticRecommendationAndStucki()
    {
        var path = CreateGradientBitmap();
        using var machine = new GrblConnection();
        using var viewModel = new RasterImportViewModel(machine,
            new AppSettingsStore(Path.Combine(_directory, "settings.json")), path,
            targetWidthMm: 100, feedRatePerMinute: 3000, maxPower: 100, dpi: 254);

        var options = viewModel.BuildOptions();

        var recommendation = ImageAutoAdjuster.Recommend(BitmapLoader.LoadGrayscale(path));
        Assert.Equal(recommendation.Gamma, options.Gamma);
        Assert.Equal(recommendation.Brightness, options.Brightness);
        Assert.Equal(recommendation.Contrast, options.Contrast);
        Assert.Equal(recommendation.BlackPoint, options.BlackPoint);
        Assert.Equal(recommendation.WhitePoint, options.WhitePoint);
        Assert.Equal(recommendation.Sharpen, options.Sharpen);
        Assert.True(options.UseDithering);
        Assert.False(options.UseThreshold);
        Assert.Equal(DitheringAlgorithm.Stucki, options.DitheringAlgorithm);
    }

    [Fact]
    public void AutomaticRecommendationAdaptsToDarkAndBrightPhotos()
    {
        var dark = new GrayscaleImage { Width = 2, Height = 2, Luminance = [30, 40, 50, 60] };
        var bright = new GrayscaleImage { Width = 2, Height = 2, Luminance = [190, 205, 220, 235] };

        var darkRecommendation = ImageAutoAdjuster.Recommend(dark);
        var brightRecommendation = ImageAutoAdjuster.Recommend(bright);

        Assert.True(darkRecommendation.Gamma > 1);
        Assert.True(darkRecommendation.Brightness > 0);
        Assert.True(brightRecommendation.Gamma < 1);
        Assert.True(brightRecommendation.Brightness < 0);
    }

    [Fact]
    public void NoDitherChoiceUsesBinaryThresholding()
    {
        var path = CreateGradientBitmap();
        using var machine = new GrblConnection();
        using var viewModel = new RasterImportViewModel(machine,
            new AppSettingsStore(Path.Combine(_directory, "settings.json")), path,
            targetWidthMm: 100, feedRatePerMinute: 3000, maxPower: 100, dpi: 254)
        {
            SelectedDithering = RasterImportViewModel.DitheringChoices.Single(choice => choice.Algorithm is null),
        };

        var options = viewModel.BuildOptions();

        Assert.False(options.UseDithering);
        Assert.True(options.UseThreshold);
        Assert.Equal(128, options.ThresholdValue);
    }

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

    private string CreateGradientBitmap()
    {
        var path = Path.Combine(_directory, $"gradient-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(5, 5);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var luminance = 20 + x * 35 + y * 10;
            bitmap.SetPixel(x, y, Color.FromArgb(luminance, luminance, luminance));
        }
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
