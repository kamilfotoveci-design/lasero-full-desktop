using System.IO;
using OpenCvSharp;

namespace Lasero.App;

/// <summary>Normalizes WebP to lossless PNG before existing WIC/GDI+ raster paths read it.
/// OpenCV's configured Windows runtime is already shipped with Lasero. Animated WebP decodes its
/// first frame, matching the static raster-object model.</summary>
internal static class RasterImportSource
{
    private const long MaximumFileBytes = 200L * 1024 * 1024;
    private const long MaximumPixelCount = 100_000_000;

    public static string NormalizeWebp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase)) return path;

        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Soubor WebP nebyl nalezen.", path);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException("Obrázek WebP překračuje limit 200 MB.");

        using var decoded = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new InvalidDataException("Obrázek WebP nelze dekódovat.");
        if ((long)decoded.Width * decoded.Height > MaximumPixelCount)
            throw new InvalidDataException("Obrázek WebP má příliš mnoho pixelů.");

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "RasterImports");
        Directory.CreateDirectory(directory);
        var normalized = Path.Combine(directory, $"webp-{Guid.NewGuid():N}.png");
        if (!Cv2.ImWrite(normalized, decoded)) throw new InvalidDataException("Obrázek WebP nelze převést do PNG.");
        return normalized;
    }
}
