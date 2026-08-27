using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using Lasero.App;
using Lasero.Core.Import;

namespace Lasero.Tests;

public sealed class RasterCanvasPreviewTests
{
    [Fact]
    public void ImportedColorBitmapIsRenderedOnCanvasAsProcessedGrayscale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-grayscale-preview-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(2, 1))
            {
                bitmap.SetPixel(0, 0, System.Drawing.Color.Red);
                bitmap.SetPixel(1, 0, System.Drawing.Color.Blue);
                bitmap.Save(path, ImageFormat.Png);
            }

            var preview = ProcessedImagePreviewRenderer.RenderFile(path, new RasterImportOptions
            {
                TargetWidthMm = 20,
            });

            Assert.Equal(PixelFormats.Gray8, preview.Format);
            var pixels = new byte[2];
            preview.CopyPixels(pixels, 2, 0);
            Assert.NotEqual(pixels[0], pixels[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
