using System.Drawing;
using System.IO;
using Lasero.Core.Trace;

namespace Lasero.Tests;

public sealed class BitmapTracerTests
{
    [Fact]
    public void Trace_FilledRectangle_CreatesClosedSimplifiedContourAtRequestedSize()
    {
        var path = CreateBitmap(80, 60, graphics => graphics.FillRectangle(Brushes.Black, 10, 12, 40, 28));
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 80,
                Threshold = 128,
                MinimumFeaturePixels = 1,
                SimplificationPixels = 0.5,
            });

            Assert.Single(result.Document.Shapes);
            Assert.True(result.Document.Shapes[0].IsClosed);
            Assert.Equal(4, result.Document.Shapes[0].Points.Count);
            Assert.Equal(80, result.WidthMm, 6);
            Assert.Equal(60, result.HeightMm, 6);
            Assert.Equal(1, result.ContourCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trace_RemovesIsolatedNoiseBelowMinimumFeatureSize()
    {
        var path = CreateBitmap(32, 32, graphics =>
        {
            graphics.FillRectangle(Brushes.Black, 3, 3, 1, 1);
            graphics.FillRectangle(Brushes.Black, 10, 10, 8, 8);
        });
        try
        {
            var result = BitmapTracer.Trace(path, new BitmapTraceOptions
            {
                TargetWidthMm = 32,
                Threshold = 128,
                MinimumFeaturePixels = 4,
                SimplificationPixels = 0.5,
            });

            Assert.Single(result.Document.Shapes);
            Assert.Equal(1, result.ContourCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateBitmap(int width, int height, Action<Graphics> draw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-trace-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        draw(graphics);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }
}
