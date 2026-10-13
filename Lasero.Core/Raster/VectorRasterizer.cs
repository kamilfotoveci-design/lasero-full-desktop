using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;

namespace Lasero.Core.Raster;

/// <summary>What <see cref="VectorRasterizer.Render"/> produced: the PNG on disk and the exact world
/// rectangle (mm) its pixels cover.</summary>
public sealed record RasterizedVector(string FilePath, int WidthPx, int HeightPx, double Dpi, BoundingBox2D Frame);

/// <summary>
/// Draws vector shapes (already in world mm) into a black-on-white PNG, the first half of "Převést na
/// bitmapu". Closed shapes on a fill layer are filled (a compound outline with holes keeps its holes,
/// contours that share a GeometrySetId are one compound path); closed shapes on a line layer and open
/// paths are stroked one pixel (one scan line) wide, i.e. what the laser would burn. The picture is
/// white wherever nothing is drawn, so a transparent background cannot turn into a black block when it
/// is engraved. World Y points up and image rows point down, hence the flip below.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VectorRasterizer
{
    public const double DefaultDpi = 254;
    public const double MinDpi = 25.4;
    public const double MaxDpi = 1270;

    /// <summary>Upper bound on width x height in pixels (24-bit, so 50 MP is about 150 MB while drawing).
    /// A larger selection is rendered at a proportionally lower resolution instead of failing.</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>The resolution matching a layer's scan-line spacing, or <see cref="DefaultDpi"/> when
    /// the spacing is not a usable number.</summary>
    public static double DpiFromLineInterval(double lineIntervalMm) =>
        lineIntervalMm > 0 && double.IsFinite(lineIntervalMm)
            ? Math.Clamp(25.4 / lineIntervalMm, MinDpi, MaxDpi)
            : DefaultDpi;

    /// <summary>The picture grid for a frame: its size in mm (never thinner than one scan line), its size
    /// in pixels and the resolution actually used, which is lower than asked when the picture would
    /// otherwise exceed <paramref name="maxPixels"/>.</summary>
    public static (double WidthMm, double HeightMm, int WidthPx, int HeightPx, double Dpi) PlanGrid(
        double frameWidthMm, double frameHeightMm, double dpi, long maxPixels = MaxPixels)
    {
        dpi = double.IsFinite(dpi) && dpi > 0 ? Math.Clamp(dpi, MinDpi, MaxDpi) : DefaultDpi;
        var interval = 25.4 / dpi;
        var widthMm = Math.Max(frameWidthMm, interval);
        var heightMm = Math.Max(frameHeightMm, interval);

        var pixels = Math.Ceiling(widthMm / interval) * Math.Ceiling(heightMm / interval);
        if (pixels > maxPixels)
        {
            dpi = Math.Max(MinDpi, dpi * Math.Sqrt(maxPixels / pixels));
            interval = 25.4 / dpi;
        }

        return (
            widthMm, heightMm,
            Math.Max(1, (int)Math.Round(widthMm / interval)),
            Math.Max(1, (int)Math.Round(heightMm / interval)),
            dpi);
    }

    /// <summary>Renders <paramref name="worldShapes"/> into <paramref name="frame"/> (world mm) and saves
    /// a PNG at <paramref name="destinationPath"/>. A frame thinner than one scan line is widened to one
    /// so a straight line still has a pixel to live in; the returned <see cref="RasterizedVector.Frame"/>
    /// is the rectangle the picture really covers.</summary>
    public static RasterizedVector Render(
        IReadOnlyList<ImportedShape> worldShapes,
        BoundingBox2D frame,
        double dpi,
        Func<ImportedShape, bool> isFilled,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(worldShapes);
        ArgumentNullException.ThrowIfNull(isFilled);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (frame.IsEmpty) throw new ArgumentException("The frame to render is empty.", nameof(frame));

        var (widthMm, heightMm, widthPx, heightPx, usedDpi) = PlanGrid(frame.Width, frame.Height, dpi);
        dpi = usedDpi;

        var centreX = (frame.MinX + frame.MaxX) / 2;
        var centreY = (frame.MinY + frame.MaxY) / 2;
        var covered = new BoundingBox2D(
            centreX - widthMm / 2, centreY - heightMm / 2, centreX + widthMm / 2, centreY + heightMm / 2);
        var scaleX = widthPx / widthMm;
        var scaleY = heightPx / heightMm;

        PointF ToPixel(Position world) => new(
            (float)((world.X - covered.MinX) * scaleX),
            (float)((covered.MaxY - world.Y) * scaleY));

        using (var bitmap = new Bitmap(widthPx, heightPx, PixelFormat.Format24bppRgb))
        {
            bitmap.SetResolution((float)dpi, (float)dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using var pen = new Pen(Color.Black, 1f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

                // Filled contours of one compound path go into one even-odd path so holes stay holes.
                foreach (var set in worldShapes.Where(shape => shape.IsClosed && shape.Points.Count >= 3 && isFilled(shape))
                             .GroupBy(shape => shape.GeometrySetId))
                {
                    using var path = new GraphicsPath(FillMode.Alternate);
                    foreach (var shape in set)
                    {
                        path.StartFigure();
                        path.AddPolygon(shape.Points.Select(ToPixel).ToArray());
                    }
                    graphics.FillPath(Brushes.Black, path);
                }

                foreach (var shape in worldShapes)
                {
                    if (shape.Points.Count < 2) continue;
                    if (shape.IsClosed && shape.Points.Count >= 3 && isFilled(shape)) continue;
                    var points = shape.Points.Select(ToPixel).ToArray();
                    if (shape.IsClosed && points.Length >= 3) graphics.DrawPolygon(pen, points);
                    else graphics.DrawLines(pen, points);
                }
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
                ?? throw new InvalidOperationException("The destination path has no directory.");
            Directory.CreateDirectory(directory);

            // Written beside the target and moved into place, so a failed save never leaves a half-written
            // PNG that a project would later try to pack.
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                bitmap.Save(temporaryPath, ImageFormat.Png);
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        return new RasterizedVector(destinationPath, widthPx, heightPx, dpi, covered);
    }
}
