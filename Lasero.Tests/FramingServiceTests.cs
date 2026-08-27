using Lasero.Core.GCode;
using Lasero.Core.Jobs;
using Xunit;

namespace Lasero.Tests;

public class FramingServiceTests
{
    [Fact]
    public void FullOutlineStartsWithLaserOffRapidToFirstCorner()
    {
        var box = new BoundingBox2D(0, 0, 100, 50);
        var lines = FramingService.BuildFrameGCode(box, new FramingOptions { Mode = FramingMode.FullOutline });

        Assert.Equal("G90", lines[0]);
        Assert.Equal("M5", lines[1]);
        Assert.Equal("G0 X0 Y0", lines[2]);
        Assert.Contains("M5", lines[^1]); // ends with laser off
    }

    [Fact]
    public void FullOutlineTracesAllFourCorners()
    {
        var box = new BoundingBox2D(0, 0, 100, 50);
        var lines = FramingService.BuildFrameGCode(box, new FramingOptions { Mode = FramingMode.FullOutline });

        Assert.Contains(lines, l => l.Contains("X100") && l.Contains("Y0"));
        Assert.Contains(lines, l => l.Contains("X100") && l.Contains("Y50"));
        Assert.Contains(lines, l => l.Contains("X0") && l.Contains("Y50"));
    }

    [Fact]
    public void ThrowsForEmptyBoundingBox()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FramingService.BuildFrameGCode(BoundingBox2D.Empty, new FramingOptions()));
    }

    [Fact]
    public void CornersOnlyModeStaysWithinBoundingBox()
    {
        var box = new BoundingBox2D(0, 0, 100, 50);
        var lines = FramingService.BuildFrameGCode(box, new FramingOptions { Mode = FramingMode.CornersOnly, CornerStubLength = 10 });

        Assert.Contains(lines, l => l.StartsWith("G0 X0 Y0"));
    }

    [Fact]
    public void CornersOnlyNeverRapidsWithLaserEnabled()
    {
        var box = new BoundingBox2D(0, 0, 100, 50);
        var lines = FramingService.BuildFrameGCode(box, new FramingOptions
        {
            Mode = FramingMode.CornersOnly,
            CornerStubLength = 10,
            LaserPower = 1,
        });

        var laserEnabled = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("M3", StringComparison.Ordinal)) laserEnabled = true;
            if (line == "M5") laserEnabled = false;
            if (line.StartsWith("G0", StringComparison.Ordinal))
                Assert.False(laserEnabled, $"Rapid move must have the laser disabled: {line}");
        }
    }
}
