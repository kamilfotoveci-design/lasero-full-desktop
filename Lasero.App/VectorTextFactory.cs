using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.App;

/// <summary>Converts installed Windows fonts to flattened millimetre vector contours.</summary>
[SupportedOSPlatform("windows")]
public static class VectorTextFactory
{
    private const double DipsPerMillimetre = 96.0 / 25.4;

    public static SceneObject Create(string text, Position origin, double heightMm, RgbColor color)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text nesmí být prázdný.", nameof(text));
        if (!double.IsFinite(heightMm) || heightMm is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(heightMm), "Výška textu musí být mezi 1 a 200 mm.");

        var formatted = new FormattedText(
            text.Trim(),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            heightMm * DipsPerMillimetre,
            Brushes.Black,
            1);

        var flattened = formatted.BuildGeometry(new Point(0, 0))
            .GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        var shapes = new List<ImportedShape>();
        var bounds = BoundingBox2D.Empty;

        foreach (var figure in flattened.Figures)
        {
            var points = new List<Position> { ToWorld(figure.StartPoint, origin) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment polyLine:
                        points.AddRange(polyLine.Points.Select(point => ToWorld(point, origin)));
                        break;
                    case LineSegment line:
                        points.Add(ToWorld(line.Point, origin));
                        break;
                }
            }

            if (points.Count < 2) continue;
            foreach (var point in points) bounds = bounds.Include(point.X, point.Y);
            shapes.Add(new ImportedShape
            {
                Points = points,
                IsClosed = figure.IsClosed,
                LayerColor = color,
                PreferredMode = LayerMode.Fill,
            });
        }

        if (shapes.Count == 0 || bounds.IsEmpty)
            throw new InvalidOperationException("Zadaný text neobsahuje žádné vykreslitelné znaky.");

        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var localShapes = shapes.Select(shape => shape with
        {
            Points = shape.Points
                .Select(point => new Position(point.X - centerX, point.Y - centerY, point.Z))
                .ToList(),
        }).ToList();

        return new SceneObject
        {
            Name = text.Trim().Length <= 40 ? text.Trim() : $"{text.Trim()[..37]}…",
            LocalShapes = localShapes,
            LocalBounds = new BoundingBox2D(
                bounds.MinX - centerX,
                bounds.MinY - centerY,
                bounds.MaxX - centerX,
                bounds.MaxY - centerY),
            LocalPivot = Position.Zero,
            Transform = ObjectTransform.Identity with { X = centerX, Y = centerY },
        };
    }

    private static Position ToWorld(Point point, Position origin) => new(
        origin.X + point.X / DipsPerMillimetre,
        origin.Y - point.Y / DipsPerMillimetre,
        0);
}
