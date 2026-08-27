using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Trace;

/// <summary>
/// Converts a thresholded bitmap into closed vector contours. The implementation is intentionally
/// local and deterministic so tracing remains available offline and produces the same result on
/// every LASERO client.
/// </summary>
public static class BitmapTracer
{
    private static readonly RgbColor TraceColor = new(18, 18, 18);

    [SupportedOSPlatform("windows")]
    public static BitmapTraceResult Trace(
        string filePath,
        BitmapTraceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        options ??= new BitmapTraceOptions();
        options.Validate();

        using var source = new Bitmap(filePath);
        if (source.Width < 1 || source.Height < 1)
            throw new InvalidDataException("Bitmap does not contain any pixels.");

        var foreground = ReadForeground(source, options.Threshold, options.Invert, cancellationToken);
        RemoveSmallComponents(foreground, options.MinimumFeaturePixels, cancellationToken);
        var contours = ExtractContours(foreground, cancellationToken);

        var scale = options.TargetWidthMm / source.Width;
        var heightMm = source.Height * scale;
        var layer = LayerSettings.CreateDefault(TraceColor, LayerMode.Cut, "Trasovaný vektor");
        var shapes = new List<ImportedShape>(contours.Count);

        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var simplified = SimplifyClosed(contour, options.SimplificationPixels);
            if (simplified.Count < 3 || Math.Abs(SignedArea(simplified)) < 0.5) continue;

            shapes.Add(new ImportedShape
            {
                LayerId = layer.Id,
                Points = simplified
                    .Select(point => new Position(point.X * scale, (source.Height - point.Y) * scale, 0))
                    .ToList(),
                IsClosed = true,
                LayerColor = TraceColor,
                PreferredMode = LayerMode.Cut,
            });
        }

        var document = new ImportedDocument
        {
            Shapes = shapes,
            Layers = [layer],
            // Keep the full image frame as local bounds. Copying the bitmap transform then preserves
            // its exact position and physical size, even when transparent margins surround the trace.
            BoundingBox = new BoundingBox2D(0, 0, options.TargetWidthMm, heightMm),
            SourceFileName = Path.GetFileName(filePath),
        };

        return new BitmapTraceResult
        {
            Document = document,
            PixelWidth = source.Width,
            PixelHeight = source.Height,
            ContourCount = shapes.Count,
            PointCount = shapes.Sum(shape => shape.Points.Count),
        };
    }

    [SupportedOSPlatform("windows")]
    private static bool[,] ReadForeground(Bitmap source, byte threshold, bool invert, CancellationToken cancellationToken)
    {
        using var pixels = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(pixels))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var result = new bool[source.Height, source.Width];
        var rect = new Rectangle(0, 0, pixels.Width, pixels.Height);
        var data = pixels.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = Math.Abs(data.Stride);
            var bytes = new byte[rowBytes * pixels.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < pixels.Height; y++)
            {
                if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = data.Stride >= 0 ? y * rowBytes : (pixels.Height - 1 - y) * rowBytes;
                for (var x = 0; x < pixels.Width; x++)
                {
                    var offset = row + x * 4;
                    var luma = (bytes[offset + 2] * 299 + bytes[offset + 1] * 587 + bytes[offset] * 114) / 1000;
                    result[y, x] = invert ? luma >= threshold : luma < threshold;
                }
            }
        }
        finally
        {
            pixels.UnlockBits(data);
        }

        return result;
    }

    private static void RemoveSmallComponents(bool[,] pixels, int minimumSize, CancellationToken cancellationToken)
    {
        if (minimumSize <= 1) return;

        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var visited = new bool[height, width];
        var queue = new Queue<IntPoint>();
        var component = new List<IntPoint>();

        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if (!pixels[y, x] || visited[y, x]) continue;
                queue.Clear();
                component.Clear();
                queue.Enqueue(new IntPoint(x, y));
                visited[y, x] = true;

                while (queue.Count > 0)
                {
                    var point = queue.Dequeue();
                    component.Add(point);
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = point.X + dx;
                        var ny = point.Y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height || visited[ny, nx] || !pixels[ny, nx]) continue;
                        visited[ny, nx] = true;
                        queue.Enqueue(new IntPoint(nx, ny));
                    }
                }

                if (component.Count >= minimumSize) continue;
                foreach (var point in component) pixels[point.Y, point.X] = false;
            }
        }
    }

    private static List<List<IntPoint>> ExtractContours(bool[,] pixels, CancellationToken cancellationToken)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var edges = new List<Edge>();

        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if (!pixels[y, x]) continue;
                if (y == 0 || !pixels[y - 1, x]) edges.Add(new Edge(new IntPoint(x, y), new IntPoint(x + 1, y)));
                if (x == width - 1 || !pixels[y, x + 1]) edges.Add(new Edge(new IntPoint(x + 1, y), new IntPoint(x + 1, y + 1)));
                if (y == height - 1 || !pixels[y + 1, x]) edges.Add(new Edge(new IntPoint(x + 1, y + 1), new IntPoint(x, y + 1)));
                if (x == 0 || !pixels[y, x - 1]) edges.Add(new Edge(new IntPoint(x, y + 1), new IntPoint(x, y)));
            }
        }

        var outgoing = new Dictionary<IntPoint, List<int>>();
        for (var i = 0; i < edges.Count; i++)
        {
            if (!outgoing.TryGetValue(edges[i].Start, out var list))
                outgoing[edges[i].Start] = list = [];
            list.Add(i);
        }

        var used = new bool[edges.Count];
        var contours = new List<List<IntPoint>>();
        for (var startIndex = 0; startIndex < edges.Count; startIndex++)
        {
            if (used[startIndex]) continue;
            if ((startIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();

            var first = edges[startIndex];
            var contour = new List<IntPoint> { first.Start };
            var currentIndex = startIndex;
            var closed = false;

            for (var guard = 0; guard <= edges.Count; guard++)
            {
                used[currentIndex] = true;
                var current = edges[currentIndex];
                var end = current.End;
                if (end == first.Start)
                {
                    closed = true;
                    break;
                }

                contour.Add(end);
                if (!outgoing.TryGetValue(end, out var candidates)) break;
                currentIndex = ChooseNextEdge(current, candidates, edges, used);
                if (currentIndex < 0) break;
            }

            if (!closed) continue;
            RemoveCollinear(contour);
            if (contour.Count >= 3) contours.Add(contour);
        }

        return contours;
    }

    private static int ChooseNextEdge(Edge current, IReadOnlyList<int> candidates, IReadOnlyList<Edge> edges, bool[] used)
    {
        var currentDirection = Direction(current);
        var bestIndex = -1;
        var bestPriority = int.MaxValue;
        foreach (var candidateIndex in candidates)
        {
            if (used[candidateIndex]) continue;
            var delta = (Direction(edges[candidateIndex]) - currentDirection + 4) % 4;
            var priority = delta switch { 1 => 0, 0 => 1, 3 => 2, _ => 3 };
            if (priority >= bestPriority) continue;
            bestPriority = priority;
            bestIndex = candidateIndex;
        }
        return bestIndex;
    }

    private static int Direction(Edge edge) => (edge.End.X - edge.Start.X, edge.End.Y - edge.Start.Y) switch
    {
        (1, 0) => 0,
        (0, 1) => 1,
        (-1, 0) => 2,
        (0, -1) => 3,
        _ => throw new InvalidOperationException("Contour edge is not axis aligned."),
    };

    private static void RemoveCollinear(List<IntPoint> points)
    {
        var changed = true;
        while (changed && points.Count >= 3)
        {
            changed = false;
            for (var i = points.Count - 1; i >= 0; i--)
            {
                var previous = points[(i - 1 + points.Count) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                if ((current.X - previous.X) * (next.Y - current.Y) !=
                    (current.Y - previous.Y) * (next.X - current.X)) continue;
                points.RemoveAt(i);
                changed = true;
            }
        }
    }

    private static List<IntPoint> SimplifyClosed(IReadOnlyList<IntPoint> points, double tolerance)
    {
        if (points.Count < 4 || tolerance <= 0) return points.ToList();
        var firstIndex = 0;
        var oppositeIndex = 1;
        var farthest = -1d;
        for (var i = 1; i < points.Count; i++)
        {
            var distance = SquaredDistance(points[firstIndex], points[i]);
            if (distance <= farthest) continue;
            farthest = distance;
            oppositeIndex = i;
        }

        var firstHalf = SliceRing(points, firstIndex, oppositeIndex);
        var secondHalf = SliceRing(points, oppositeIndex, firstIndex);
        var simplifiedFirst = SimplifyOpen(firstHalf, tolerance);
        var simplifiedSecond = SimplifyOpen(secondHalf, tolerance);
        var result = simplifiedFirst.Take(simplifiedFirst.Count - 1).ToList();
        result.AddRange(simplifiedSecond.Take(simplifiedSecond.Count - 1));
        RemoveCollinear(result);
        return result;
    }

    private static List<IntPoint> SliceRing(IReadOnlyList<IntPoint> points, int start, int end)
    {
        var result = new List<IntPoint>();
        var index = start;
        while (true)
        {
            result.Add(points[index]);
            if (index == end) break;
            index = (index + 1) % points.Count;
        }
        return result;
    }

    private static List<IntPoint> SimplifyOpen(IReadOnlyList<IntPoint> points, double tolerance)
    {
        if (points.Count <= 2) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, points.Count - 1));
        var toleranceSquared = tolerance * tolerance;

        while (ranges.Count > 0)
        {
            var (start, end) = ranges.Pop();
            var maxDistance = 0d;
            var maxIndex = -1;
            for (var i = start + 1; i < end; i++)
            {
                var distance = DistanceToSegmentSquared(points[i], points[start], points[end]);
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                maxIndex = i;
            }
            if (maxIndex < 0 || maxDistance <= toleranceSquared) continue;
            keep[maxIndex] = true;
            ranges.Push((start, maxIndex));
            ranges.Push((maxIndex, end));
        }

        return points.Where((_, index) => keep[index]).ToList();
    }

    private static double DistanceToSegmentSquared(IntPoint point, IntPoint start, IntPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (dx == 0 && dy == 0) return SquaredDistance(point, start);
        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (double)(dx * dx + dy * dy), 0, 1);
        var projectedX = start.X + t * dx;
        var projectedY = start.Y + t * dy;
        var px = point.X - projectedX;
        var py = point.Y - projectedY;
        return px * px + py * py;
    }

    private static double SquaredDistance(IntPoint first, IntPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    private static double SignedArea(IReadOnlyList<IntPoint> points)
    {
        var area = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            area += points[i].X * next.Y - next.X * points[i].Y;
        }
        return area / 2;
    }

    private readonly record struct IntPoint(int X, int Y);
    private readonly record struct Edge(IntPoint Start, IntPoint End);
}
