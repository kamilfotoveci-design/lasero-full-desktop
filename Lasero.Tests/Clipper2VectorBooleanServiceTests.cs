using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Xunit;

namespace Lasero.Tests;

public class Clipper2VectorBooleanServiceTests
{
    private static readonly IVectorBooleanService Service = Clipper2VectorBooleanService.Default;

    private static List<Position> Square(double minX, double minY, double maxX, double maxY) =>
    [
        new Position(minX, minY, 0), new Position(maxX, minY, 0),
        new Position(maxX, maxY, 0), new Position(minX, maxY, 0),
    ];

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

    [Fact]
    public void ResolveOfANestedPairProducesAnOuterRingAndAnOppositelyWoundHole()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20), Square(8, 8, 12, 12) };

        var resolved = Service.Resolve(rings, VectorFillRule.EvenOdd);

        Assert.Equal(2, resolved.Count);
        var areas = resolved.Select(SignedArea).OrderBy(area => Math.Abs(area)).ToList();
        Assert.Equal(16, Math.Abs(areas[0]), precision: 6);
        Assert.Equal(400, Math.Abs(areas[1]), precision: 6);
        Assert.True(Math.Sign(areas[0]) != Math.Sign(areas[1]));
    }

    [Fact]
    public void ResolveOfTwoDisjointRingsReturnsBothUnchanged()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 10, 10), Square(20, 0, 30, 10) };

        var resolved = Service.Resolve(rings, VectorFillRule.EvenOdd);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(200, resolved.Sum(ring => Math.Abs(SignedArea(ring))), precision: 6);
    }

    /// <summary>EvenOdd is not a general "merge overlapping shapes" operation: two rings that
    /// partially overlap without one nesting inside the other have their shared area excluded
    /// (covered twice = even parity = unfilled), not merged — the opposite of what Union(subject,
    /// clip) does for the same two rings. This is exactly why BuildSourceRings in SceneViewModel.cs
    /// only ever calls Resolve on the rings of one GeometrySetId group (which — by construction, via
    /// NormalizeNestedCompoundPaths — only ever holds properly nested hole relationships, never
    /// arbitrary partial overlap) and reaches for a real Union to combine separate groups instead.</summary>
    [Fact]
    public void ResolveOfTwoPartiallyOverlappingRingsExcludesTheSharedAreaUnderEvenOdd()
    {
        var rings = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20), Square(10, 10, 30, 30) };

        var resolved = Service.Resolve(rings, VectorFillRule.EvenOdd);

        // Each 400-area square loses its 100-area shared corner: (400-100) + (400-100) = 600 — not
        // the 700 a true union of the same two rings produces (see UnionOfTwoOverlappingSquares...).
        Assert.Equal(600, resolved.Sum(ring => Math.Abs(SignedArea(ring))), precision: 6);
    }

    [Fact]
    public void UnionOfTwoOverlappingSquaresCountsTheSharedAreaOnce()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20) };
        var clip = new List<IReadOnlyList<Position>> { Square(10, 10, 30, 30) };

        var result = Service.Union(subject, clip, VectorFillRule.EvenOdd);

        Assert.Equal(700, result.Sum(ring => Math.Abs(SignedArea(ring))), precision: 6);
    }

    [Fact]
    public void SubtractOfAFullyEnclosedShapePunchesAHole()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20) };
        var clip = new List<IReadOnlyList<Position>> { Square(8, 8, 12, 12) };

        var result = Service.Subtract(subject, clip, VectorFillRule.EvenOdd);

        Assert.Equal(2, result.Count);
        var areas = result.Select(SignedArea).OrderBy(area => Math.Abs(area)).ToList();
        Assert.Equal(16, Math.Abs(areas[0]), precision: 6);
        Assert.Equal(400, Math.Abs(areas[1]), precision: 6);
        Assert.True(Math.Sign(areas[0]) != Math.Sign(areas[1]), "The hole ring must wind opposite the outer ring.");
    }

    [Fact]
    public void SubtractOfTheWholeShapeFromItselfLeavesNothing()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20) };
        var clip = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20) };

        var result = Service.Subtract(subject, clip, VectorFillRule.EvenOdd);

        Assert.Empty(result);
    }

    [Fact]
    public void IntersectOfTwoDisjointSquaresIsEmpty()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 10, 10) };
        var clip = new List<IReadOnlyList<Position>> { Square(20, 0, 30, 10) };

        var result = Service.Intersect(subject, clip, VectorFillRule.EvenOdd);

        Assert.Empty(result);
    }

    [Fact]
    public void XorOfTwoOverlappingSquaresKeepsOnlyTheNonOverlappingParts()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 10) };
        var clip = new List<IReadOnlyList<Position>> { Square(10, 0, 30, 10) };

        var result = Service.Xor(subject, clip, VectorFillRule.EvenOdd);

        Assert.Equal(2, result.Count);
        Assert.Equal(200, result.Sum(ring => Math.Abs(SignedArea(ring))), precision: 6);
    }

    [Fact]
    public void ClosedRingsDoNotRepeatTheirFirstPointAsTheirLastPoint()
    {
        var subject = new List<IReadOnlyList<Position>> { Square(0, 0, 20, 20) };
        var clip = new List<IReadOnlyList<Position>> { Square(10, 10, 30, 30) };

        var result = Service.Union(subject, clip, VectorFillRule.EvenOdd);

        var ring = Assert.Single(result);
        Assert.NotEqual(ring[0], ring[^1]);
    }
}
