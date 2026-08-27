using Lasero.Core.GCode;
using Lasero.Core.Raster;

namespace Lasero.Tests;

public class RasterPlannerTests
{
    private static ProcessedImage FullBurnImage(int width, int height) => new()
    {
        Width = width,
        Height = height,
        PowerFraction = Enumerable.Repeat(1.0, width * height).ToList(),
    };

    [Fact]
    public void Plan_ScalesBitmapToPhysicalWidthAndDerivesHeightFromAspectRatio()
    {
        var image = FullBurnImage(2, 2);
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 10,
            LineIntervalMm = 5,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };

        var job = RasterPlanner.Plan(image, options);

        // scale = 10/2 = 5mm/px; aspect-preserved height = 2px * 5 = 10mm.
        // Row 0 samples at Y=10 (top), row 1 at Y=5 — burn X spans 0..5 (pixel 0 and pixel 1 positions).
        Assert.Equal(new BoundingBox2D(0, 5, 5, 10), job.Bounds);
    }

    [Fact]
    public void Plan_ExplicitHeightOverridesAspectRatioForNonUniformScaling()
    {
        var image = FullBurnImage(2, 2);
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 10,
            TargetHeightMm = 20, // aspect-preserved would be 10mm — this deliberately distorts it
            LineIntervalMm = 10,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };

        var job = RasterPlanner.Plan(image, options);

        Assert.Equal(10, job.Bounds.MinY);
        Assert.Equal(20, job.Bounds.MaxY);
    }

    [Fact]
    public void Plan_LineIntervalControlsRowSamplingStep()
    {
        var image = FullBurnImage(1, 10);
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 1, // scale = 1mm/px, so LineIntervalMm maps 1:1 to row step in pixels
            LineIntervalMm = 3,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };

        var job = RasterPlanner.Plan(image, options);

        var sampledRowYs = job.Moves.Select(m => m.Y).Distinct().OrderByDescending(y => y).ToList();
        // py = 0, 3, 6, 9 -> 4 distinct rows sampled out of 10.
        Assert.Equal(4, sampledRowYs.Count);
    }

    [Fact]
    public void Plan_GeneratesTravelThenBurnMovesInRowOrder()
    {
        var image = FullBurnImage(2, 2);
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 10,
            LineIntervalMm = 5,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };

        var job = RasterPlanner.Plan(image, options);

        Assert.Equal(6, job.Moves.Count);
        Assert.Equal(RasterMoveKind.Travel, job.Moves[0].Kind);
        Assert.Equal(RasterMoveKind.Burn, job.Moves[1].Kind);
        Assert.Equal(RasterMoveKind.Burn, job.Moves[2].Kind);
        Assert.Equal(RasterMoveKind.Travel, job.Moves[3].Kind);
        // Top row (Y=10) is emitted before the bottom row (Y=5) — image row 0 is the top.
        Assert.Equal(10, job.Moves[0].Y);
        Assert.Equal(5, job.Moves[3].Y);
    }

    [Fact]
    public void Plan_OffsetTranslatesTheWholeJob()
    {
        var image = FullBurnImage(2, 2);
        var baseOptions = new RasterPlanOptions
        {
            TargetWidthMm = 10,
            LineIntervalMm = 5,
            MaxPower = 100,
            FeedRatePerMinute = 1000,
        };

        var origin = RasterPlanner.Plan(image, baseOptions).Bounds;
        var offset = RasterPlanner.Plan(image, baseOptions with { OffsetX = 100, OffsetY = 50 }).Bounds;

        Assert.Equal(origin.MinX + 100, offset.MinX);
        Assert.Equal(origin.MinY + 50, offset.MinY);
    }

    [Theory]
    [InlineData(1.0, 0, 100, 100)]
    [InlineData(0.0, 0, 100, 0)]
    [InlineData(0.5, 0, 100, 50)]
    [InlineData(1.0, 20, 80, 80)]
    [InlineData(-0.5, 10, 50, 10)]   // out-of-range fraction clamps to MinPower
    [InlineData(2.0, 10, 50, 50)]    // out-of-range fraction clamps to MaxPower
    public void QuantizePower_MapsAndClampsIntoMinMaxRange(double fraction, double min, double max, double expected)
    {
        var options = new RasterPlanOptions
        {
            TargetWidthMm = 1,
            LineIntervalMm = 1,
            MinPower = min,
            MaxPower = max,
            FeedRatePerMinute = 1000,
            PowerSteps = 100, // fine granularity so simple fractions like 0.5 land exactly, not on a rounding boundary
        };

        Assert.Equal(expected, RasterPlanner.QuantizePower(fraction, options), precision: 6);
    }
}
