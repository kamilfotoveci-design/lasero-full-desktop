using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

internal enum SceneHitKind
{
    Stroke,
    Fill,
}

internal sealed record SceneHitCandidate(SceneObject Object, int ShapeIndex, SceneHitKind Kind, int ZIndex);

/// <summary>
/// Resolves direct pointer selection independently from WPF element hit-testing. The canvas supplies
/// layer fill semantics and visibility; this type owns broad/precise geometry selection only.
/// </summary>
internal static class SceneHitTester
{
    internal const double DefaultTolerancePx = 6;

    public static IReadOnlyList<SceneHitCandidate> HitTest(
        IReadOnlyList<SceneObject> objects,
        Position pointerWorld,
        double pixelsPerMillimeter,
        Func<SceneObject, ImportedShape, bool> isFilled,
        Func<SceneObject, ImportedShape, bool>? isShapeVisible = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(isFilled);
        if (pixelsPerMillimeter <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerMillimeter));

        var toleranceMm = DefaultTolerancePx / pixelsPerMillimeter;
        var toleranceSquared = toleranceMm * toleranceMm;
        var matches = new List<SceneHitCandidate>();
        for (var objectIndex = objects.Count - 1; objectIndex >= 0; objectIndex--)
        {
            var obj = objects[objectIndex];
            var shapes = obj.GetWorldShapes();
            for (var shapeIndex = shapes.Count - 1; shapeIndex >= 0; shapeIndex--)
            {
                var shape = shapes[shapeIndex];
                if (shape.Points.Count < 2 || isShapeVisible?.Invoke(obj, shape) == false) continue;

                var bounds = BoundsOf(shape);
                if (pointerWorld.X < bounds.MinX - toleranceMm || pointerWorld.X > bounds.MaxX + toleranceMm ||
                    pointerWorld.Y < bounds.MinY - toleranceMm || pointerWorld.Y > bounds.MaxY + toleranceMm)
                    continue;

                if (IsNearStroke(shape, pointerWorld, toleranceSquared))
                {
                    matches.Add(new SceneHitCandidate(obj, shapeIndex, SceneHitKind.Stroke, objectIndex));
                    continue;
                }

                if (shape.IsClosed && isFilled(obj, shape) && ContainsPoint(shape.Points, pointerWorld))
                    matches.Add(new SceneHitCandidate(obj, shapeIndex, SceneHitKind.Fill, objectIndex));
            }
        }

        return matches
            .OrderBy(candidate => candidate.Kind)
            .ThenByDescending(candidate => candidate.ZIndex)
            .ThenByDescending(candidate => candidate.ShapeIndex)
            .ToList();
    }

    private static Lasero.Core.GCode.BoundingBox2D BoundsOf(ImportedShape shape)
    {
        var bounds = Lasero.Core.GCode.BoundingBox2D.Empty;
        foreach (var point in shape.Points)
            bounds = bounds.Include(point.X, point.Y);
        return bounds;
    }

    private static bool IsNearStroke(ImportedShape shape, Position pointer, double toleranceSquared)
    {
        for (var index = 1; index < shape.Points.Count; index++)
        {
            if (DistanceSquaredToSegment(pointer, shape.Points[index - 1], shape.Points[index]) <= toleranceSquared)
                return true;
        }

        if (shape.IsClosed && !SamePoint(shape.Points[0], shape.Points[^1]) &&
            DistanceSquaredToSegment(pointer, shape.Points[^1], shape.Points[0]) <= toleranceSquared)
            return true;

        return false;
    }

    private static double DistanceSquaredToSegment(Position point, Position start, Position end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12)
            return Squared(point.X - start.X) + Squared(point.Y - start.Y);

        var projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy);
        projection = Math.Clamp(projection, 0, 1);
        var closestX = start.X + projection * dx;
        var closestY = start.Y + projection * dy;
        return Squared(point.X - closestX) + Squared(point.Y - closestY);
    }

    private static bool ContainsPoint(IReadOnlyList<Position> polygon, Position point)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            var crosses = (a.Y > point.Y) != (b.Y > point.Y) &&
                          point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool SamePoint(Position first, Position second) =>
        Math.Abs(first.X - second.X) < 1e-9 && Math.Abs(first.Y - second.Y) < 1e-9;

    private static double Squared(double value) => value * value;
}
