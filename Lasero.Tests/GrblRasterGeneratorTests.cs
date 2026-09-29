using Lasero.Core.GCode;
using Lasero.Core.Raster;

namespace Lasero.Tests;

public class GrblRasterGeneratorTests
{
    private static readonly IReadOnlyList<RasterMove> SampleMoves =
    [
        new RasterMove { Kind = RasterMoveKind.Travel, X = 0, Y = 10 },
        new RasterMove { Kind = RasterMoveKind.Burn, X = 0, Y = 10, Power = 50 },
        new RasterMove { Kind = RasterMoveKind.Burn, X = 5, Y = 10, Power = 50 },
        new RasterMove { Kind = RasterMoveKind.Travel, X = 0, Y = 5 },
        new RasterMove { Kind = RasterMoveKind.Burn, X = 0, Y = 5, Power = 80 },
        new RasterMove { Kind = RasterMoveKind.Burn, X = 2, Y = 5, Power = 80 },
        new RasterMove { Kind = RasterMoveKind.Burn, X = 5, Y = 5, Power = 30 },
    ];

    private static LaserJob MakeJob(double feed = 1000)
    {
        var bounds = SampleMoves.Aggregate(BoundingBox2D.Empty, (b, m) => b.Include(m.X, m.Y));
        return new LaserJob { Moves = SampleMoves, Bounds = bounds, FeedRatePerMinute = feed };
    }

    [Fact]
    public void Generate_ProducesExpectedGCodeShape()
    {
        var lines = GrblRasterGenerator.Generate(MakeJob(), 100);

        Assert.Equal(new[]
        {
            "G90", "G21", "M5",
            "G0 X0 Y10",
            "M4 S50",
            "G1 X5 Y10 S50 F1000",
            "M5",
            "G0 X0 Y5",
            "M4 S80",
            "G1 X2 Y5 S80 F1000",
            "G1 X5 Y5 S30",
            "M5",
            "M5",
        }, lines);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var job = MakeJob();

        var first = GrblRasterGenerator.Generate(job, 100);
        var second = GrblRasterGenerator.Generate(job, 100);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Generate_ScalesPercentPowerToReportedControllerMaximum()
    {
        var lines = GrblRasterGenerator.Generate(MakeJob(), 1000);

        Assert.Contains("M4 S500", lines);
        Assert.Contains("G1 X5 Y10 S500 F1000", lines);
        Assert.Contains("M4 S800", lines);
        Assert.Contains("G1 X5 Y5 S300", lines);
    }

    [Fact]
    public void Generate_RoundTripsThroughGCodeParserContainingTheSameGeometry()
    {
        // GCodeParser assumes the machine starts at (0,0,0) before the first line, so its bounding
        // box can be a strict superset of LaserJob.Bounds (pure move geometry) whenever the job
        // itself never visits the origin — as here. It must never be smaller: every point the
        // planner actually computed has to still be reachable by walking the generated G-code.
        var job = MakeJob();

        var lines = GrblRasterGenerator.Generate(job, 100);
        var parsed = GCodeParser.Parse(lines);

        Assert.True(parsed.BoundingBox.MinX <= job.Bounds.MinX);
        Assert.True(parsed.BoundingBox.MinY <= job.Bounds.MinY);
        Assert.True(parsed.BoundingBox.MaxX >= job.Bounds.MaxX);
        Assert.True(parsed.BoundingBox.MaxY >= job.Bounds.MaxY);
    }
}
