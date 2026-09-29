using Lasero.Core.GCode;
using Xunit;

namespace Lasero.Tests;

public class GCodeParserTests
{
    [Fact]
    public void ComputesBoundingBoxForSimpleRectangle()
    {
        var lines = new[]
        {
            "G90",
            "G21",
            "G0 X0 Y0",
            "M3 S255",
            "G1 X100 Y0 F1000",
            "G1 X100 Y50",
            "G1 X0 Y50",
            "G1 X0 Y0",
            "M5",
        };

        var doc = GCodeParser.Parse(lines);

        Assert.Equal(0, doc.BoundingBox.MinX);
        Assert.Equal(0, doc.BoundingBox.MinY);
        Assert.Equal(100, doc.BoundingBox.MaxX);
        Assert.Equal(50, doc.BoundingBox.MaxY);
    }

    [Fact]
    public void MarksLaserOnOnlyDuringPoweredFeedMoves()
    {
        var lines = new[]
        {
            "G0 X0 Y0",
            "M3 S500",
            "G1 X10 Y0 F1000",
            "M5",
            "G0 X20 Y0",
        };

        var doc = GCodeParser.Parse(lines);

        Assert.False(doc.Segments[0].LaserOn); // G0 travel
        Assert.True(doc.Segments[1].LaserOn);  // G1 with spindle on and S>0
        Assert.False(doc.Segments[2].LaserOn); // G0 after M5
    }

    [Fact]
    public void HandlesIncrementalDistanceMode()
    {
        var lines = new[]
        {
            "G91",
            "G0 X10 Y0",
            "G0 X10 Y0", // should end at X20 absolute, not X10
        };

        var doc = GCodeParser.Parse(lines);

        Assert.Equal(20, doc.Segments[^1].End.X);
    }

    [Fact]
    public void ConvertsInchesToMillimeters()
    {
        var lines = new[] { "G20", "G0 X1 Y0" }; // 1 inch

        var doc = GCodeParser.Parse(lines);

        Assert.Equal(25.4, doc.Segments[0].End.X, precision: 3);
    }

    [Fact]
    public void FirstRapidTravelFromTheImplicitOriginIsNotPartOfTheJobExtent()
    {
        var lines = new[]
        {
            "G90", "G21", "M5",
            "G0 X200 Y100",
            "M4 S500",
            "G1 X230 Y100 F1000",
            "G1 X230 Y130",
            "M5",
        };

        var doc = GCodeParser.Parse(lines);

        Assert.Equal(200, doc.BoundingBox.MinX);
        Assert.Equal(100, doc.BoundingBox.MinY);
        Assert.Equal(30, doc.BoundingBox.Width);
        Assert.Equal(30, doc.BoundingBox.Height);
    }

    [Fact]
    public void IgnoresModalOnlyLinesWithNoMotion()
    {
        var lines = new[] { "G21", "G90", "M3 S255" };

        var doc = GCodeParser.Parse(lines);

        Assert.Empty(doc.Segments);
        Assert.True(doc.BoundingBox.IsEmpty);
    }
}
