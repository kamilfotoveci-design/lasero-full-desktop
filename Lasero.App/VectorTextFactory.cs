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

/// <summary>
/// The typeface a piece of vector text is built from. Once the text is placed it becomes contours,
/// so this describes the moment of creation rather than a property that stays editable afterwards.
/// </summary>
public sealed record VectorTextStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public bool Bold { get; init; }
    public bool Italic { get; init; }

    public static VectorTextStyle Default { get; } = new();
}

/// <summary>Converts installed Windows fonts to flattened millimetre vector contours.</summary>
[SupportedOSPlatform("windows")]
public static class VectorTextFactory
{
    private const double DipsPerMillimetre = 96.0 / 25.4;

    public static SceneObject Create(string text, Position origin, double heightMm, RgbColor color) =>
        Create(text, origin, heightMm, color, VectorTextStyle.Default);

    public static SceneObject Create(
        string text, Position origin, double heightMm, RgbColor color, VectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text nesmí být prázdný.", nameof(text));
        if (!double.IsFinite(heightMm) || heightMm is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(heightMm), "Výška textu musí být mezi 1 a 200 mm.");

        // A family the machine does not have would silently fall back to the WPF default, so resolve
        // it here and keep the fallback explicit.
        var family = string.IsNullOrWhiteSpace(style.FontFamily)
            ? new FontFamily("Segoe UI")
            : new FontFamily(style.FontFamily);

        var typeface = new Typeface(
            family,
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text.Trim(),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
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
