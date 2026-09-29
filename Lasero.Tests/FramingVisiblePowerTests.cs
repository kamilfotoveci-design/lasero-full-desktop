using System.Linq;
using Lasero.Core.GCode;
using Lasero.Core.Jobs;
using Xunit;

namespace Lasero.Tests;

public sealed class FramingVisiblePowerTests
{
    [Fact]
    public void VisibleFramingPowerIsOnePercent() =>
        Assert.Equal(1, FramingOptions.VisiblePowerPercent);

    [Theory]
    [InlineData(1000, "M3 S10")]
    [InlineData(255, "M3 S2.55")]
    public void FullOutlineFrameLightsTheBeamAtOnePercentOfControllerMaximum(double maxS, string expected)
    {
        var box = new BoundingBox2D(0, 0, 40, 20);
        var lines = FramingService.BuildFrameGCode(box, new FramingOptions
        {
            Mode = FramingMode.FullOutline,
            LaserPower = FramingOptions.VisiblePowerPercent,
        }, maxS).ToList();

        Assert.Contains(expected, lines);
        Assert.Equal("M5", lines[^1]);
        Assert.True(lines.IndexOf(expected) > lines.IndexOf("G0 X0 Y0"), "the beam must never light before the rapid to the start corner");
    }

    [Fact]
    public void ZeroPowerKeepsTheBeamOff()
    {
        var lines = FramingService.BuildFrameGCode(new BoundingBox2D(0, 0, 40, 20), new FramingOptions { LaserPower = 0 }, 1000).ToList();
        Assert.DoesNotContain(lines, l => l.StartsWith("M3") || l.StartsWith("M4"));
    }
}
