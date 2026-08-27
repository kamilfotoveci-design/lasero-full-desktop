using Lasero.Core.Grbl;
using Xunit;

namespace Lasero.Tests;

public class GrblStatusParserTests
{
    [Fact]
    public void ParsesIdleWithMachinePosition()
    {
        var ok = GrblStatusParser.TryParse("<Idle|MPos:1.000,2.000,0.000|FS:0,0>", out var status);

        Assert.True(ok);
        Assert.Equal(GrblMachineMode.Idle, status.Mode);
        Assert.Equal(1.0, status.MachinePosition.X);
        Assert.Equal(2.0, status.MachinePosition.Y);
    }

    [Fact]
    public void DerivesWorkPositionFromMachinePositionAndWco()
    {
        var ok = GrblStatusParser.TryParse("<Run|MPos:15.000,5.000,0.000|FS:1000,0|WCO:10.000,0.000,0.000>", out var status);

        Assert.True(ok);
        Assert.Equal(15.0, status.MachinePosition.X);
        // WPos = MPos - WCO
        Assert.Equal(5.0, status.WorkPosition.X);
        Assert.Equal(5.0, status.WorkPosition.Y);
    }

    [Fact]
    public void ParsesSubStateAfterColonAsBaseMode()
    {
        var ok = GrblStatusParser.TryParse("<Hold:0|MPos:0.000,0.000,0.000|FS:0,0>", out var status);

        Assert.True(ok);
        Assert.Equal(GrblMachineMode.Hold, status.Mode);
    }

    [Fact]
    public void ParsesOverridesAndPins()
    {
        var ok = GrblStatusParser.TryParse("<Run|MPos:0,0,0|FS:500,0|Ov:100,100,80|Pn:PD>", out var status);

        Assert.True(ok);
        Assert.Equal(100, status.FeedOverridePercent);
        Assert.Equal(80, status.SpindleOverridePercent);
        Assert.Equal("PD", status.TriggeredPins);
    }

    [Fact]
    public void RejectsNonStatusLines()
    {
        Assert.False(GrblStatusParser.TryParse("ok", out _));
        Assert.False(GrblStatusParser.TryParse("error:9", out _));
        Assert.False(GrblStatusParser.TryParse("Grbl 1.1h ['$' for help]", out _));
    }
}
