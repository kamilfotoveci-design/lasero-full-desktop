using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Import;
using Lasero.Core.Trace;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// Regression coverage for this session's Raster Import / Bitmap Trace / Background Removal parity
/// pass: EditorViewModel.ImportSvgFile/ImportRasterFile (real Lasero.Core.Import/Raster pipeline, no
/// fakes), the OBRÁZEK section's gating properties, and the trace/background-removal round trips.
/// Matches Lasero.Tests/SceneViewModelTests.cs's own ImportingBitmapCreatesAnEditableGrayscaleEngravingLayer
/// fixture pattern (a real 2x1 PNG via System.Drawing) so the two apps are checked the same way.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class EditorViewModelRasterTests
{
    private const string SampleSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
            <rect x="0" y="0" width="100" height="50" fill="none" stroke="#FF0000" />
            <rect x="10" y="10" width="20" height="20" fill="#000000" />
        </svg>
        """;

    private static EditorViewModel NewViewModel() =>
        new(new FakeLaserMachine(), new AppSettingsStore(Path.Combine(Path.GetTempPath(), $"lasero-test-{Guid.NewGuid():N}.json")));

    private static string CreateTestPng()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-avalonia-raster-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(8, 8);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 1, 1, 6, 6);
        }
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void ImportSvgFileAddsAnObjectAndSelectsIt()
    {
        TestApp.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), $"lasero-avalonia-svg-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, SampleSvg);
        try
        {
            var viewModel = NewViewModel();
            viewModel.ImportSvgFile(path, targetWidthMm: 100);

            var imported = Assert.Single(viewModel.Document.Objects);
            Assert.Same(imported, viewModel.SelectedObject);
            Assert.False(imported.IsRaster);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RepeatedImportsCascadeInsteadOfStacking()
    {
        TestApp.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), $"lasero-avalonia-svg-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, SampleSvg);
        try
        {
            var viewModel = NewViewModel();
            viewModel.ImportSvgFile(path, targetWidthMm: 100);
            viewModel.ImportSvgFile(path, targetWidthMm: 100);

            Assert.Equal(2, viewModel.Document.Objects.Count);
            Assert.NotEqual(viewModel.Document.Objects[0].Transform, viewModel.Document.Objects[1].Transform);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportRasterFileCreatesRasterLayerAndGatesTheObrazekSection()
    {
        TestApp.EnsureInitialized();
        var path = CreateTestPng();
        try
        {
            var viewModel = NewViewModel();
            var options = new RasterImportOptions { TargetWidthMm = 20, Dpi = 254, MaxPower = 100, FeedRatePerMinute = 1000 };

            viewModel.ImportRasterFile(path, options);

            var imported = Assert.Single(viewModel.Document.Objects);
            Assert.True(imported.IsRaster);
            Assert.Same(imported, viewModel.SelectedObject);
            Assert.True(viewModel.IsSelectedRaster);
            Assert.True(viewModel.CanTraceSelectedRaster);
            Assert.True(viewModel.CanRemoveSelectedBackground);
            Assert.False(viewModel.CanRestoreSelectedBackground);

            var layer = Assert.Single(viewModel.Document.Layers);
            Assert.True(layer.IsRaster);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReplaceRasterWithTraceSwapsInATracedVectorObjectAndSelectsIt()
    {
        TestApp.EnsureInitialized();
        var path = CreateTestPng();
        try
        {
            var viewModel = NewViewModel();
            viewModel.ImportRasterFile(path, new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 });
            var rasterObject = viewModel.Document.Objects[0];

            var traceOptions = new BitmapTraceOptions { TargetWidthMm = rasterObject.LocalBounds.Width };
            var traceResult = BitmapTracer.Trace(path, traceOptions);
            Assert.True(traceResult.ContourCount > 0); // a filled black square on white always traces to at least one contour

            viewModel.ReplaceRasterWithTrace(rasterObject, traceResult);

            var traced = Assert.Single(viewModel.Document.Objects);
            Assert.False(traced.IsRaster);
            Assert.Same(traced, viewModel.SelectedObject);
            Assert.False(viewModel.IsSelectedRaster);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommitAndRestoreBackgroundRemovalRoundTripsTheRasterFilePath()
    {
        TestApp.EnsureInitialized();
        var original = CreateTestPng();
        var removed = CreateTestPng();
        try
        {
            var viewModel = NewViewModel();
            viewModel.ImportRasterFile(original, new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 });
            var rasterObject = viewModel.Document.Objects[0];

            viewModel.CommitBackgroundRemoval(rasterObject, removed);

            var afterRemoval = Assert.Single(viewModel.Document.Objects);
            Assert.Equal(removed, afterRemoval.RasterFilePath);
            Assert.Equal(original, afterRemoval.OriginalRasterFilePath);
            Assert.True(afterRemoval.HasBackgroundRemoved);
            Assert.Same(afterRemoval, viewModel.SelectedObject);
            Assert.True(viewModel.CanRestoreSelectedBackground);
            Assert.False(viewModel.CanRemoveSelectedBackground);

            viewModel.RestoreSelectedBackground.Execute().Subscribe();

            var afterRestore = Assert.Single(viewModel.Document.Objects);
            Assert.Equal(original, afterRestore.RasterFilePath);
            Assert.False(afterRestore.HasBackgroundRemoved);
            Assert.True(viewModel.CanRemoveSelectedBackground);
        }
        finally
        {
            File.Delete(original);
            File.Delete(removed);
        }
    }
}
