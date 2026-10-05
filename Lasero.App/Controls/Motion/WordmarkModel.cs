using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>A closed contour with cumulative arc length, so the "laser" can be at any distance along it.</summary>
internal sealed class TracedLoop
{
    private readonly double[] _xy;
    private readonly double[] _cum; // _cum[i] = length from vertex 0 to vertex i; last entry closes the loop

    public TracedLoop(double[] xy)
    {
        _xy = xy;
        var n = xy.Length / 2;
        _cum = new double[n + 1];
        for (var i = 1; i <= n; i++)
        {
            var (ax, ay) = Vertex(i - 1);
            var (bx, by) = Vertex(i % n);
            _cum[i] = _cum[i - 1] + Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }
    }

    public int Count => _xy.Length / 2;
    public double Length => _cum[^1];

    public (double X, double Y) Vertex(int i) { i %= Count; return (_xy[i * 2], _xy[i * 2 + 1]); }

    public Point PointAt(double d)
    {
        d = Math.Clamp(d, 0, Length);
        var i = Array.BinarySearch(_cum, d);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Count - 1);
        var (ax, ay) = Vertex(i);
        var (bx, by) = Vertex(i + 1);
        var seg = _cum[i + 1] - _cum[i];
        var t = seg <= 1e-9 ? 0 : (d - _cum[i]) / seg;
        return new Point(ax + (bx - ax) * t, ay + (by - ay) * t);
    }

    /// <summary>Adds the open polyline between arc distances d0 and d1 as one figure.</summary>
    public void AddRange(StreamGeometryContext ctx, double d0, double d1)
    {
        d0 = Math.Clamp(d0, 0, Length); d1 = Math.Clamp(d1, 0, Length);
        if (d1 - d0 < 1e-6) return;
        ctx.BeginFigure(PointAt(d0), false, false);
        var i = Array.BinarySearch(_cum, d0);
        i = i < 0 ? ~i : i + 1; // first vertex strictly after d0
        for (; i < Count + 1 && _cum[i] < d1; i++)
        {
            var (x, y) = Vertex(i);
            ctx.LineTo(new Point(x, y), true, true);
        }
        ctx.LineTo(PointAt(d1), true, true);
    }
}

/// <summary>A letter (or the laser head): its fill geometry and the contours the laser follows.</summary>
internal sealed class WordmarkPiece
{
    public WordmarkPiece(WordmarkGroup group)
    {
        Name = group.Name;
        Loops = group.Loops.Select(l => new TracedLoop(l)).ToArray();
        TotalLength = Loops.Sum(l => l.Length);

        var fill = new StreamGeometry { FillRule = FillRule.EvenOdd };
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        using (var ctx = fill.Open())
        {
            foreach (var loop in Loops)
            {
                for (var i = 0; i < loop.Count; i++)
                {
                    var (x, y) = loop.Vertex(i);
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    if (i == 0) ctx.BeginFigure(new Point(x, y), true, true);
                    else ctx.LineTo(new Point(x, y), false, false);
                }
            }
        }
        fill.Freeze();
        Fill = fill;
        Bounds = new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    public string Name { get; }
    public TracedLoop[] Loops { get; }
    public double TotalLength { get; }
    public Geometry Fill { get; }
    public Rect Bounds { get; }

    /// <summary>Distance <paramref name="d"/> along the concatenated contours: the geometry already drawn, and
    /// where the laser currently is. Loops are followed one after another (outer contour first).</summary>
    public (Geometry? Path, Point Tip, bool HasTip) Partial(double d, double tail)
    {
        d = Math.Clamp(d, 0, TotalLength);
        var path = new StreamGeometry();
        var tip = default(Point); var hasTip = false;
        using (var ctx = path.Open())
        {
            var offset = 0.0;
            foreach (var loop in Loops)
            {
                if (d <= offset) break;
                var local = Math.Min(d - offset, loop.Length);
                loop.AddRange(ctx, 0, local);
                if (d < offset + loop.Length || loop == Loops[^1]) { tip = loop.PointAt(local); hasTip = true; }
                offset += loop.Length;
            }
        }
        path.Freeze();
        return (path, tip, hasTip);
    }

    /// <summary>The short bright comet trailing the laser tip, in the loop the tip is currently on.</summary>
    public Geometry? Tail(double d, double tailLength)
    {
        d = Math.Clamp(d, 0, TotalLength);
        var offset = 0.0;
        foreach (var loop in Loops)
        {
            if (d <= offset + loop.Length)
            {
                var local = d - offset;
                var g = new StreamGeometry();
                using (var ctx = g.Open()) loop.AddRange(ctx, Math.Max(0, local - tailLength), local);
                g.Freeze();
                return g;
            }
            offset += loop.Length;
        }
        return null;
    }
}

/// <summary>Immutable wordmark data shared by every control and renderer in the process.</summary>
internal static class WordmarkModel
{
    public static readonly WordmarkPiece[] Pieces = WordmarkData.Groups.Select(g => new WordmarkPiece(g)).ToArray();
    public const int LetterCount = 6; // L A S E R O; piece 6 is the laser head
    public static WordmarkPiece Head => Pieces[LetterCount];

    /// <summary>Bounds of the whole mark in wordmark pixel space (the raster is 660 x 205).</summary>
    public static Rect Bounds => new(0, 0, WordmarkData.Width, WordmarkData.Height);
}
