using Lasero.Core.GCode;
using Lasero.Core.Raster;
using Lasero.Tests.Golden;

namespace Lasero.Tests;

/// <summary>
/// Freezes <see cref="GrblRasterGenerator"/>'s output — the third independent G-code emitter, and
/// the only one that already sits behind a machine-independent intermediate representation
/// (<see cref="LaserJob"/>, produced by <see cref="RasterPlanner"/>). That makes this pipeline the
/// existing in-repo precedent for the planned vector LaserJob work.
///
/// Jobs are constructed directly rather than by scanning a bitmap, so these goldens are fully
/// deterministic and carry no image-decoding or file-IO dependency.
/// </summary>
public sealed class GoldenGCodeRasterTests
{
    private const double ControllerMaximumS = 1000;

    [Fact]
    public void Golden_Raster_SingleBurnRun() =>
        GoldenGCode.Verify("raster-single-burn-run", GrblRasterGenerator.Generate(
            Job(3000,
                Travel(0, 0),
                Burn(0, 0, 100),
                Burn(10, 0, 100)),
            ControllerMaximumS));

    /// <summary>Two engraved rows with a laser-off reposition between them. The M5 before every
    /// travel is the invariant that matters: the beam is never on during a rapid.</summary>
    [Fact]
    public void Golden_Raster_TwoRowsWithTravel() =>
        GoldenGCode.Verify("raster-two-rows-with-travel", GrblRasterGenerator.Generate(
            Job(3000,
                Travel(0, 0),
                Burn(0, 0, 80),
                Burn(10, 0, 80),
                Travel(0, 0.1),
                Burn(0, 0.1, 80),
                Burn(10, 0.1, 80)),
            ControllerMaximumS));

    /// <summary>Per-pixel power modulation inside one run — every G1 after the first carries its own
    /// S word, which is what makes greyscale photo engraving work.</summary>
    [Fact]
    public void Golden_Raster_VaryingPowerWithinRun() =>
        GoldenGCode.Verify("raster-varying-power-within-run", GrblRasterGenerator.Generate(
            Job(4500,
                Travel(0, 0),
                Burn(0, 0, 10),
                Burn(2, 0, 35),
                Burn(4, 0, 60),
                Burn(6, 0, 85),
                Burn(8, 0, 100)),
            ControllerMaximumS));

    [Fact]
    public void Golden_Raster_FractionalCoordinatesAndPower() =>
        GoldenGCode.Verify("raster-fractional-coordinates", GrblRasterGenerator.Generate(
            Job(1234.5678,
                Travel(1.2345, 6.7891),
                Burn(1.2345, 6.7891, 12.3456),
                Burn(9.8765, 6.7891, 12.3456)),
            ControllerMaximumS));

    [Fact]
    public void Golden_Raster_PowerScalingAgainst255Controller() =>
        GoldenGCode.Verify("raster-power-scaling-255", GrblRasterGenerator.Generate(
            Job(3000,
                Travel(0, 0),
                Burn(0, 0, 50),
                Burn(10, 0, 50)),
            255));

    /// <summary>A job with no moves at all still emits the preamble and the doubled trailing M5.</summary>
    [Fact]
    public void Golden_Raster_EmptyJob() =>
        GoldenGCode.Verify("raster-empty-job", GrblRasterGenerator.Generate(Job(3000), ControllerMaximumS));

    /// <summary>
    /// Multi-pass output, reproducing <c>RasterImporter.BuildGCode</c>'s pass loop
    /// (Lasero.Core/Import/RasterImporter.cs:57-64) exactly: the <em>entire</em> generated program,
    /// preamble and trailing M5s included, is repeated once per pass rather than only the moves.
    /// A 3-pass raster therefore sends G90/G21/M5 three times.
    /// </summary>
    [Fact]
    public void Golden_Raster_ThreePassesRepeatWholeProgram()
    {
        var pass = GrblRasterGenerator.Generate(
            Job(3000, Travel(0, 0), Burn(0, 0, 60), Burn(10, 0, 60)),
            ControllerMaximumS);

        var lines = new List<string>();
        for (var index = 0; index < 3; index++)
            lines.AddRange(pass);

        GoldenGCode.Verify("raster-three-passes", lines);
    }

    // ----------------------------------------------------------------- helpers

    private static LaserJob Job(double feedRate, params RasterMove[] moves)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var move in moves)
            bounds = bounds.Include(move.X, move.Y);

        return new LaserJob
        {
            Moves = moves,
            Bounds = moves.Length == 0 ? new BoundingBox2D(0, 0, 0, 0) : bounds,
            FeedRatePerMinute = feedRate,
        };
    }

    private static RasterMove Travel(double x, double y) =>
        new() { Kind = RasterMoveKind.Travel, X = x, Y = y };

    private static RasterMove Burn(double x, double y, double power) =>
        new() { Kind = RasterMoveKind.Burn, X = x, Y = y, Power = power };
}
