using Lasero.Core.GCode;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;

namespace Lasero.Tests;

public sealed class FillToolpathTests
{
    private static readonly RgbColor Black = new(0, 0, 0);

    private static ImportedDocument SquareDocument(double size, LayerSettings layer)
    {
        var square = new ImportedShape
        {
            Points =
            [
                new(0, 0, 0),
                new(size, 0, 0),
                new(size, size, 0),
                new(0, size, 0),
            ],
            IsClosed = true,
            LayerColor = Black,
            LayerId = layer.Id,
            PreferredMode = layer.Mode,
        };

        return new ImportedDocument
        {
            Shapes = [square],
            Layers = [layer],
            BoundingBox = new BoundingBox2D(0, 0, size, size),
        };
    }

    private static LayerSettings FillLayer(double intervalMm) => new()
    {
        Color = Black,
        Name = "Fill",
        Mode = LayerMode.Fill,
        Speed = 3000,
        Power = 30,
        Passes = 1,
        FillLineIntervalMm = intervalMm,
    };

    [Fact]
    public void FillAlternatesScanDirectionSoTheHeadDoesNotRaceBackEveryLine()
    {
        var document = SquareDocument(10, FillLayer(1));

        var lines = ToolpathBuilder.BuildGCode(document, 100);
        var parsed = GCodeParser.Parse(lines, "fill");

        var rapid = parsed.Segments.Where(s => s.IsRapid).Sum(JobTimeEstimatorProbe.Length);
        var cut = parsed.Segments.Where(s => !s.IsRapid).Sum(JobTimeEstimatorProbe.Length);

        // Before serpentine every 10mm engraved line was followed by a 10mm rapid back to the left
        // margin, so repositioning travel matched engraving travel almost exactly. Connecting the
        // lines end-to-end leaves only the short step between rows.
        Assert.True(rapid < cut / 2,
            $"Fill should not spend comparable travel repositioning: cut={cut:N1}mm rapid={rapid:N1}mm");
    }

    [Fact]
    public void FillStillEngravesEveryScanlineAndKeepsTheLaserOffBetweenThem()
    {
        var document = SquareDocument(10, FillLayer(1));

        var lines = ToolpathBuilder.BuildGCode(document, 100);

        // Reordering the runs must not change how many there are, nor the M4/M5 bracketing that
        // keeps the beam off while repositioning.
        var engraveMoves = lines.Count(line => line.StartsWith("G1", StringComparison.Ordinal));
        var laserOn = lines.Count(line => line.StartsWith("M4", StringComparison.Ordinal));
        var laserOff = lines.Count(line => line.StartsWith("M5", StringComparison.Ordinal));

        Assert.Equal(10, engraveMoves);
        Assert.Equal(engraveMoves, laserOn);
        // Two more M5 than M4: the builder also switches the beam off in the preamble and again at
        // the end of the file, so an interrupted or replayed job never starts or ends beam-on.
        Assert.Equal(engraveMoves + 2, laserOff);
        Assert.Equal("M5", lines[2]);
        Assert.Equal("M5", lines[^1]);

        // The invariant that matters: the beam is never on during a repositioning move. Tracking the
        // M4/M5 state is the honest way to check it — looking only at the immediately preceding line
        // would be defeated by the comment lines the builder writes between operations.
        var beamOn = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("M4", StringComparison.Ordinal)) beamOn = true;
            else if (line.StartsWith("M5", StringComparison.Ordinal)) beamOn = false;
            else if (line.StartsWith("G0", StringComparison.Ordinal))
                Assert.False(beamOn, $"Rapid at line {i} happens with the beam on: {line}");
        }
    }

    [Fact]
    public void SwitchingCutToFillMovesUntouchedSpeedAndPowerToTheFillDefaults()
    {
        var layer = LayerSettings.CreateDefault(Black, LayerMode.Cut, "Vektor");
        Assert.Equal(350, layer.Speed);

        layer.Mode = LayerMode.Fill;

        Assert.Equal(3000, layer.Speed);
        Assert.Equal(30, layer.Power);
    }

    [Fact]
    public void VectorPowerPercentScalesToReportedControllerMaximum()
    {
        var document = SquareDocument(10, FillLayer(1));

        var lines = ToolpathBuilder.BuildGCode(document, 1000);

        Assert.Contains("M4 S300", lines);
    }

    [Fact]
    public void SwitchingModeLeavesSpeedAndPowerTheOperatorTypedInAlone()
    {
        var layer = LayerSettings.CreateDefault(Black, LayerMode.Cut, "Vektor");
        layer.Speed = 500;
        layer.Power = 80;

        layer.Mode = LayerMode.Fill;

        Assert.Equal(500, layer.Speed);
        Assert.Equal(80, layer.Power);
    }
}

internal static class JobTimeEstimatorProbe
{
    public static double Length(GCodeSegment segment)
    {
        var dx = segment.End.X - segment.Start.X;
        var dy = segment.End.Y - segment.Start.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
