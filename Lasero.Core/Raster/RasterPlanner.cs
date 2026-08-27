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
            var rowY = heightMm - py * verticalScale + options.OffsetY;
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

            var runStart = px;
            var currentPower = QuantizePower(image.At(px, py), options);
            moves.Add(new RasterMove { Kind = RasterMoveKind.Travel, X = runStart * scale + options.OffsetX, Y = rowY });
            moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = runStart * scale + options.OffsetX, Y = rowY, Power = currentPower });

            while (px < image.Width && image.At(px, py) > options.MinBurnFraction)
            {
                var power = QuantizePower(image.At(px, py), options);
                if (Math.Abs(power - currentPower) > 0.01)
                {
                    currentPower = power;
                    moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = px * scale + options.OffsetX, Y = rowY, Power = currentPower });
                }
                px++;
            }

            // Always add a move for the run's last pixel — even if it duplicates the previous point —
            // so GrblRasterGenerator always has at least one point to hang a feed-rate word on.
            var lastX = (px - 1) * scale + options.OffsetX;
            moves.Add(new RasterMove { Kind = RasterMoveKind.Burn, X = lastX, Y = rowY, Power = currentPower });
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
