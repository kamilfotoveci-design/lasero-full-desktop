using Lasero.App.Controls;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// SceneHitTester rejects whole objects by bounds before it materialises any world points. These
/// tests pin that the early rejection never changes which candidates come back: on random scenes
/// (rotated, scaled, flipped, hidden-layer, filled and stroke-only objects) the result is identical,
/// candidate for candidate and in the same order, to the straightforward scan over every object.
/// </summary>
public sealed class SceneHitTesterEquivalenceTests
{
    private static readonly RgbColor Red = new(255, 0, 0);

    private static SceneObject RandomObject(Random random, int index)
    {
        var x = random.NextDouble() * 300;
        var y = random.NextDouble() * 300;
        var w = 5 + random.NextDouble() * 60;
        var h = 5 + random.NextDouble() * 60;
        var obj = (index % 3) switch
        {
            0 => ScenePrimitiveFactory.CreateRectangle(new Position(x, y, 0), new Position(x + w, y + h, 0), Red, $"r{index}"),
            1 => ScenePrimitiveFactory.CreateEllipse(new Position(x, y, 0), new Position(x + w, y + h, 0), Red, $"e{index}"),
            _ => ScenePrimitiveFactory.CreateStar(new Position(x, y, 0), new Position(x + w, y + h, 0), 5, 0.4, Red, $"s{index}"),
        };
        obj.Transform = new ObjectTransform(
            random.NextDouble() * 40 - 20, random.NextDouble() * 40 - 20,
            random.NextDouble() * 360,
            (random.NextDouble() < 0.2 ? -1 : 1) * (0.3 + random.NextDouble() * 2),
            (random.NextDouble() < 0.2 ? -1 : 1) * (0.3 + random.NextDouble() * 2));
        return obj;
    }

    /// <summary>The previous implementation, verbatim in behaviour: scan every object.</summary>
    private static List<SceneHitCandidate> Reference(
        IReadOnlyList<SceneObject> objects, Position pointer, double pixelsPerMm,
        Func<SceneObject, ImportedShape, bool> isFilled, Func<SceneObject, ImportedShape, bool> isVisible)
    {
        var tolerance = SceneHitTester.DefaultTolerancePx / pixelsPerMm;
        var toleranceSquared = tolerance * tolerance;
        var matches = new List<SceneHitCandidate>();
        for (var objectIndex = objects.Count - 1; objectIndex >= 0; objectIndex--)
        {
            var obj = objects[objectIndex];
            var shapes = obj.GetWorldShapes();
            for (var shapeIndex = shapes.Count - 1; shapeIndex >= 0; shapeIndex--)
            {
                var shape = shapes[shapeIndex];
                if (shape.Points.Count < 2 || !isVisible(obj, shape)) continue;
                var bounds = BoundingBox2D.Empty;
                foreach (var point in shape.Points) bounds = bounds.Include(point.X, point.Y);
                if (pointer.X < bounds.MinX - tolerance || pointer.X > bounds.MaxX + tolerance ||
                    pointer.Y < bounds.MinY - tolerance || pointer.Y > bounds.MaxY + tolerance)
                    continue;

                var near = false;
                for (var i = 1; i < shape.Points.Count && !near; i++)
                    near = Distance2(pointer, shape.Points[i - 1], shape.Points[i]) <= toleranceSquared;
                if (!near && shape.IsClosed &&
                    (Math.Abs(shape.Points[0].X - shape.Points[^1].X) >= 1e-9 || Math.Abs(shape.Points[0].Y - shape.Points[^1].Y) >= 1e-9))
                    near = Distance2(pointer, shape.Points[^1], shape.Points[0]) <= toleranceSquared;
                if (near)
                {
                    matches.Add(new SceneHitCandidate(obj, shapeIndex, SceneHitKind.Stroke, objectIndex));
                    continue;
                }

                if (shape.IsClosed && isFilled(obj, shape) && Inside(shape.Points, pointer))
                    matches.Add(new SceneHitCandidate(obj, shapeIndex, SceneHitKind.Fill, objectIndex));
            }
        }

        return matches.OrderBy(c => c.Kind).ThenByDescending(c => c.ZIndex).ThenByDescending(c => c.ShapeIndex).ToList();
    }

    private static double Distance2(Position p, Position a, Position b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
    }

    private static bool Inside(IReadOnlyList<Position> polygon, Position point)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    [Fact]
    public void BoundsRejectionReturnsExactlyTheCandidatesOfAFullScanOnRandomScenes()
    {
        var random = new Random(1234);
        var checkedHits = 0;
        for (var scene = 0; scene < 12; scene++)
        {
            var objects = Enumerable.Range(0, 40 + scene * 10).Select(i => RandomObject(random, i)).ToList();
            Func<SceneObject, ImportedShape, bool> isFilled = (o, _) => o.Name.StartsWith('r') || o.Name.StartsWith('e');
            Func<SceneObject, ImportedShape, bool> isVisible = (o, _) => !o.Name.EndsWith('7');

            for (var probe = 0; probe < 250; probe++)
            {
                var pointer = new Position(random.NextDouble() * 400 - 10, random.NextDouble() * 400 - 10, 0);
                var pixelsPerMm = 0.2 + random.NextDouble() * 12;

                var expected = Reference(objects, pointer, pixelsPerMm, isFilled, isVisible);
                var actual = SceneHitTester.HitTest(objects, pointer, pixelsPerMm, isFilled, isVisible);

                Assert.Equal(expected.Count, actual.Count);
                for (var i = 0; i < expected.Count; i++)
                {
                    Assert.Same(expected[i].Object, actual[i].Object);
                    Assert.Equal(expected[i].ShapeIndex, actual[i].ShapeIndex);
                    Assert.Equal(expected[i].Kind, actual[i].Kind);
                    Assert.Equal(expected[i].ZIndex, actual[i].ZIndex);
                }

                checkedHits += expected.Count;
            }
        }

        Assert.True(checkedHits > 500, "the random scenes must actually produce hits, or the comparison proves nothing");
    }

    [Fact]
    public void ObjectBoundsCacheFollowsReplacedShapes()
    {
        var obj = RandomObject(new Random(3), 0);
        obj.Transform = ObjectTransform.Identity;
        var before = SceneHitTester.HitTest([obj], new Position(obj.LocalBounds.MinX, obj.LocalBounds.MinY, 0), 4, (_, _) => false);
        Assert.NotEmpty(before);

        // Replace the geometry with a shape far away: the cached bounds of the old list must not be reused.
        obj.RestoreLocalShapes([new ImportedShape
        {
            Points = [new Position(1000, 1000, 0), new Position(1010, 1000, 0), new Position(1010, 1010, 0), new Position(1000, 1000, 0)],
            IsClosed = true, LayerColor = Red, PreferredMode = LayerMode.Cut,
        }]);

        Assert.Empty(SceneHitTester.HitTest([obj], new Position(obj.LocalBounds.MinX, obj.LocalBounds.MinY, 0), 4, (_, _) => false));
        Assert.NotEmpty(SceneHitTester.HitTest([obj], new Position(1005, 1000, 0), 4, (_, _) => false));
    }
}
