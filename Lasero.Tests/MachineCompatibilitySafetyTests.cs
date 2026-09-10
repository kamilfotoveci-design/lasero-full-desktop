using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class MachineCompatibilitySafetyTests
{
    [Theory]
    [InlineData("algolaser-alpha-mk2-20w")]
    [InlineData("algolaser-pixi")]
    [InlineData("xtool-f2")]
    [InlineData("xtool-s1")]
    [InlineData("algolaser-diy-kit-mk2")]
    public void UnverifiedModelCannotOpenTransportEvenOutsideUi(string id)
    {
        using var transport = new VirtualGrblTransport();
        using var connection = new GrblConnection(transport) { CompatibilityId = id };
        Assert.Throws<InvalidOperationException>(() => connection.Connect(VirtualGrblTransport.PortName));
        Assert.False(transport.IsOpen);
        Assert.Equal(GrblConnectionState.Disconnected, connection.State);
        Assert.All(Enum.GetValues<MachineCapability>(), capability =>
            Assert.Equal(CapabilityVerification.Unverified, MachineCompatibilityCatalog.Get(id).GetCapability(capability).Verification));
    }

    [Fact]
    public void ConnectedSessionCannotChangeCompatibilitySelection()
    {
        using var connection = new GrblConnection(new VirtualGrblTransport());
        connection.Connect(VirtualGrblTransport.PortName);
        Assert.Throws<InvalidOperationException>(() => connection.CompatibilityId = "xtool-f2");
        Assert.Equal(MachineCompatibilityCatalog.ExistingGrblId, connection.CompatibilityId);
    }

    [Fact]
    public void UnknownCompatibilityIdIsRejected()
    {
        using var connection = new GrblConnection(new VirtualGrblTransport());
        Assert.Throws<ArgumentException>(() => connection.CompatibilityId = "xtool-f2-ultra");
    }

    [Theory]
    [InlineData("$130=NaN")]
    [InlineData("$130=Infinity")]
    [InlineData("$30=1e999")]
    public void NonfiniteFirmwareSettingsAreRejected(string setting)
    {
        Assert.False(GrblDeviceProfileParser.TryParseSetting(setting, out _, out _));
        Assert.Empty(GrblDeviceProfileParser.Parse([setting]).NumericSettings);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteWorkAreaBlocksExecution(double value)
    {
        var result = JobPreflight.Evaluate(Context() with { WorkAreaWidthMm = value });
        Assert.Contains(result.Issues, issue => issue.Code == "machine.invalid-work-area");
        Assert.False(result.CanStart);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonfiniteGeneratedBoundsBlockExecution(double value)
    {
        var document = new GCodeDocument { RawLines = ["G1 X1 Y1"], Segments = [], BoundingBox = new(0, 0, value, 1) };
        var result = JobPreflight.Evaluate(Context() with { Document = document });
        Assert.Contains(result.Issues, issue => issue.Code == "job.invalid-bounds");
        Assert.False(result.CanStart);
    }

    private static JobPreflightContext Context() => new()
    {
        IsConnected = true, Document = GCodeParser.Parse(["G0 X0 Y0", "G1 X1 Y1"], "synthetic"),
        MachineStatus = new MachineStatus { Mode = GrblMachineMode.Idle, MachinePosition = Position.Zero, WorkPosition = Position.Zero, WorkCoordinateOffset = Position.Zero },
        MachineStatusAge = TimeSpan.Zero, RequireFraming = false, HasFramedCurrentDocument = false,
        WorkAreaWidthMm = 100, WorkAreaHeightMm = 100,
    };
}