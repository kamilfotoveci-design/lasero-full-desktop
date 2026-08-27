using Lasero.Core.GCode;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

public sealed class JobSimulationTests
{
    private static readonly MachineMotionProfile ExactProfile = new()
    {
        RapidSpeedMmPerMinute = 120,
        DefaultWorkSpeedMmPerMinute = 60,
        CommandOverhead = TimeSpan.Zero,
    };

    [Fact]
    public void EstimateUsesFeedRateFromTheExecutedToolpath()
    {
        var document = GCodeParser.Parse(["G1 X60 Y0 F60"], "one-minute-line");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);
        Assert.Equal(TimeSpan.FromMinutes(1), estimate.Duration);
    }

    [Fact]
    public void ScrubbingToHalfTimeReturnsHalfwayPositionAndProgress()
    {
        var document = GCodeParser.Parse(["G1 X60 Y0 F60"], "one-minute-line");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);
        var state = JobSimulator.GetStateAtTime(document, estimate, TimeSpan.FromSeconds(30));

        Assert.Equal(30, state.LaserPosition.X, 6);
        Assert.Equal(0, state.LaserPosition.Y, 6);
        Assert.Equal(0.5, state.Progress, 6);
        Assert.Equal(0, state.CompletedSegments);
    }

    [Fact]
    public void SimulationClampsPastTheEndAndReportsCompletedToolpath()
    {
        var document = GCodeParser.Parse(["G1 X60 Y0 F60"], "one-minute-line");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);
        var state = JobSimulator.GetStateAtTime(document, estimate, TimeSpan.FromHours(1));

        Assert.Equal(document.Segments.Count, state.CompletedSegments);
        Assert.Equal(1, state.Progress);
        Assert.Equal(document.Segments[^1].End, state.LaserPosition);
        Assert.Equal(estimate.Duration, state.Elapsed);
    }
}
