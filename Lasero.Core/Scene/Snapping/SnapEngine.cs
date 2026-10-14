using Lasero.Core.Grbl;

namespace Lasero.Core.Scene.Snapping;

/// <summary>
/// A candidate snap point contributed by some geometry. What that geometry IS (a node anchor, a
/// segment midpoint...) is not this file's concern — see SnapCandidateBuilder for the one existing
/// source (a VectorPath's own nodes/midpoints/self-intersections). A future line/shape-drawing tool,
/// path-continuation gesture, or object-transform drag can contribute its own candidates from its own
/// source and still resolve them through the same Resolve() below.
/// </summary>
public enum SnapKind
{
    Node,
    Endpoint,
    Midpoint,
    Intersection,
    Grid,
}

public readonly record struct SnapCandidate(Position Point, SnapKind Kind);

/// <summary>The outcome of one Resolve() call: Point is either the original query point (Target is
/// null, nothing was within tolerance) or a candidate's own Point (Target holds which candidate and
/// what kind it was, e.g. to render a snap indicator or to test "did this land on an Endpoint" for a
/// join preview).</summary>
public readonly record struct SnapResult(Position Point, SnapCandidate? Target)
{
    public bool Snapped => Target is not null;
}

public enum SnapAxis { X, Y }

/// <summary>Outcome of <see cref="SnapEngine.ResolveAxis"/>: the (possibly snapped) coordinate and
/// what it snapped to, null when nothing was within tolerance.</summary>
public readonly record struct AxisSnapResult(double Value, SnapCandidate? Target)
{
    public bool Snapped => Target is not null;
}

/// <summary>
/// Pure, WPF-free "closest candidate within tolerance" resolver — deliberately knows nothing about
/// pointer events, screen pixels, WPF, or SceneCanvas. A caller converts its own screen-space
/// tolerance to world units once (tolerance in mm = tolerance in px / current px-per-mm scale) and
/// passes a candidate list it already has ready (typically built once per drag-gesture, not
/// recomputed every pointer-move sample — see SnapCandidateBuilder's own doc comment on why that is
/// both correct and cheap). This split is what makes the same engine reusable for node editing today
/// and, later, line/shape drawing, path continuation, or object transforms: every one of those is
/// just "a different candidate source resolved through the same Resolve call".
///
/// Grid snapping is handled separately from the geometry candidate list (a regular infinite grid has
/// no finite candidate set to search) and only applies when no geometry candidate is within
/// tolerance — object/path snap points always win over the grid, the same priority every mainstream
/// vector/CAD editor uses.
/// </summary>
public static class SnapEngine
{
    // Deterministic priority used only to break a near-tie between two candidates that are (within
    // Epsilon) equally close — without this, floating-point noise between frames could make the
    // resolved target flicker between two almost-equidistant candidates as the pointer moves a
    // fraction of a pixel. Endpoint ranks above Node so an open path's end, which is usually the more
    // deliberate target (e.g. "snap onto that endpoint to close the path"), wins a genuine tie.
    private static int PriorityOf(SnapKind kind) => kind switch
    {
        SnapKind.Endpoint => 0,
        SnapKind.Node => 1,
        SnapKind.Intersection => 2,
        SnapKind.Midpoint => 3,
        SnapKind.Grid => 4,
        _ => 5,
    };

    private const double TieEpsilonSquared = 1e-9;

    public static SnapResult Resolve(
        Position point,
        IReadOnlyList<SnapCandidate> candidates,
        double toleranceWorld,
        double? gridStepMm = null)
    {
        var geometryMatch = ResolveGeometry(point, candidates, toleranceWorld);
        if (geometryMatch is { } hit) return new SnapResult(hit.Point, hit);

        if (gridStepMm is { } step && step > 1e-9)
        {
            var gridPoint = SnapToGrid(point, step);
            var dx = gridPoint.X - point.X;
            var dy = gridPoint.Y - point.Y;
            if (dx * dx + dy * dy <= toleranceWorld * toleranceWorld)
            {
                var candidate = new SnapCandidate(gridPoint, SnapKind.Grid);
                return new SnapResult(gridPoint, candidate);
            }
        }

        return new SnapResult(point, null);
    }

    /// <summary>
    /// One-axis variant of <see cref="Resolve"/> for gestures that move an edge rather than a point
    /// (resizing a box drags one edge along one axis). A geometry candidate matches when its coordinate
    /// on <paramref name="axis"/> is within tolerance, whatever its other coordinate — aligning an edge
    /// with another object's node line — and still wins over the grid. <paramref name="crossValue"/> is
    /// the edge's coordinate on the other axis; it only places the returned Grid candidate for display.
    /// </summary>
    public static AxisSnapResult ResolveAxis(
        double value,
        SnapAxis axis,
        IReadOnlyList<SnapCandidate> candidates,
        double toleranceWorld,
        double? gridStepMm = null,
        double crossValue = 0)
    {
        SnapCandidate? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(CoordinateOf(candidate.Point, axis) - value);
            if (distance > toleranceWorld) continue;
            if (best is null || distance < bestDistance - 1e-6
                || distance < bestDistance + 1e-6 && PriorityOf(candidate.Kind) < PriorityOf(best.Value.Kind))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        if (best is { } hit) return new AxisSnapResult(CoordinateOf(hit.Point, axis), hit);

        if (gridStepMm is { } step && step > 1e-9)
        {
            var snapped = Math.Round(value / step) * step;
            if (Math.Abs(snapped - value) <= toleranceWorld)
            {
                var point = axis == SnapAxis.X ? new Position(snapped, crossValue, 0) : new Position(crossValue, snapped, 0);
                return new AxisSnapResult(snapped, new SnapCandidate(point, SnapKind.Grid));
            }
        }

        return new AxisSnapResult(value, null);
    }

    private static double CoordinateOf(Position point, SnapAxis axis) => axis == SnapAxis.X ? point.X : point.Y;

    private static SnapCandidate? ResolveGeometry(
        Position point, IReadOnlyList<SnapCandidate> candidates, double toleranceWorld)
    {
        SnapCandidate? best = null;
        var bestDistSq = double.MaxValue;
        var toleranceSq = toleranceWorld * toleranceWorld;

        foreach (var candidate in candidates)
        {
            var dx = candidate.Point.X - point.X;
            var dy = candidate.Point.Y - point.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq > toleranceSq) continue;

            if (best is null || distSq < bestDistSq - TieEpsilonSquared)
            {
                best = candidate;
                bestDistSq = distSq;
            }
            else if (distSq < bestDistSq + TieEpsilonSquared && PriorityOf(candidate.Kind) < PriorityOf(best.Value.Kind))
            {
                best = candidate;
                bestDistSq = distSq;
            }
        }

        return best;
    }

    public static Position SnapToGrid(Position point, double stepMm) => new(
        Math.Round(point.X / stepMm) * stepMm,
        Math.Round(point.Y / stepMm) * stepMm,
        point.Z);
}
