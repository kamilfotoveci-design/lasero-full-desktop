using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class GrblDeviceProfileParserTests
{
    [Fact]
    public void PixiSizedTravelDoesNotProveModelIdentity()
    {
        var profile = GrblDeviceProfileParser.Parse(["$130=100", "$131=100", "$30=1000", "$32=1"]);

        Assert.Null(KnownMachineProfiles.Match(profile));
    }

    [Fact]
    public void AlphaSizedTravelDoesNotProveModelIdentity()
    {
        var profile = GrblDeviceProfileParser.Parse(["$130=400", "$131=410", "$30=1000", "$32=1"]);

        Assert.Null(KnownMachineProfiles.Match(profile));
    }

    [Fact]
    public void ParsesWorkAreaAndLaserModeFromStandardSettings()
    {
        var profile = GrblDeviceProfileParser.Parse(
            ["$30=1000", "$32=1", "$130=500.000", "$131=400.000", "$132=80.000"],
            "Grbl 1.1h");

        Assert.Equal("Grbl 1.1h", profile.FirmwareBanner);
        Assert.Equal(500, profile.MaxTravelXmm);
        Assert.Equal(400, profile.MaxTravelYmm);
        Assert.Equal(80, profile.MaxTravelZmm);
        Assert.Equal(1000, profile.MaxSpindleSpeed);
        Assert.True(profile.LaserModeEnabled);
    }

    [Theory]
    [InlineData("$130=500.000 (x, step/mm)", 130, 500)]
    [InlineData("$32=0", 32, 0)]
    [InlineData(" $130=500", 0, 0)]
    [InlineData("ok", 0, 0)]
    public void SettingParserHandlesCommonFormats(string line, int expectedNumber, double expectedValue)
    {
        var parsed = GrblDeviceProfileParser.TryParseSetting(line, out var number, out var value);

        Assert.Equal(expectedNumber != 0, parsed);
        Assert.Equal(expectedNumber, number);
        Assert.Equal(expectedValue, value);
    }
}
