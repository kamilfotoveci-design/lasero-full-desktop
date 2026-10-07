using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Serilog;

namespace Lasero.App;

/// <summary>
/// Decodes a raster object's canvas preview off the UI thread, at display resolution, and keeps the
/// result.
///
/// The canvas used to decode every raster at full size, synchronously, on the UI thread, once per
/// import and again for every image on every full rebuild: a 12-megapixel PNG took a visible fraction
/// of a second of frozen window and 48 MB of memory each. The preview is for judging placement, so it
/// is decoded no larger than <see cref="MaxLongSidePixels"/>; engraving output is produced from the
/// source file by the raster pipeline and never reads this bitmap.
/// </summary>
public static class RasterPreviewLoader
{
    /// <summary>Largest side of a preview bitmap in pixels (about 12 MB per image).</summary>
    public const int MaxLongSidePixels = 2048;

    private const int CacheCapacity = 24;

    private static readonly ConcurrentDictionary<(string Path, long Ticks, long Length), Task<BitmapSource?>> Cache = new();
    private static readonly ConcurrentQueue<(string Path, long Ticks, long Length)> Order = new();

    /// <summary>The preview for <paramref name="filePath"/>, decoding it on a worker thread the first
    /// time. Null when the file is missing or cannot be decoded. The returned bitmap is frozen.</summary>
    public static Task<BitmapSource?> GetAsync(string filePath)
    {
        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        if (!info.Exists) return Task.FromResult<BitmapSource?>(null);
        var key = (info.FullName, info.LastWriteTimeUtc.Ticks, info.Length);
        if (Cache.TryGetValue(key, out var existing)) return existing;

        var created = Task.Run(() => Decode(info.FullName));
        if (!Cache.TryAdd(key, created)) return Cache[key];

        Order.Enqueue(key);
        while (Cache.Count > CacheCapacity && Order.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
        return created;
    }

    /// <summary>Synchronous decode used by the cache; public for tests and for callers that are already off the UI thread.</summary>
    public static BitmapSource? Decode(string filePath)
    {
        try
        {
            int sourceWidth;
            int sourceHeight;
            using (var probe = File.OpenRead(filePath))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                sourceWidth = frame.PixelWidth;
                sourceHeight = frame.PixelHeight;
            }

            using var stream = File.OpenRead(filePath);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (Math.Max(sourceWidth, sourceHeight) > MaxLongSidePixels)
            {
                // Decode straight to the preview size; setting one dimension keeps the aspect ratio.
                if (sourceWidth >= sourceHeight) bitmap.DecodePixelWidth = MaxLongSidePixels;
                else bitmap.DecodePixelHeight = MaxLongSidePixels;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            // Missing or corrupt source file: the canvas keeps the outline-only rendering. Logged because
            // a silently blank raster looks identical to one that has not been drawn yet.
            Log.Warning(ex, "Could not build the canvas preview for raster {Path}", filePath);
            return null;
        }
    }

    /// <summary>Forgets every cached preview (tests, and after the source folder is known to have changed).</summary>
    public static void Clear()
    {
        Cache.Clear();
        while (Order.TryDequeue(out _)) { }
    }
}
