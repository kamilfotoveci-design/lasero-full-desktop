using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Raster;

namespace Lasero.Tests;

public sealed class JobPreflightTests
{
    [Fact]
    public void OversizedRasterJobTripsTheSameOutsideWorkAreaCheckAsVectorJobs()
    {
        // Proves the raster pipeline reuses JobPreflight's existing bounds check rather than
        // needing (or having) a second, raster-specific implementation.
        var image = new ProcessedImage { Width = 2, Height = 1, PowerFraction = [1.0, 1.0] };
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 1000, // far larger than the 500mm work area used by Context() below
            LineIntervalMm = 1,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };
        var job = RasterPlanner.Plan(image, options);
        var document = GCodeParser.Parse(GrblRasterGenerator.Generate(job), "raster");

        var result = JobPreflight.Evaluate(Context() with { Document = document });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "job.outside-work-area");
    }

    [Fact]
    public void ReadyIdleMachineWithFramedInBoundsJobCanStart()
    {
        var result = JobPreflight.Evaluate(Context());

        Assert.True(result.CanStart);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(GrblMachineMode.Alarm)]
    [InlineData(GrblMachineMode.Door)]
    [InlineData(GrblMachineMode.Hold)]
    [InlineData(GrblMachineMode.Run)]
    [InlineData(GrblMachineMode.Unknown)]
    public void NonIdleMachineBlocksStart(GrblMachineMode mode)
    {
        var result = JobPreflight.Evaluate(Context() with { MachineStatus = Status(mode) });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "machine.not-idle");
    }

    [Fact]
    public void MissingFramingBlocksWhenRequired()
    {
        var result = JobPreflight.Evaluate(Context() with { HasFramedCurrentDocument = false });

        Assert.False(result.CanStart);
        Assert.Equal("job.framing-required", result.FirstBlockingIssue!.Code);
    }

    [Fact]
    public void JobOutsideWorkAreaBlocksStart()
    {
        var document = GCodeParser.Parse(["G0 X0 Y0", "G1 X501 Y10"], "outside");
        var result = JobPreflight.Evaluate(Context() with { Document = document });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "job.outside-work-area");
    }

    [Fact]
    public void DoorOrLimitInputBlocksEvenWhenModeIsIdle()
    {
        var result = JobPreflight.Evaluate(Context() with { MachineStatus = Status(GrblMachineMode.Idle, "XD") });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "machine.limit-triggered");
        Assert.Contains(result.Issues, issue => issue.Code == "machine.door-triggered");
    }

    [Fact]
    public void StaleMachineStatusBlocksStart()
    {
        var result = JobPreflight.Evaluate(Context() with { MachineStatusAge = TimeSpan.FromSeconds(3) });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "machine.stale-status");
    }

    [Fact]
    public void InvalidVectorEngravingSettingsBlockStartWithActionableReasons()
    {
        var layer = new LayerSettings
        {
            Color = new RgbColor(255, 0, 0), Name = "Testovací gravírování", Mode = LayerMode.Fill,
            Power = 0, Speed = 0, Passes = 0, FillLineIntervalMm = 0,
        };
        var result = JobPreflight.Evaluate(Context() with { Layers = [layer] });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-power");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-speed");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-passes");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-interval");
    }

    [Fact]
    public void InvalidRasterEngravingSettingsBlockStart()
    {
        var options = new RasterImportOptions { MaxPower = 101, FeedRatePerMinute = 0, Passes = 0, Dpi = 0 };
        var result = JobPreflight.Evaluate(Context() with { RasterOptions = [options] });

        Assert.False(result.CanStart);
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-power");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-speed");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-passes");
        Assert.Contains(result.Issues, issue => issue.Code == "settings.invalid-interval");
    }

    private static JobPreflightContext Context() => new()
    {
        IsConnected = true,
        Document = GCodeParser.Parse(["G0 X0 Y0", "G1 X100 Y100"], "test"),
        MachineStatus = Status(GrblMachineMode.Idle),
        MachineStatusAge = TimeSpan.Zero,
        RequireFraming = true,
        HasFramedCurrentDocument = true,
        WorkAreaWidthMm = 500,
        WorkAreaHeightMm = 400,
    };

    private static MachineStatus Status(GrblMachineMode mode, string? pins = null) => new()
    {
        Mode = mode,
        MachinePosition = Position.Zero,
        WorkPosition = Position.Zero,
        WorkCoordinateOffset = Position.Zero,
        TriggeredPins = pins,
    };
}

