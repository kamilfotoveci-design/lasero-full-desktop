using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Jobs;

/// <summary>
/// The toolpath flattened into straight pieces, once, so the preview can redraw it at any zoom without
/// re-interpreting arcs. A straight segment is one piece; an arc becomes a short chain of chords. Segment
/// <c>i</c> owns pieces <c>PieceStart[i]</c> up to (not including) <c>PieceStart[i + 1]</c>, which is what
/// lets playback draw "the first N segments" and a fraction of the next one.
/// </summary>
public sealed class PreviewPathGeometry
{
    /// <summary>Largest distance between an arc and its chords, in millimetres.</summary>
    public const double ChordToleranceMm = 0.02;

    public const int MaxChordsPerArc = 360;

    private PreviewPathGeometry(float[] points, int[] pieceStart, BoundingBox2D bounds)
    {
        Points = points;
        PieceStart = pieceStart;
        Bounds = bounds;
    }

    /// <summary>Four floats per piece: x0, y0, x1, y1 in work coordinates (mm).</summary>
    public float[] Points { get; }

    /// <summary>One entry per segment plus a closing one, so a segment's pieces are always a contiguous range.</summary>
    public int[] PieceStart { get; }

    public int SegmentCount => PieceStart.Length - 1;
    public int PieceCount => Points.Length / 4;

    /// <summary>The extent of every move, rapids included.</summary>
    public BoundingBox2D Bounds { get; }

    public static PreviewPathGeometry Build(IReadOnlyList<GCodeSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var points = new List<float>(Math.Max(16, segments.Count * 4));
        var starts = new int[segments.Count + 1];
        var bounds = BoundingBox2D.Empty;

        for (var i = 0; i < segments.Count; i++)
        {
            starts[i] = points.Count / 4;
            var segment = segments[i];
            if (segment.IsArc && segment.ArcCenter is { } center)
            {
                var chords = ChordCount(segment, center, out var startAngle, out var sweep, out var radius);
                var previous = segment.Start;
                for (var step = 1; step <= chords; step++)
                {
                    Position next;
                    if (step == chords)
                    {
                        next = segment.End;
                    }
                    else
                    {
                        var angle = startAngle + sweep * step / chords;
                        next = new Position(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius, 0);
                    }

                    Add(points, previous, next, ref bounds);
                    previous = next;
                }
            }
            else
            {
                Add(points, segment.Start, segment.End, ref bounds);
            }
        }

        starts[segments.Count] = points.Count / 4;
        return new PreviewPathGeometry(points.ToArray(), starts, bounds);
    }

    private static void Add(List<float> points, Position from, Position to, ref BoundingBox2D bounds)
    {
        points.Add((float)from.X);
        points.Add((float)from.Y);
        points.Add((float)to.X);
        points.Add((float)to.Y);
        bounds = bounds.Include(from.X, from.Y).Include(to.X, to.Y);
    }

    private static int ChordCount(GCodeSegment segment, Position center, out double startAngle, out double sweep, out double radius)
    {
        radius = Math.Sqrt(Math.Pow(segment.Start.X - center.X, 2) + Math.Pow(segment.Start.Y - center.Y, 2));
        startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var endAngle = Math.Atan2(segment.End.Y - center.Y, segment.End.X - center.X);
        sweep = endAngle - startAngle;
        if (segment.ArcClockwise)
        {
            if (sweep >= 0) sweep -= Math.PI * 2;
        }
        else if (sweep <= 0)
        {
            sweep += Math.PI * 2;
        }

        if (radius <= 1e-9 || !double.IsFinite(radius) || !double.IsFinite(sweep)) return 1;
        var step = radius <= ChordToleranceMm ? Math.PI : 2 * Math.Acos(1 - ChordToleranceMm / radius);
        return Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / Math.Max(step, 1e-3)), 2, MaxChordsPerArc);
    }
}

/// <summary>
/// The preview's view of the work area: a scale (pixels per millimetre) and the work coordinate shown at the
/// centre of the canvas. Work coordinates have Y pointing up, screen coordinates have Y pointing down.
/// </summary>
public readonly record struct PreviewViewport(double Scale, double CenterX, double CenterY)
{
    public const double MinScale = 0.001;
    public const double MaxScale = 5000;

    public static PreviewViewport Fit(BoundingBox2D box, double widthPx, double heightPx, double marginPx)
    {
        if (box.IsEmpty || widthPx <= 0 || heightPx <= 0) return new PreviewViewport(1, 0, 0);

        var availableWidth = Math.Max(1, widthPx - 2 * marginPx);
        var availableHeight = Math.Max(1, heightPx - 2 * marginPx);
        var scaleX = box.Width < 1e-6 ? double.PositiveInfinity : availableWidth / box.Width;
        var scaleY = box.Height < 1e-6 ? double.PositiveInfinity : availableHeight / box.Height;
        var scale = Math.Min(scaleX, scaleY);
        if (double.IsInfinity(scale)) scale = 10;
        return new PreviewViewport(Math.Clamp(scale, MinScale, MaxScale), (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
    }

    public double ToScreenX(double x, double widthPx) => widthPx / 2 + (x - CenterX) * Scale;
    public double ToScreenY(double y, double heightPx) => heightPx / 2 - (y - CenterY) * Scale;
    public double ToWorldX(double screenX, double widthPx) => CenterX + (screenX - widthPx / 2) / Scale;
    public double ToWorldY(double screenY, double heightPx) => CenterY - (screenY - heightPx / 2) / Scale;

    /// <summary>Zooms by <paramref name="factor"/> keeping the work point under the pointer where it is.</summary>
    public PreviewViewport ZoomAt(double screenX, double screenY, double factor, double widthPx, double heightPx)
    {
        if (!double.IsFinite(factor) || factor <= 0) return this;
        var scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        var worldX = ToWorldX(screenX, widthPx);
        var worldY = ToWorldY(screenY, heightPx);
        return new PreviewViewport(
            scale,
            worldX - (screenX - widthPx / 2) / scale,
            worldY + (screenY - heightPx / 2) / scale);
    }

    /// <summary>Drags the content by a pixel offset: it follows the pointer.</summary>
    public PreviewViewport PanBy(double deltaXPx, double deltaYPx) =>
        new(Scale, CenterX - deltaXPx / Scale, CenterY + deltaYPx / Scale);
}
