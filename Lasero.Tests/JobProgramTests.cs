using Lasero.Core.Jobs;
using Lasero.Tests.Golden;

namespace Lasero.Tests;

/// <summary>
/// The device-level lines wrapped around a streamed job: air assist (M8/M7 ... M9) and the return move.
/// These lines reach the machine, so the exact sequence is frozen here and in Golden/gcode/job-program-*.
/// The wrapper only ever adds laser-off commands; the tests also pin that no added line can enable the laser.
/// </summary>
public sealed class JobProgramTests
{
    private static readonly string[] Job =
    [
        "G90", "G21", "M5",
        "G0 X0 Y0", "M4 S500", "G1 X10 Y0 F1000", "G1 X10 Y10 F1000", "M5",
        "M5",
    ];

    [Fact]
    public void DefaultOptionsLeaveTheJobUntouched()
    {
        var program = JobProgram.Wrap(Job, new JobProgramOptions());

        Assert.Equal(Job, program.Lines);
        Assert.Equal(0, program.PrologueCount);
        Assert.Equal(0, program.EpilogueCount);
        Assert.Equal(Job.Length, program.JobLineCount);
        Assert.True(new JobProgramOptions().IsEmpty);
    }

    [Fact]
    public void AirAssistM8OpensAndClosesTheJob()
    {
        var program = JobProgram.Wrap(Job, new JobProgramOptions { AirAssist = AirAssistMode.M8 });

        Assert.Equal("M8", program.Lines[0]);
        Assert.Equal(["M5", "M9"], program.Lines.TakeLast(2));
        Assert.Equal(1, program.PrologueCount);
        Assert.Equal(2, program.EpilogueCount);
        Assert.Equal(Job, program.Lines.Skip(1).Take(Job.Length));
        GoldenGCode.Verify("job-program-air-m8", program.Lines);
    }

    [Fact]
    public void AirAssistM7UsesTheMistOutput()
    {
        var program = JobProgram.Wrap(Job, new JobProgramOptions { AirAssist = AirAssistMode.M7 });

        Assert.Equal("M7", program.Lines[0]);
        Assert.DoesNotContain("M8", program.Lines);
        Assert.Equal("M9", program.Lines[^1]);
    }

    [Fact]
    public void ReturnToStartTravelsAfterTheLaserIsOffInAbsoluteMode()
    {
        var program = JobProgram.Wrap(Job, new JobProgramOptions { ReturnTo = (0, 0) });

        Assert.Equal(["M5", "G90", "G0 X0 Y0"], program.Lines.TakeLast(3));
        Assert.Equal(0, program.PrologueCount);
        Assert.Equal(3, program.EpilogueCount);
        GoldenGCode.Verify("job-program-return-origin", program.Lines);
    }

    [Fact]
    public void AirAssistAndReturnTogetherSwitchTheAirOffBeforeTravelling()
    {
        var program = JobProgram.Wrap(Job, new JobProgramOptions
        {
            AirAssist = AirAssistMode.M7,
            ReturnTo = (123.456, 78.9),
        });

        Assert.Equal(["M5", "M9", "G90", "G0 X123.456 Y78.9"], program.Lines.TakeLast(4));
        GoldenGCode.Verify("job-program-air-m7-return-reference", program.Lines);
    }

    [Fact]
    public void NumbersAreWrittenWithTheInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("cs-CZ");
        try
        {
            var epilogue = JobProgram.Epilogue(new JobProgramOptions { ReturnTo = (1.5, 2.25) });
            Assert.Equal("G0 X1.5 Y2.25", epilogue[^1]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(AirAssistMode.Off, false)]
    [InlineData(AirAssistMode.M8, true)]
    [InlineData(AirAssistMode.M7, true)]
    public void NothingTheWrapperAddsEnablesTheLaserOrMovesWithItOn(AirAssistMode air, bool expectsAirLines)
    {
        var options = new JobProgramOptions { AirAssist = air, ReturnTo = (5, 6) };
        var added = JobProgram.Prologue(options).Concat(JobProgram.Epilogue(options)).ToList();

        Assert.Equal(expectsAirLines, added.Any(line => line is "M7" or "M8" or "M9"));
        Assert.DoesNotContain(added, line => line.StartsWith("M3", StringComparison.Ordinal) || line.StartsWith("M4", StringComparison.Ordinal));
        // The only motion is one G0, and the M5 before it switches the laser off.
        var move = added.FindIndex(line => line.StartsWith("G0 ", StringComparison.Ordinal));
        Assert.True(move > 0);
        Assert.Contains("M5", added.Take(move));
        Assert.Single(added, line => line.StartsWith("G0", StringComparison.Ordinal) || line.StartsWith("G1", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonFiniteReturnTargetIsRefusedInsteadOfStreamed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobProgram.Epilogue(new JobProgramOptions { ReturnTo = (double.NaN, 0) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobProgram.Wrap(Job, new JobProgramOptions { ReturnTo = (0, double.PositiveInfinity) }));
    }

    [Fact]
    public void TheWrapperDoesNotMutateTheJobItWraps()
    {
        var copy = Job.ToList();
        JobProgram.Wrap(copy, new JobProgramOptions { AirAssist = AirAssistMode.M8, ReturnTo = (0, 0) });
        Assert.Equal(Job, copy);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(25, 10)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void FramingPowerIsClampedToAVisibleButHarmlessDot(double stored, double expected)
    {
        Assert.Equal(expected, FramingOptions.ClampVisiblePower(stored));
    }

    [Fact]
    public void ConfiguredFramingPowerReachesTheFramingGCode()
    {
        var box = new Lasero.Core.GCode.BoundingBox2D(0, 0, 40, 20);
        var lines = FramingService.BuildFrameGCode(
            box, new FramingOptions { LaserPower = 4 }, controllerMaximumS: 1000);
        Assert.Contains("M3 S40", lines);

        var off = FramingService.BuildFrameGCode(
            box, new FramingOptions { LaserPower = 0 }, controllerMaximumS: 1000);
        Assert.DoesNotContain(off, line => line.StartsWith("M3", StringComparison.Ordinal));
    }
}
