using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.Tests;

/// <summary>
/// A closed ring is fitted as chains between break points, and the nodes of neighbouring chains are merged.
/// Merging used to keep only one of the two handles of each shared anchor, and at the closing seam it kept
/// neither, so the last chain of every ring was drawn as one straight chord across the shape. These tests
/// fit dense closed polylines with 0, 1 and 2 corners and require the whole curve, seam included, to stay on
/// the polyline.
/// </summary>
public sealed class BezierFitterSeamTests
{
    private static readonly BezierFitOptions Options = new()
    {
        ToleranceMm = 0.35,
        CornerAngleDegrees = 30,
        DedupeDistanceMm = 0.5,
    };

    [Fact]
    public void Circle_NoCorners_CurveStaysOnTheRingIncludingTheSeam()
    {
        var ring = Dense(Enumerable.Range(0, 720).Select(i =>
        {
            var a = i * Math.PI / 360;
            return new Position(60 + 50 * Math.Cos(a), 60 + 50 * Math.Sin(a), 0);
        }).ToList());

        AssertStaysOn(BezierFitter.FitClosed(ring, Options), ring, 0.6);
    }

    [Fact]
    public void Teardrop_OneCorner_CurveStaysOnTheRingIncludingTheSeam()
    {
        var points = new List<Position> { new(60, 10, 0) };
        for (var degrees = -30.0; degrees <= 210.0; degrees += 1)
        {
            var a = degrees * Math.PI / 180;
            points.Add(new Position(60 + 30 * Math.Cos(a), 70 + 30 * Math.Sin(a), 0));
        }

        var ring = Dense(points);
        AssertStaysOn(BezierFitter.FitClosed(ring, Options), ring, 0.6);
    }

    [Fact]
    public void Lens_TwoCorners_CurveStaysOnTheRingIncludingTheSeam()
    {
        var phi = Math.Acos(20.0 / 30.0);
        var points = new List<Position>();
        for (var a = -phi; a <= phi; a += 0.01)
            points.Add(new Position(40 + 30 * Math.Cos(a), 60 + 30 * Math.Sin(a), 0));
        for (var a = Math.PI - phi; a <= Math.PI + phi; a += 0.01)
            points.Add(new Position(80 + 30 * Math.Cos(a), 60 + 30 * Math.Sin(a), 0));

        var ring = Dense(points);
        AssertStaysOn(BezierFitter.FitClosed(ring, Options), ring, 0.6);
    }

    [Fact]
    public void EveryClosedSubpathSegmentKeepsItsHandles_NoStraightChordBetweenDistantAnchors()
    {
        var ring = Dense(Enumerable.Range(0, 720).Select(i =>
        {
            var a = i * Math.PI / 360;
            return new Position(60 + 50 * Math.Cos(a), 60 + 50 * Math.Sin(a), 0);
        }).ToList());

        var subpath = BezierFitter.FitClosed(ring, Options);

        for (var i = 0; i < subpath.SegmentCount; i++)
        {
            var (a, b) = subpath.Segment(i);
            var chord = Math.Sqrt(Math.Pow(a.Anchor.X - b.Anchor.X, 2) + Math.Pow(a.Anchor.Y - b.Anchor.Y, 2));
            if (chord > 15) Assert.False(VectorSubpath.IsStraightSegment(a, b), $"segment {i} is a straight chord of {chord:0.0} mm");
        }
    }

    /// <summary>Resamples a closed polyline to a spacing of about 0.5 mm so it is a dense ring like the engine's.</summary>
    private static List<Position> Dense(List<Position> closed)
    {
        var result = new List<Position>();
        for (var i = 0; i < closed.Count; i++)
        {
            var a = closed[i];
            var b = closed[(i + 1) % closed.Count];
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) / 0.5));
            for (var s = 0; s < steps; s++)
            {
                var t = s / (double)steps;
                result.Add(new Position(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0));
            }
        }
        return result;
    }

    private static void AssertStaysOn(VectorSubpath subpath, List<Position> ring, double tolerance)
    {
        Assert.True(subpath.IsClosed);
        var flattened = subpath.Flatten(0.02);
        for (var i = 0; i < flattened.Count; i++)
        {
            var a = flattened[i];
            var b = flattened[(i + 1) % flattened.Count];
            foreach (var t in new[] { 0.0, 0.5 })
            {
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                var distance = DistanceToRing(ring, x, y);
                Assert.True(distance <= tolerance, $"curve passes ({x:0.0}, {y:0.0}), {distance:0.00} mm from the ring");
            }
        }
    }

    private static double DistanceToRing(List<Position> ring, double x, double y)
    {
        var best = double.MaxValue;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var lengthSquared = abx * abx + aby * aby;
            var t = lengthSquared < 1e-12 ? 0 : Math.Clamp(((x - a.X) * abx + (y - a.Y) * aby) / lengthSquared, 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(x - (a.X + abx * t), 2) + Math.Pow(y - (a.Y + aby * t), 2)));
        }
        return best;
    }
}
