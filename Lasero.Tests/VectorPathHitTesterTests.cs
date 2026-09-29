using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

public sealed class VectorPathHitTesterTests
{
    [Fact]
    public void StraightSegmentReturnsClosestScreenPositionAndParameter()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
        ]);

        var hit = VectorPathHitTester.FindNearest(path, new Position(5, 1, 0), 1.1, point => point);

        Assert.NotNull(hit);
        Assert.Equal(0, hit.Value.SubpathIndex);
        Assert.Equal(0, hit.Value.SegmentIndex);
        Assert.Equal(0.5, hit.Value.T, precision: 8);
        Assert.Equal(1, hit.Value.Distance, precision: 8);
    }

    [Fact]
    public void CubicSegmentRemainsSelectableAfterScreenProjection()
    {
        var path = VectorPath.SingleOpen(
        [
            new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Corner),
            new VectorNode(new Position(10, 0, 0), new Position(10, 10, 0), null, VectorNodeType.Corner),
        ]);

        var hit = VectorPathHitTester.FindNearest(
            path,
            new Position(50, -75, 0),
            0.1,
            point => new Position(point.X * 10, -point.Y * 10, 0));

        Assert.NotNull(hit);
        Assert.Equal(0, hit.Value.SegmentIndex);
        Assert.Equal(0.5, hit.Value.T, precision: 8);
        Assert.Equal(0, hit.Value.Distance, precision: 8);
    }

    [Fact]
    public void SegmentOutsideScreenToleranceIsNotHit()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
        ]);

        var hit = VectorPathHitTester.FindNearest(path, new Position(5, 3, 0), 2, point => point);

        Assert.Null(hit);
    }
}
