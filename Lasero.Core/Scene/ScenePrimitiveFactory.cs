using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>Creates editable vector objects in millimetres from canvas gestures.</summary>
public static class ScenePrimitiveFactory
{
    public static SceneObject CreateRectangle(Position start, Position end, RgbColor color, string name)
    {
        var bounds = NormalizeBounds(start, end);
        var points = new[]
        {
            new Position(bounds.MinX, bounds.MinY, 0),
            new Position(bounds.MaxX, bounds.MinY, 0),
            new Position(bounds.MaxX, bounds.MaxY, 0),
            new Position(bounds.MinX, bounds.MaxY, 0),
            new Position(bounds.MinX, bounds.MinY, 0),
        };

        return CreateObject(points, isClosed: true, bounds, color, name);
    }

    public static SceneObject CreateEllipse(Position start, Position end, RgbColor color, string name)
    {
        const int segments = 64;
        var bounds = NormalizeBounds(start, end);
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var radiusX = bounds.Width / 2;
        var radiusY = bounds.Height / 2;
        var points = Enumerable.Range(0, segments + 1)
            .Select(index =>
            {
                var angle = 2 * Math.PI * index / segments;
                return new Position(centerX + radiusX * Math.Cos(angle), centerY + radiusY * Math.Sin(angle), 0);
            })
            .ToArray();

        return CreateObject(points, isClosed: true, bounds, color, name);
    }

    public static SceneObject CreateLine(Position start, Position end, RgbColor color, string name)
    {
        var points = new[] { start with { Z = 0 }, end with { Z = 0 } };
        return CreateObject(points, isClosed: false, BoundsFromPoints(points), color, name);
    }

    public static SceneObject CreatePolygon(Position start, Position end, int sides, RgbColor color, string name)
    {
        if (sides < 3) throw new ArgumentOutOfRangeException(nameof(sides));
        var points = CreateRadialPoints(start, end, sides, innerRatio: null);
        return CreateObject(points, isClosed: true, BoundsFromPoints(points), color, name);
    }

    public static SceneObject CreateStar(Position start, Position end, int points, double innerRatio, RgbColor color, string name)
    {
        if (points < 3) throw new ArgumentOutOfRangeException(nameof(points));
        if (innerRatio is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(innerRatio));
        var vertices = CreateRadialPoints(start, end, points, innerRatio);
        return CreateObject(vertices, isClosed: true, BoundsFromPoints(vertices), color, name);
    }

    private static IReadOnlyList<Position> CreateRadialPoints(Position start, Position end, int points, double? innerRatio)
    {
        var bounds = NormalizeBounds(start, end);
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var radiusX = bounds.Width / 2;
        var radiusY = bounds.Height / 2;
        var vertexCount = innerRatio.HasValue ? points * 2 : points;
        var vertices = new List<Position>(vertexCount + 1);

        for (var index = 0; index < vertexCount; index++)
        {
            var angle = Math.PI / 2 + 2 * Math.PI * index / vertexCount;
            var ratio = innerRatio.HasValue && index % 2 == 1 ? innerRatio.Value : 1;
            vertices.Add(new Position(
                centerX + radiusX * ratio * Math.Cos(angle),
                centerY + radiusY * ratio * Math.Sin(angle),
                0));
        }

        vertices.Add(vertices[0]);
        return vertices;
    }

    private static SceneObject CreateObject(
        IReadOnlyList<Position> points,
        bool isClosed,
        BoundingBox2D bounds,
        RgbColor color,
        string name)
    {
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var localPoints = points
            .Select(point => new Position(point.X - centerX, point.Y - centerY, 0))
            .ToArray();
        var localBounds = new BoundingBox2D(
            bounds.MinX - centerX,
            bounds.MinY - centerY,
            bounds.MaxX - centerX,
            bounds.MaxY - centerY);

        return new SceneObject
        {
            Name = name,
            LocalShapes =
            [
                new ImportedShape
                {
                    Points = localPoints,
                    IsClosed = isClosed,
                    LayerColor = color,
                    PreferredMode = LayerMode.Cut,
                },
            ],
            LocalPivot = Position.Zero,
            LocalBounds = localBounds,
            Transform = ObjectTransform.Identity with { X = centerX, Y = centerY },
        };
    }

    private static BoundingBox2D NormalizeBounds(Position start, Position end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Max(start.X, end.X),
        Math.Max(start.Y, end.Y));

    private static BoundingBox2D BoundsFromPoints(IEnumerable<Position> points)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in points)
            bounds = bounds.Include(point.X, point.Y);
        return bounds;
    }
}
