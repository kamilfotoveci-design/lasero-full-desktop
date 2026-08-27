using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.Core.GCode;
using Lasero.Core.Scene;

namespace Lasero.App.Thumbnails;

/// <summary>Rasterizes a SceneDocument's real geometry into a small cached PNG for the Home dashboard's
/// recent-projects cards — reuses the same world-space shape math SceneCanvas uses for live editing
/// (SceneObject.GetWorldShapes/WorldBounds), just drawn once off-screen instead of interactively.</summary>
public static class SceneThumbnailRenderer
{
    private const double MarginPx = 16;

    public static void RenderToFile(SceneDocument document, string path, int size = 240)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var bitmap = Render(document, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var temporaryPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temporaryPath))
                encoder.Save(stream);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static RenderTargetBitmap Render(SceneDocument document, int size = 240)
    {
        var visibleObjects = document.Objects.Where(o => o.IsVisible).ToList();
        var bounds = ComputeWorldBounds(visibleObjects);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));

            if (visibleObjects.Count > 0 && bounds.Width > 0 && bounds.Height > 0)
            {
                var scale = Math.Min((size - MarginPx * 2) / bounds.Width, (size - MarginPx * 2) / bounds.Height);
                var offsetX = (size - bounds.Width * scale) / 2;
                var offsetY = (size - bounds.Height * scale) / 2;

                double ToCanvasX(double worldX) => offsetX + (worldX - bounds.MinX) * scale;
                // World Y grows up (mm coordinates); canvas Y grows down.
                double ToCanvasY(double worldY) => offsetY + (bounds.MaxY - worldY) * scale;

                foreach (var obj in visibleObjects)
                {
                    foreach (var shape in obj.GetWorldShapes())
                        DrawShape(dc, shape, ToCanvasX, ToCanvasY);
                }
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawShape(DrawingContext dc, Lasero.Core.Import.ImportedShape shape, Func<double, double> toCanvasX, Func<double, double> toCanvasY)
    {
        if (shape.Points.Count < 2) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(shape.LayerColor.R, shape.LayerColor.G, shape.LayerColor.B)), 1.4);
        pen.Freeze();

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            var first = shape.Points[0];
            gc.BeginFigure(new Point(toCanvasX(first.X), toCanvasY(first.Y)), isFilled: false, isClosed: shape.IsClosed);
            gc.PolyLineTo(shape.Points.Skip(1).Select(p => new Point(toCanvasX(p.X), toCanvasY(p.Y))).ToList(), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static BoundingBox2D ComputeWorldBounds(IReadOnlyList<SceneObject> objects)
    {
        var box = BoundingBox2D.Empty;
        foreach (var obj in objects)
        {
            var world = obj.WorldBounds();
            if (world.IsEmpty) continue;
            box = box.Include(world.MinX, world.MinY);
            box = box.Include(world.MaxX, world.MaxY);
        }
        return box;
    }
}
