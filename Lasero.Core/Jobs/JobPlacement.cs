using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Jobs;

public enum JobPlacementMode
{
    CurrentPosition,
    AbsoluteCoordinates,
}

public enum JobOriginAnchor
{
    TopLeft,
    Center,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Places a prepared job relative to a physical reference point chosen by the operator. The caller
/// only stores the physical point and anchor; offsets are recalculated whenever design bounds change.
/// </summary>
public sealed record JobPlacement(Position ReferencePosition, JobOriginAnchor Anchor = JobOriginAnchor.TopLeft)
{
    public (double X, double Y) CalculateOffset(BoundingBox2D bounds)
    {
        if (bounds.IsEmpty)
            throw new InvalidOperationException("Úlohu nelze umístit, protože neobsahuje žádnou dráhu.");

        var anchor = Anchor switch
        {
            JobOriginAnchor.TopLeft => (bounds.MinX, bounds.MaxY),
            JobOriginAnchor.Center => ((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2),
            JobOriginAnchor.TopRight => (bounds.MaxX, bounds.MaxY),
            JobOriginAnchor.BottomLeft => (bounds.MinX, bounds.MinY),
            JobOriginAnchor.BottomRight => (bounds.MaxX, bounds.MinY),
            _ => throw new ArgumentOutOfRangeException(nameof(Anchor)),
        };

        return (ReferencePosition.X - anchor.Item1, ReferencePosition.Y - anchor.Item2);
    }
}
