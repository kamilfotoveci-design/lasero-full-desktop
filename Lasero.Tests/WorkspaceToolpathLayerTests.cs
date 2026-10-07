using System.Windows;
using System.Windows.Media;
using Lasero.App.Controls;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Tests;

/// <summary>
/// The G-code preview merges consecutive segments into polylines and builds them off the UI thread.
/// Merging must not lose, add or reorder a single segment of any layer.
/// </summary>
public sealed class WorkspaceToolpathLayerTests
{
    private static GCodeSegment Segment(double x0, double y0, double x1, double y1, bool laser, bool rapid, int line) => new()
    {
        Start = new Position(x0, y0, 0), End = new Position(x1, y1, 0),
        LaserOn = laser, IsRapid = rapid, SourceLine = line,
    };

    private static List<(Point A, Point B)> Pieces(Geometry? geometry)
    {
        var pieces = new List<(Point, Point)>();
        if (geometry is null) return pieces;
        foreach (var figure in PathGeometry.CreateFromGeometry(geometry).Figures)
        {
            var current = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line: pieces.Add((current, line.Point)); current = line.Point; break;
                    case PolyLineSegment poly:
                        foreach (var point in poly.Points) { pieces.Add((current, point)); current = point; }
                        break;
                    default: throw new InvalidOperationException(segment.GetType().Name);
                }
            }
        }

        return pieces;
    }

    [Fact]
    public void MergedLayersContainExactlyTheSegmentsOfEachClassInOrder()
    {
        var random = new Random(8);
        var segments = new List<GCodeSegment>();
        double x = 0, y = 0;
        for (var i = 0; i < 3000; i++)
        {
            var nx = Math.Clamp(x + random.NextDouble() * 6 - 3, 0, 300);
            var ny = Math.Clamp(y + random.NextDouble() * 6 - 3, 0, 300);
            var kind = random.Next(10);
            // Mostly chained burn runs (shared end points) with occasional travel and rapid jumps.
            segments.Add(Segment(x, y, nx, ny, laser: kind < 7, rapid: kind >= 9, i));
            if (random.Next(12) == 0) { x = random.NextDouble() * 300; y = random.NextDouble() * 300; } else { x = nx; y = ny; }
        }

        const double scale = 2.5;
        var (burn, travel, rapid) = WorkspaceCanvas.BuildToolpathLayers(segments, scale, 5, 7, 700, CancellationToken.None);

        Point Map(Position p) => new(20 + (p.X - 5) * scale, 700 - 20 - (p.Y - 7) * scale);
        List<(Point, Point)> Expected(Func<GCodeSegment, bool> pick) =>
            segments.Where(pick).Select(s => (Map(s.Start), Map(s.End))).ToList();

        Assert.Equal(Expected(s => s.LaserOn), Pieces(burn));
        Assert.Equal(Expected(s => !s.LaserOn && !s.IsRapid), Pieces(travel));
        Assert.Equal(Expected(s => !s.LaserOn && s.IsRapid), Pieces(rapid));
        Assert.True(burn!.IsFrozen && travel!.IsFrozen && rapid!.IsFrozen, "geometry must be frozen to cross threads");
    }

    [Fact]
    public void ChainedSegmentsBecomeOneFigure()
    {
        var segments = Enumerable.Range(0, 1000)
            .Select(i => Segment(i, 0, i + 1, 0, laser: true, rapid: false, i)).ToList();

        var (burn, travel, rapid) = WorkspaceCanvas.BuildToolpathLayers(segments, 1, 0, 0, 100, CancellationToken.None);

        Assert.Null(travel);
        Assert.Null(rapid);
        Assert.Single(PathGeometry.CreateFromGeometry(burn!).Figures);
    }

    [Fact]
    public void BuildCanBeCancelled()
    {
        var segments = Enumerable.Range(0, 100_000)
            .Select(i => Segment(i, 0, i + 1, 0, laser: true, rapid: false, i)).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WorkspaceCanvas.BuildToolpathLayers(segments, 1, 0, 0, 100, cts.Token));
    }
}
