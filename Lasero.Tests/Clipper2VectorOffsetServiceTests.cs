using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Xunit;

namespace Lasero.Tests;

public class Clipper2VectorOffsetServiceTests
{
    private static readonly IVectorOffsetService Service = Clipper2VectorOffsetService.Default;

    private static List<Position> Square(double minX, double minY, double maxX, double maxY) =>
    [
        new Position(minX, minY, 0), new Position(maxX, minY, 0),
        new Position(maxX, maxY, 0), new Position(minX, maxY, 0),
    ];

    private static List<Position> Circle(double centerX, double centerY, double radius, int sides = 64)
    {
        var points = new List<Position>(sides);
        for (var i = 0; i < sides; i++)
        {
            var angle = 2 * Math.PI * i / sides;
            points.Add(new Position(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle), 0));
        }
        return points;
    }

    private static double SignedArea(IReadOnlyList<Position> points)
    {
        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Position> points)
    {
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return (minX, minY, maxX, maxY);
    }

    [Fact]
    public void OutwardOffsetOfARectangleGrowsItByTheAnalyticAmount()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 10) };

        var result = Service.OffsetClosedGroup(rings, 2, VectorJoinType.Miter, 4);

        var ring = Assert.Single(result);
        // A miter-joined outset of a rectangle by d on every side grows it to (w+2d) x (h+2d).
        Assert.Equal(24 * 14, Math.Abs(SignedArea(ring)), precision: 3);
    }

    [Fact]
    public void InwardOffsetOfARectangleShrinksItByTheAnalyticAmount()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 10) };

        var result = Service.OffsetClosedGroup(rings, -2, VectorJoinType.Miter, 4);

        var ring = Assert.Single(result);
        Assert.Equal(16 * 6, Math.Abs(SignedArea(ring)), precision: 3);
    }

    [Fact]
    public void OutwardOffsetOfACircleIncreasesTheEffectiveRadius()
    {
        var circle = Circle(0, 0, 10);
        var rings = new List<IReadOnlyList<Position>> { circle };

        var result = Service.OffsetClosedGroup(rings, 3, VectorJoinType.Round, 2);

        var ring = Assert.Single(result);
        var effectiveRadius = Math.Sqrt(Math.Abs(SignedArea(ring)) / Math.PI);
        Assert.True(effectiveRadius > 12.5 && effectiveRadius < 13.5,
            $"Expected effective radius near 13, got {effectiveRadius}");
    }

    [Fact]
    public void OutwardOffsetOfACompoundPathWithAHoleGrowsTheOuterAndShrinksTheHole()
    {
        var outer = Square(0, 0, 20, 20);
        var hole = Square(8, 8, 12, 12); // wound opposite the outer ring so it reads as a hole
        hole.Reverse();
        var rings = new List<IReadOnlyList<Position>> { outer, hole };

        var result = Service.OffsetClosedGroup(rings, 1, VectorJoinType.Miter, 4);

        Assert.Equal(2, result.Count);
        var areas = result.Select(SignedArea).ToList();
        var outerAfter = areas.OrderByDescending(Math.Abs).First();
        var holeAfter = areas.OrderByDescending(Math.Abs).Last();

        Assert.True(Math.Abs(outerAfter) > 400, "Outer ring should have grown beyond its original 400mm² area.");
        Assert.True(Math.Abs(holeAfter) < 16, "Hole ring should have shrunk below its original 16mm² area.");
        Assert.True(Math.Sign(outerAfter) != Math.Sign(holeAfter), "Outer and hole rings must keep opposite winding.");
    }

    [Fact]
    public void OutwardOffsetOfNestedHolesKeepsAllThreeRingsNestedAndMovingTheRightWay()
    {
        // outer(0..40) > hole(8..32, reversed) > island-inside-hole(14..26, same winding as outer)
        var outer = Square(0, 0, 40, 40);
        var hole = Square(8, 8, 32, 32);
        hole.Reverse();
        var island = Square(14, 14, 26, 26);
        var rings = new List<IReadOnlyList<Position>> { outer, hole, island };

        var result = Service.OffsetClosedGroup(rings, 1, VectorJoinType.Miter, 4);

        Assert.Equal(3, result.Count);
        var signedAreas = result.Select(SignedArea).OrderByDescending(Math.Abs).ToList();

        // Largest (outer) must have grown past 1600, smallest two (hole, island) must still nest and
        // move oppositely to the outer's winding / each other appropriately.
        Assert.True(Math.Abs(signedAreas[0]) > 1600, "Outer ring should have grown.");
        Assert.True(Math.Sign(signedAreas[0]) != Math.Sign(signedAreas[1]), "Hole must wind opposite the outer ring.");
        Assert.True(Math.Sign(signedAreas[1]) != Math.Sign(signedAreas[2]), "Island must wind opposite the hole.");
    }

    [Fact]
    public void ConcaveLShapeOffsetsOutwardWithoutThrowingAndKeepsOneRing()
    {
        var lShape = new List<Position>
        {
            new(0, 0, 0), new(20, 0, 0), new(20, 10, 0),
            new(10, 10, 0), new(10, 20, 0), new(0, 20, 0),
        };
        var rings = new List<IReadOnlyList<Position>> { lShape };

        var result = Service.OffsetClosedGroup(rings, 1, VectorJoinType.Round, 2);

        var ring = Assert.Single(result);
        Assert.True(Math.Abs(SignedArea(ring)) > Math.Abs(SignedArea(lShape)));
    }

    [Fact]
    public void ConcaveLShapeOffsetsInwardWithoutThrowingAndKeepsOneRing()
    {
        var lShape = new List<Position>
        {
            new(0, 0, 0), new(20, 0, 0), new(20, 10, 0),
            new(10, 10, 0), new(10, 20, 0), new(0, 20, 0),
        };
        var rings = new List<IReadOnlyList<Position>> { lShape };

        var result = Service.OffsetClosedGroup(rings, -1, VectorJoinType.Round, 2);

        var ring = Assert.Single(result);
        Assert.True(Math.Abs(SignedArea(ring)) < Math.Abs(SignedArea(lShape)));
    }

    [Fact]
    public void ShrinkingBeyondDisappearanceReturnsEmptyInsteadOfThrowing()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 4, 4) };

        var result = Service.OffsetClosedGroup(rings, -10, VectorJoinType.Miter, 4);

        Assert.Empty(result);
    }

    [Fact]
    public void RoundMiterAndBevelJoinsProduceGeometricallyDifferentOutwardCorners()
    {
        // A sharp 90° corner makes the three join styles diverge the most: miter reaches a full
        // right-angle point outward, bevel cuts a flat across the corner, round arcs between them.
        var corner = new List<Position> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0) };
        var rings = new List<IReadOnlyList<Position>> { corner };

        var miter = Service.OffsetClosedGroup(rings, 3, VectorJoinType.Miter, 4);
        var bevel = Service.OffsetClosedGroup(rings, 3, VectorJoinType.Bevel, 4);
        var round = Service.OffsetClosedGroup(rings, 3, VectorJoinType.Round, 4);

        var miterRing = Assert.Single(miter);
        var bevelRing = Assert.Single(bevel);
        var roundRing = Assert.Single(round);

        // The corner at (10,10) offsets outward toward (13,13) (distance sqrt(2)*3 ~= 4.24) for a
        // true miter; a bevel clips that point off, so its farthest vertex near that corner is closer
        // to (10,10) than miter's is.
        var originalCorner = new Position(10, 10, 0);
        double MaxDistanceNear(IReadOnlyList<Position> ring) =>
            ring.Where(p => p.X > 9 && p.Y > 9)
                .Select(p => Math.Sqrt(Math.Pow(p.X - originalCorner.X, 2) + Math.Pow(p.Y - originalCorner.Y, 2)))
                .DefaultIfEmpty(0)
                .Max();

        var miterReach = MaxDistanceNear(miterRing);
        var bevelReach = MaxDistanceNear(bevelRing);

        Assert.True(miterReach > bevelReach,
            $"Miter's corner ({miterReach}) should reach farther from the original corner than bevel's ({bevelReach}).");
        // Round's corner is smoothed with several vertices where miter/bevel use one or two.
        Assert.True(roundRing.Count > bevelRing.Count,
            "A round join should produce more vertices around the corner than a bevel join.");
    }

    [Fact]
    public void OpenPathOffsetProducesAClosedBufferExtendingRoughlyTheDistanceOnBothSides()
    {
        // Path spans x:[0,10] y:[0,10]. A butt-capped buffer stays flush with the path at its two
        // endpoints (the start at (0,0) and the end at (10,10)) but bulges out by ~distanceMm on
        // either side of the path itself — the bottom of the first (horizontal) leg and the right of
        // the second (vertical) leg, the two sides the path actually exposes.
        var path = new List<Position> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0) };

        var result = Service.OffsetOpenPath(path, 2, VectorJoinType.Round, 2);

        Assert.NotEmpty(result);
        var ring = result[0];
        var (minX, minY, maxX, maxY) = Bounds(ring);
        Assert.True(minX > -0.5 && minX < 0.5, $"minX {minX} should stay flush with the path's own start (butt cap).");
        Assert.True(maxY > 9.5 && maxY < 10.5, $"maxY {maxY} should stay flush with the path's own end (butt cap).");
        Assert.True(minY < -1.5 && minY > -2.5, $"minY {minY} should sit ~2mm below the path's bottom edge.");
        Assert.True(maxX > 11.5 && maxX < 12.5, $"maxX {maxX} should sit ~2mm right of the path's right edge.");
    }

    [Fact]
    public void SelfIntersectingBowtieOffsetsOutwardWithoutThrowing()
    {
        var bowtie = new List<Position>
        {
            new(0, 0, 0), new(10, 10, 0), new(10, 0, 0), new(0, 10, 0),
        };
        var rings = new List<IReadOnlyList<Position>> { bowtie };

        var result = Service.OffsetClosedGroup(rings, 1, VectorJoinType.Round, 2);

        Assert.All(result, ring => Assert.True(ring.Count >= 3));
        Assert.All(result, ring => Assert.True(Math.Abs(SignedArea(ring)) > 0));
    }
}
