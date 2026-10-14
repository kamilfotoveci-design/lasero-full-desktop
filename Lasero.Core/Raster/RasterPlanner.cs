using Lasero.Core.GCode;

namespace Lasero.Core.Raster;

public enum RasterMoveKind { Travel, Burn }

/// <summary>One machine move in mm, laser-off (Travel) or laser-on-at-power (Burn). Pure geometry —
/// no gcode-text concerns (like avoiding repeated F words) belong here; that's GrblRasterGenerator's job.</summary>
public sealed record RasterMove
{
    public required RasterMoveKind Kind { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public double Power { get; init; }
}

/// <summary>A machine-agnostic description of what to burn: shared by framing, the canvas bounds
/// overlay, and final G-code generation, all reading from the same instance for a given import.</summary>
public sealed record LaserJob
{
    public required IReadOnlyList<RasterMove> Moves { get; init; }
    public required BoundingBox2D Bounds { get; init; }
    public required double FeedRatePerMinute { get; init; }
}

public sealed record RasterPlanOptions
{
    public required double TargetWidthMm { get; init; }

    /// <summary>Null = aspect-ratio preserved (height derived from the image's own pixel aspect).
    /// Set explicitly to allow non-uniform scaling.</summary>
    public double? TargetHeightMm { get; init; }

    public required double LineIntervalMm { get; init; }
    public double MinPower { get; init; }
    public required double MaxPower { get; init; }
    public required double FeedRatePerMinute { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public int PowerSteps { get; init; } = 10;

    /// <summary>Below this fraction a pixel is treated as pure background — never burned, regardless
    /// of MinPower (a power floor only applies to pixels already marked for burning).</summary>
    public double MinBurnFraction { get; init; } = 1e-4;
}

public static class RasterPlanner
{
    public static LaserJob Plan(ProcessedImage image, RasterPlanOptions options)
    {
        var horizontalScale = options.TargetWidthMm / image.Width;
        var heightMm = options.TargetHeightMm ?? image.Height * horizontalScale;
        var verticalScale = heightMm / image.Height;
        var rowStepPx = Math.Max(1, (int)Math.Round(options.LineIntervalMm / verticalScale));

        var moves = new List<RasterMove>();

        for (int py = 0; py < image.Height; py += rowStepPx)
        {
            // Row centre, not the pixel's top edge: the scan line runs through the middle of the
            // pixel it samples, so the engraving is not shifted half a pixel against the placement.
            var rowY = heightMm - (py + 0.5) * verticalScale + options.OffsetY;
            PlanRow(image, py, rowY, horizontalScale, options, moves);
        }

        var bounds = BoundingBox2D.Empty;
        foreach (var move in moves)
            bounds = bounds.Include(move.X, move.Y);

        return new LaserJob { Moves = moves, Bounds = bounds, FeedRatePerMinute = options.FeedRatePerMinute };
    }

    private static void PlanRow(ProcessedImage image, int py, double rowY, double scale, RasterPlanOptions options, List<RasterMove> moves)
    {
        int px = 0;
        while (px < image.Width)
        {
            if (image.At(px, py) <= options.MinBurnFraction) { px++; continue; }

            // Each Burn move after the first is a segment END edge carrying that segment's OWN power
            // (GRBL applies the S word of a G1 to the move it ends). The first Burn only arms the laser
            // at the left edge of the run's first pixel. A run [start, end) therefore burns the full
            // width of every pixel, and a single pixel gets a real one-pixel-long segment.
            var runStart = px;
            var currentPower = QuantizePower(image.At(px, py), options);
            moves.Add(new RasterMove { Kind = RasterMoveKind.Travel, X = runStart * scale + options.OffsetX, Y = rowY });
            moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = runStart * scale + options.OffsetX, Y = rowY, Power = currentPower });

            while (px < image.Width && image.At(px, py) > options.MinBurnFraction)
            {
                var power = QuantizePower(image.At(px, py), options);
                if (Math.Abs(power - currentPower) > 0.01)
                {
                    // Close the segment that ran at the old power at this pixel's left edge.
                    moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = px * scale + options.OffsetX, Y = rowY, Power = currentPower });
                    currentPower = power;
                }
                px++;
            }

            // px is now the exclusive end of the run: close the last segment at its right edge.
            moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = px * scale + options.OffsetX, Y = rowY, Power = currentPower });
        }
    }

    /// <summary>Maps a 0..1 power fraction into [MinPower, MaxPower], quantized to PowerSteps so a run
    /// emits a handful of moves rather than one per pixel.</summary>
    public static double QuantizePower(double fraction, RasterPlanOptions options)
    {
        var range = options.MaxPower - options.MinPower;
        var raw = options.MinPower + fraction * range;
        var step = options.PowerSteps > 0 ? range / options.PowerSteps : 0;
        var quantized = step <= 0 ? raw : options.MinPower + Math.Round((raw - options.MinPower) / step) * step;
        return Math.Clamp(quantized, Math.Min(options.MinPower, options.MaxPower), Math.Max(options.MinPower, options.MaxPower));
    }
}
