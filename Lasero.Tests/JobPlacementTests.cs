using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

public sealed class JobPlacementTests
{
    [Theory]
    [InlineData(JobOriginAnchor.TopLeft, 90, 170)]
    [InlineData(JobOriginAnchor.Center, 70, 180)]
    [InlineData(JobOriginAnchor.TopRight, 50, 170)]
    [InlineData(JobOriginAnchor.BottomLeft, 90, 190)]
    [InlineData(JobOriginAnchor.BottomRight, 50, 190)]
    public void PhysicalReferencePlacesTheSelectedJobAnchorAtTheLaserPosition(
        JobOriginAnchor anchor,
        double expectedOffsetX,
        double expectedOffsetY)
    {
        var bounds = new BoundingBox2D(10, 10, 50, 30);
        var placement = new JobPlacement(new Position(100, 200, 0), anchor);

        var offset = placement.CalculateOffset(bounds);

        Assert.Equal(expectedOffsetX, offset.X, 6);
        Assert.Equal(expectedOffsetY, offset.Y, 6);
    }

    [Fact]
    public void EmptyToolpathCannotBePlaced()
    {
        var placement = new JobPlacement(new Position(100, 200, 0));

        Assert.Throws<InvalidOperationException>(() => placement.CalculateOffset(BoundingBox2D.Empty));
    }
}
