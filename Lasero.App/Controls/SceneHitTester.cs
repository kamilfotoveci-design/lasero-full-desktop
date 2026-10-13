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

    /// <summary>
    /// Tests the move surface shown by the selection adorner. A single object uses its rotated local
    /// bounds; a group uses the shared world-space bounding box displayed by the canvas. This is
    /// deliberately separate from shape hit-testing: a cut-only closed contour has no fill, but once
    /// selected it must still be draggable from anywhere inside its transform frame.
    /// </summary>
    public static bool IsInsideSelectionBounds(
        IReadOnlyList<SceneObject> selectedObjects,
        Position pointerWorld)
    {
        ArgumentNullException.ThrowIfNull(selectedObjects);
        if (selectedObjects.Count == 0) return false;

        if (selectedObjects.Count == 1)
        {
            var obj = selectedObjects[0];
            var local = obj.Transform.Inverse(pointerWorld, obj.LocalPivot);
            return Contains(obj.LocalBounds, local);
        }

        var bounds = Lasero.Core.GCode.BoundingBox2D.Empty;
        foreach (var obj in selectedObjects)
            bounds = Union(bounds, obj.WorldBounds());
        return Contains(bounds, pointerWorld);
    }

    public static IReadOnlyList<SceneHitCandidate> HitTest(
        IReadOnlyList<SceneObject> objects,
        Position pointerWorld,
        double pixelsPerMillimeter,
        Func<SceneObject, ImportedShape, bool> isFilled,
        Func<SceneObject, ImportedShape, bool>? isShapeVisible = null,
        double tolerancePx = DefaultTolerancePx)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(isFilled);
        if (pixelsPerMillimeter <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerMillimeter));
        if (!double.IsFinite(tolerancePx) || tolerancePx <= 0) tolerancePx = DefaultTolerancePx;

        var toleranceMm = tolerancePx / pixelsPerMillimeter;
        var toleranceSquared = toleranceMm * toleranceMm;
        var matches = new List<SceneHitCandidate>();
        for (var objectIndex = objects.Count - 1; objectIndex >= 0; objectIndex--)
        {
            var obj = objects[objectIndex];
            // Whole-object rejection first: most objects are nowhere near the pointer, and copying
            // every point of every object into world space just to discover that is what made a click
            // on a large scene cost tens of milliseconds.
            if (!MayReach(obj, pointerWorld, toleranceMm)) continue;
            var shapes = obj.GetWorldShapes();
            var visibleShapes = new List<(ImportedShape Shape, int Index)>(shapes.Count);
            for (var shapeIndex = shapes.Count - 1; shapeIndex >= 0; shapeIndex--)
            {
                var shape = shapes[shapeIndex];
                if (shape.Points.Count < 2 || isShapeVisible?.Invoke(obj, shape) == false) continue;
                visibleShapes.Add((shape, shapeIndex));

                var bounds = BoundsOf(shape);
                if (pointerWorld.X < bounds.MinX - toleranceMm || pointerWorld.X > bounds.MaxX + toleranceMm ||
                    pointerWorld.Y < bounds.MinY - toleranceMm || pointerWorld.Y > bounds.MaxY + toleranceMm)
                    continue;

                if (IsNearStroke(shape, pointerWorld, toleranceSquared))
                {
                    matches.Add(new SceneHitCandidate(obj, shapeIndex, SceneHitKind.Stroke, objectIndex));
                }
            }

            // WPF renders one compound geometry per geometry set, layer and colour with
            // Nonzero fill. Test the same compound here: a hole must cancel its outer
            // contour instead of the outer contour independently claiming the click.
            foreach (var group in visibleShapes.GroupBy(entry =>
                         (entry.Shape.GeometrySetId, entry.Shape.LayerId, entry.Shape.LayerColor)))
            {
                var winding = 0;
                var topShapeIndex = -1;
                foreach (var (shape, shapeIndex) in group)
                {
                    if (!shape.IsClosed || shape.Points.Count < 3 || !isFilled(obj, shape)) continue;
                    winding += WindingNumber(shape.Points, pointerWorld);
                    topShapeIndex = Math.Max(topShapeIndex, shapeIndex);
                }
                if (winding != 0 && topShapeIndex >= 0)
                    matches.Add(new SceneHitCandidate(obj, topShapeIndex, SceneHitKind.Fill, objectIndex));
            }
        }

        return matches
            .OrderBy(candidate => candidate.Kind)
            .ThenByDescending(candidate => candidate.ZIndex)
            .ThenByDescending(candidate => candidate.ShapeIndex)
            .ToList();
    }

    // Bounds of the points actually stored in a shape list (not SceneObject.LocalBounds, which is a
    // separately persisted value). Keyed by the immutable list, so replacing the shapes naturally
    // invalidates it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<ImportedShape>, BoundsBox> LocalPointBounds = new();

    private sealed class BoundsBox(Lasero.Core.GCode.BoundingBox2D bounds)
    {
        public Lasero.Core.GCode.BoundingBox2D Bounds { get; } = bounds;
    }

    /// <summary>
    /// A conservative test: false only when the pointer is certainly farther than the tolerance from
    /// every point of the object. The object's local point bounds are mapped through its (affine)
    /// transform, so the axis-aligned box of the mapped corners contains every world point.
    /// </summary>
    private static bool MayReach(SceneObject obj, Position pointerWorld, double toleranceMm)
    {
        var local = LocalPointBounds.GetValue(obj.LocalShapes, shapes =>
        {
            var box = Lasero.Core.GCode.BoundingBox2D.Empty;
            foreach (var shape in shapes)
                foreach (var point in shape.Points)
                    box = box.Include(point.X, point.Y);
            return new BoundsBox(box);
        }).Bounds;
        if (local.IsEmpty) return false;

        var world = Lasero.Core.GCode.BoundingBox2D.Empty;
        foreach (var (x, y) in new[] { (local.MinX, local.MinY), (local.MaxX, local.MinY), (local.MaxX, local.MaxY), (local.MinX, local.MaxY) })
        {
            var mapped = obj.Transform.Apply(new Position(x, y, 0), obj.LocalPivot);
            world = world.Include(mapped.X, mapped.Y);
        }

        // A hair of slack absorbs floating-point differences between mapping corners and mapping points.
        var slack = toleranceMm + 1e-6 * (1 + Math.Abs(world.MaxX) + Math.Abs(world.MaxY));
        return pointerWorld.X >= world.MinX - slack && pointerWorld.X <= world.MaxX + slack &&
               pointerWorld.Y >= world.MinY - slack && pointerWorld.Y <= world.MaxY + slack;
    }

    private static Lasero.Core.GCode.BoundingBox2D BoundsOf(ImportedShape shape)
    {
        var bounds = Lasero.Core.GCode.BoundingBox2D.Empty;
        foreach (var point in shape.Points)
            bounds = bounds.Include(point.X, point.Y);
        return bounds;
    }

    private static bool Contains(Lasero.Core.GCode.BoundingBox2D bounds, Position point) =>
        !bounds.IsEmpty &&
        point.X >= bounds.MinX && point.X <= bounds.MaxX &&
        point.Y >= bounds.MinY && point.Y <= bounds.MaxY;

    private static Lasero.Core.GCode.BoundingBox2D Union(
        Lasero.Core.GCode.BoundingBox2D first,
        Lasero.Core.GCode.BoundingBox2D second)
    {
        if (first.IsEmpty) return second;
        if (second.IsEmpty) return first;
        return first
            .Include(second.MinX, second.MinY)
            .Include(second.MaxX, second.MaxY);
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

    private static int WindingNumber(IReadOnlyList<Position> polygon, Position point)
    {
        var winding = 0;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            var side = (b.X - a.X) * (point.Y - a.Y) -
                       (point.X - a.X) * (b.Y - a.Y);
            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && side > 0) winding++;
            }
            else if (b.Y <= point.Y && side < 0) winding--;
        }
        return winding;
    }

    private static bool SamePoint(Position first, Position second) =>
        Math.Abs(first.X - second.X) < 1e-9 && Math.Abs(first.Y - second.Y) < 1e-9;

    private static double Squared(double value) => value * value;
}
