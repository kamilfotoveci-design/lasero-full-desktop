namespace Lasero.Core.Raster;

/// <summary>Pure pixel data — no disk/GDI+ dependency, so everything downstream (ImageProcessor,
/// RasterPlanner) is unit-testable with small hand-built grids instead of real image files.</summary>
public sealed record GrayscaleImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Row-major, one byte per pixel: 0 = black, 255 = white.</summary>
    public required IReadOnlyList<byte> Luminance { get; init; }

    public byte At(int x, int y) => Luminance[y * Width + x];
}
