using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// The single entry point the app talks to for "Odstranit pozadí": makes sure the model is cached
/// locally (downloading it once if needed), runs segmentation on a source bitmap, and writes a new
/// file with the background made transparent. Never touches the source file — SceneObject/ProjectFile
/// track "original vs. background-removed" by swapping which file RasterFilePath points at (see
/// SceneObject.OriginalRasterFilePath), not by keeping two bitmaps in memory, so this service's whole
/// job ends at "write a new PNG"; undo/redo and persistence are the caller's concern.
///
/// Inference itself is isolated behind IBackgroundRemovalInferenceEngine (see that interface and
/// OnnxBackgroundRemovalInferenceEngine) so this class's file-caching and pixel-compositing logic is
/// unit-testable with a fake engine, without a real ~170MB model file or a live ONNX run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackgroundRemovalService
{
    private readonly BackgroundRemovalModelStore _modelStore;
    private readonly Func<string, IBackgroundRemovalInferenceEngine> _engineFactory;

    public BackgroundRemovalService(
        BackgroundRemovalModelStore? modelStore = null,
        Func<string, IBackgroundRemovalInferenceEngine>? engineFactory = null)
    {
        _modelStore = modelStore ?? BackgroundRemovalModelStore.CreateDefault();
        _engineFactory = engineFactory ?? (path => new OnnxBackgroundRemovalInferenceEngine(path));
    }

    public bool IsModelAvailable => _modelStore.IsModelAvailable;

    public Task DownloadModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        _modelStore.DownloadModelAsync(progress, cancellationToken);

    /// <summary>Where RemoveBackgroundAsync will write its result for a given source file — a stable,
    /// content-addressed path under the same app-local root as the model cache (sibling "background-removal"
    /// directory), so re-running removal on the same source overwrites its own prior output instead of
    /// accumulating files, and the output survives without depending on the source file's own folder
    /// being writable (it may be a read-only location the user imported from).</summary>
    public string GetDestinationPath(string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        var fullPath = Path.GetFullPath(sourceFilePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..20];
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lasero", "background-removal", hash);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, $"{name}.nobg.png");
    }

    /// <summary>
    /// Runs background removal on sourceFilePath and writes an RGBA PNG with real alpha transparency
    /// to GetDestinationPath(sourceFilePath) (or destinationFilePath if given). Does the decode,
    /// inference and re-encode entirely off the calling thread via Task.Run, so a caller invoked
    /// directly from a UI event handler never blocks the WPF dispatcher.
    /// </summary>
    public async Task<string> RemoveBackgroundAsync(
        string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        if (!_modelStore.IsModelAvailable)
            throw new InvalidOperationException("Model pro odstranění pozadí ještě nebyl stažen.");

        var destination = destinationFilePath ?? GetDestinationPath(sourceFilePath);

        await Task.Run(() =>
        {
            using var source = new Bitmap(sourceFilePath);
            var width = source.Width;
            var height = source.Height;

            var bgra = ReadTightBgra(source);
            cancellationToken.ThrowIfCancellationRequested();

            var rgb = new byte[width * height * 3];
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var bgraOffset = pixel * 4;
                var rgbOffset = pixel * 3;
                rgb[rgbOffset + 0] = bgra[bgraOffset + 2]; // R
                rgb[rgbOffset + 1] = bgra[bgraOffset + 1]; // G
                rgb[rgbOffset + 2] = bgra[bgraOffset + 0]; // B
            }

            cancellationToken.ThrowIfCancellationRequested();
            float[] mask;
            using (var engine = _engineFactory(_modelStore.ModelPath))
                mask = engine.Segment(rgb, width, height);

            cancellationToken.ThrowIfCancellationRequested();
            var composited = BackgroundMaskCompositor.ApplyMask(bgra, width, height, mask);

            var directory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Neplatná cílová cesta pro odstranění pozadí.");
            Directory.CreateDirectory(directory);

            using var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            WriteTightBgra(output, composited);

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                output.Save(temporaryPath, ImageFormat.Png);
                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }, cancellationToken).ConfigureAwait(false);

        return destination;
    }

    /// <summary>Reads a bitmap's pixels into a tightly-packed (no stride padding) 32bppArgb byte
    /// array, matching BitmapLoader's row-by-row LockBits convention elsewhere in Lasero.Core.Raster.</summary>
    private static byte[] ReadTightBgra(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;

        using var argb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb))
        {
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }

        var result = new byte[width * height * 4];
        var data = argb.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var rowBytes = width * 4;
            var row = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                Array.Copy(row, 0, result, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            argb.UnlockBits(data);
        }

        return result;
    }

    private static void WriteTightBgra(Bitmap destination, byte[] tightBgra)
    {
        var width = destination.Width;
        var height = destination.Height;
        var data = destination.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(tightBgra, y * rowBytes, data.Scan0 + y * stride, rowBytes);
        }
        finally
        {
            destination.UnlockBits(data);
        }
    }
}
