using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests.Perf;

/// <summary>
/// Realistic heavy documents for the performance harness, built without any UI input. Geometry is
/// deterministic (fixed seeds) so before/after runs compare the same scene.
/// </summary>
public static class PerfScenes
{
    public static readonly RgbColor CutColor = new(255, 0, 0);
    public static readonly RgbColor FillColor = new(52, 52, 52);
    public static readonly RgbColor PathColor = new(18, 18, 18);

    public const double BedMm = 400;

    public sealed record SceneShape(
        int Rectangles, int Ellipses, int Texts, int BezierPaths500, int BezierPaths2000, int BezierPaths5000,
        int Svg5k, int Svg10k, int Svg20k, int Polygons);

    public static readonly SceneShape Vector20 = new(5, 3, 4, 3, 1, 0, 2, 1, 1, 0);
    public static readonly SceneShape Vector200 = new(60, 30, 40, 28, 10, 2, 10, 6, 4, 10);

    /// <summary>Adds the described objects to <paramref name="vm"/> through the real AddObjectCommand.
    /// Attach the canvas afterwards so the scene is built once instead of once per object.</summary>
    public static IReadOnlyList<SceneObject> Populate(SceneViewModel vm, SceneShape shape, int seed = 7)
    {
        var random = new Random(seed);
        var added = new List<SceneObject>();
        var index = 0;

        Position RandomOrigin(double extent) =>
            new(random.NextDouble() * (BedMm - extent), random.NextDouble() * (BedMm - extent), 0);

        void Add(SceneObject obj, LayerSettings layer)
        {
            vm.Execute(new AddObjectCommand(vm.Scene, obj, [layer]));
            added.Add(obj);
            index++;
        }

        var cut = LayerSettings.CreateDefault(CutColor, LayerMode.Cut, "Rez");
        var fill = LayerSettings.CreateDefault(FillColor, LayerMode.Fill, "Text");
        var path = LayerSettings.CreateDefault(PathColor, LayerMode.Cut, "Vektor");

        for (var i = 0; i < shape.Rectangles; i++)
        {
            var o = RandomOrigin(40);
            Add(ScenePrimitiveFactory.CreateRectangle(o, new Position(o.X + 10 + random.Next(30), o.Y + 10 + random.Next(30), 0), CutColor, $"Obdélník {i}"), cut);
        }

        for (var i = 0; i < shape.Ellipses; i++)
        {
            var o = RandomOrigin(40);
            Add(ScenePrimitiveFactory.CreateEllipse(o, new Position(o.X + 10 + random.Next(30), o.Y + 10 + random.Next(30), 0), CutColor, $"Elipsa {i}"), cut);
        }

        for (var i = 0; i < shape.Polygons; i++)
        {
            var o = RandomOrigin(40);
            Add(ScenePrimitiveFactory.CreateStar(o, new Position(o.X + 30, o.Y + 30, 0), 5 + i % 4, 0.45, CutColor, $"Hvězda {i}"), cut);
        }

        for (var i = 0; i < shape.Texts; i++)
        {
            var obj = VectorTextFactory.Create($"Lasero {i} Gravír", RandomOrigin(80), 8 + i % 5 * 2, FillColor);
            Add(obj, fill);
        }

        for (var i = 0; i < shape.BezierPaths500; i++) Add(BezierObject(500, RandomOrigin(90), random, $"Křivka 500/{i}"), path);
        for (var i = 0; i < shape.BezierPaths2000; i++) Add(BezierObject(2000, RandomOrigin(90), random, $"Křivka 2000/{i}"), path);
        for (var i = 0; i < shape.BezierPaths5000; i++) Add(BezierObject(5000, RandomOrigin(90), random, $"Křivka 5000/{i}"), path);

        for (var i = 0; i < shape.Svg5k; i++) Add(SvgObject(5_000, RandomOrigin(80), random, $"SVG 5k/{i}"), cut);
        for (var i = 0; i < shape.Svg10k; i++) Add(SvgObject(10_000, RandomOrigin(80), random, $"SVG 10k/{i}"), cut);
        for (var i = 0; i < shape.Svg20k; i++) Add(SvgObject(20_000, RandomOrigin(80), random, $"SVG 20k/{i}"), cut);
        return added;
    }

    /// <summary>A wobbly closed smooth path with <paramref name="nodeCount"/> Bezier nodes.</summary>
    public static VectorPath BezierPath(int nodeCount, Position origin, Random random, double radiusMm = 40)
    {
        var nodes = new List<VectorNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            var angle = 2 * Math.PI * i / nodeCount;
            var wobble = 1 + 0.12 * Math.Sin(angle * 9) + 0.04 * Math.Sin(angle * 41 + 1) + 0.02 * random.NextDouble();
            var radius = radiusMm * wobble;
            var anchor = new Position(origin.X + radius * Math.Cos(angle), origin.Y + radius * Math.Sin(angle), 0);
            // Tangent handle length proportional to the arc length between nodes keeps the curve smooth.
            var handle = 2 * Math.PI * radius / nodeCount / 3;
            var tx = -Math.Sin(angle) * handle;
            var ty = Math.Cos(angle) * handle;
            nodes.Add(new VectorNode(
                anchor,
                new Position(anchor.X - tx, anchor.Y - ty, 0),
                new Position(anchor.X + tx, anchor.Y + ty, 0),
                VectorNodeType.Smooth));
        }

        return new VectorPath { Subpaths = [new VectorSubpath { Nodes = nodes, IsClosed = true }] };
    }

    public static SceneObject BezierObject(int nodeCount, Position origin, Random random, string name) =>
        VectorPathSceneFactory.Create(
            BezierPath(nodeCount, new Position(origin.X + 45, origin.Y + 45, 0), random), PathColor, name);

    /// <summary>An SVG whose single path has about <paramref name="segments"/> line and cubic segments.</summary>
    public static string BuildSvg(int segments, Random random)
    {
        var sb = new StringBuilder(segments * 40);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1000 1000\"><path fill=\"none\" stroke=\"red\" d=\"");
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        double Wobble(double angle) => 1 + 0.15 * Math.Sin(angle * 7) + 0.05 * Math.Sin(angle * 53) + 0.02 * random.NextDouble();
        Point2 At(int i)
        {
            var angle = 2 * Math.PI * i / segments;
            var radius = 400 * Wobble(angle);
            return new Point2(500 + radius * Math.Cos(angle), 500 + radius * Math.Sin(angle));
        }

        var start = At(0);
        sb.Append('M').Append(F(start.X)).Append(' ').Append(F(start.Y));
        for (var i = 1; i <= segments; i++)
        {
            var p = At(i % segments);
            if (i % 2 == 0)
            {
                var a = At((i - 1) % segments);
                var c1 = new Point2(a.X + (p.X - a.X) / 3, a.Y + (p.Y - a.Y) / 3 + 0.4);
                var c2 = new Point2(a.X + 2 * (p.X - a.X) / 3, a.Y + 2 * (p.Y - a.Y) / 3 - 0.4);
                sb.Append(" C").Append(F(c1.X)).Append(' ').Append(F(c1.Y)).Append(' ')
                  .Append(F(c2.X)).Append(' ').Append(F(c2.Y)).Append(' ').Append(F(p.X)).Append(' ').Append(F(p.Y));
            }
            else
            {
                sb.Append(" L").Append(F(p.X)).Append(' ').Append(F(p.Y));
            }
        }

        sb.Append(" Z\"/></svg>");
        return sb.ToString();
    }

    private readonly record struct Point2(double X, double Y);

    public static SceneObject SvgObject(int segments, Position origin, Random random, string name)
    {
        var document = SvgImporter.Import(BuildSvg(segments, random), 80, name + ".svg");
        var obj = SceneObjectFactory.FromImportedDocument(document, name);
        obj.Transform = obj.Transform with { X = origin.X, Y = origin.Y };
        return obj;
    }

    // ----------------------------------------------------------------------------------------------
    // Raster assets
    // ----------------------------------------------------------------------------------------------

    public enum RasterKind { GrayscalePng, GrayscaleJpg, OneBitPng, ColorJpg }

    /// <summary>Creates (once) a deterministic photo-like image of about <paramref name="megapixels"/> MP.</summary>
    public static string EnsureRasterAsset(RasterKind kind, double megapixels)
    {
        var width = (int)Math.Round(Math.Sqrt(megapixels * 1_000_000 * 4 / 3));
        var height = (int)Math.Round(width * 0.75);
        var extension = kind is RasterKind.GrayscaleJpg or RasterKind.ColorJpg ? "jpg" : "png";
        var path = Path.Combine(PerfEnvironment.AssetsDirectory, $"raster-{kind}-{megapixels:0.#}mp-{width}x{height}.{extension}");
        if (File.Exists(path)) return path;

        var temp = path + ".tmp";
        if (kind == RasterKind.OneBitPng)
            WriteOneBit(temp, width, height);
        else
            WriteContinuousTone(temp, width, height, color: kind == RasterKind.ColorJpg,
                format: extension == "jpg" ? ImageFormat.Jpeg : ImageFormat.Png);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static double Tone(int x, int y, int width, int height)
    {
        var u = x / (double)width;
        var v = y / (double)height;
        var value = 0.5 + 0.25 * Math.Sin(u * 18) * Math.Cos(v * 11) + 0.2 * Math.Sin((u + v) * 47)
            + 0.1 * Math.Sin(u * 211) * Math.Sin(v * 173);
        return Math.Clamp(value, 0, 1);
    }

    private static void WriteContinuousTone(string path, int width, int height, bool color, ImageFormat format)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            var noise = new Random(11);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var tone = Tone(x, y, width, height);
                    var gray = (byte)Math.Clamp(tone * 255 + noise.Next(-6, 7), 0, 255);
                    if (color)
                    {
                        row[x * 3] = (byte)(gray * 0.8);
                        row[x * 3 + 1] = gray;
                        row[x * 3 + 2] = (byte)Math.Min(255, gray * 1.1);
                    }
                    else
                    {
                        row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = gray;
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, format);
    }

    private static void WriteOneBit(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            // Ordered (Bayer 4x4) dither: representative of an already-dithered engraving source.
            int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                for (var x = 0; x < width; x++)
                {
                    var threshold = (bayer[y & 3, x & 3] + 0.5) / 16.0;
                    if (Tone(x, y, width, height) > threshold) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>A black-on-white logo-like bitmap (many blobs) that traces to thousands of nodes.</summary>
    public static string EnsureTraceAsset(int blobs = 900, int width = 3000, int height = 2000)
    {
        var path = Path.Combine(PerfEnvironment.AssetsDirectory, $"trace-{blobs}b-{width}x{height}.png");
        if (File.Exists(path)) return path;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var random = new Random(3);
            using var black = new SolidBrush(Color.Black);
            using var white = new SolidBrush(Color.White);
            for (var i = 0; i < blobs; i++)
            {
                var size = 20 + random.Next(120);
                var x = random.Next(width - size);
                var y = random.Next(height - size);
                if (i % 3 == 0) graphics.FillRectangle(black, x, y, size, size / 2 + 4);
                else graphics.FillEllipse(black, x, y, size, size);
                if (i % 5 == 0) graphics.FillEllipse(white, x + size / 4, y + size / 4, size / 2, size / 2);
            }
        }

        var temp = path + ".tmp";
        bitmap.Save(temp, ImageFormat.Png);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    // ----------------------------------------------------------------------------------------------
    // G-code
    // ----------------------------------------------------------------------------------------------

    /// <summary>A long G-code program (serpentine engraving with S words), like a raster or hatch job.</summary>
    public static List<string> BuildGCodeLines(int lineCount)
    {
        var lines = new List<string>(lineCount + 8) { "G90", "G21", "M5", "M4 S0" };
        var random = new Random(5);
        double x = 0, y = 0;
        var direction = 1;
        while (lines.Count < lineCount)
        {
            lines.Add($"G0 X{x.ToString("0.###", CultureInfo.InvariantCulture)} Y{y.ToString("0.###", CultureInfo.InvariantCulture)}");
            var runs = 8 + random.Next(24);
            for (var i = 0; i < runs && lines.Count < lineCount; i++)
            {
                x = Math.Clamp(x + direction * (0.3 + random.NextDouble() * 4), 0, BedMm);
                var power = random.Next(0, 1000);
                lines.Add($"G1 X{x.ToString("0.###", CultureInfo.InvariantCulture)} Y{y.ToString("0.###", CultureInfo.InvariantCulture)} S{power} F3000");
            }

            direction = -direction;
            y = (y + 0.1) % BedMm;
        }

        lines.Add("M5");
        return lines;
    }
}
