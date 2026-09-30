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
/// The typeface a piece of vector text is built from. Kept as a separate record from
/// <see cref="TextSource"/> because the text dialog carries a style choice around before there is
/// any text to apply it to, and because the last-used style is remembered between insertions.
/// </summary>
public sealed record VectorTextStyle
{
    public string FontFamily { get; init; } = TextSource.DefaultFontFamily;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Uppercase { get; init; }
    public bool Weld { get; init; }

    public static VectorTextStyle Default { get; } = new();

    public static VectorTextStyle From(TextSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new VectorTextStyle
        {
            FontFamily = source.FontFamily,
            Bold = source.Bold,
            Italic = source.Italic,
            Uppercase = source.Uppercase,
            Weld = source.Weld,
        };
    }
}

/// <summary>Converts installed Windows fonts to flattened millimetre vector contours.</summary>
[SupportedOSPlatform("windows")]
public static class VectorTextFactory
{
    private const double DipsPerMillimetre = 96.0 / 25.4;

    /// <summary>
    /// How far a straight segment may stray from the true curve, in millimetres. Contours are stored
    /// as polylines, so this is the only thing standing between a round letter and a polygon.
    ///
    /// It was 0.2mm, which is roughly 22 segments around a full circle: at any real engraving size the
    /// curves were visibly faceted. 0.01mm is about 100 segments on a 40mm letter — below what the
    /// laser's own spot can resolve, so it is smooth in every sense that matters, and the point count
    /// stays bounded because the tolerance is absolute: bigger text subdivides more, small text does
    /// not pay for resolution it cannot show.
    /// </summary>
    private const double FlattenToleranceMm = 0.01;

    /// <summary>Welding unions the glyph outlines against each other. The tolerance is in the same
    /// millimetre space as the flattened contours and matches the one the Unite command uses, so a
    /// welded piece of text and a manually united selection resolve overlaps the same way.</summary>
    private const double WeldToleranceMm = 0.01;

    /// <summary>Text visually near-black, on its own processing layer so filled text is not merged
    /// with the default black cut layer.</summary>
    public static RgbColor DefaultColor { get; } = new(52, 52, 52);

    public static SceneObject Create(string text, Position origin, double heightMm, RgbColor color) =>
        Create(text, origin, heightMm, color, VectorTextStyle.Default);

    public static SceneObject Create(
        string text, Position origin, double heightMm, RgbColor color, VectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Create(
            new TextSource
            {
                Text = text,
                HeightMm = heightMm,
                FontFamily = style.FontFamily,
                Bold = style.Bold,
                Italic = style.Italic,
                Uppercase = style.Uppercase,
                Weld = style.Weld,
            },
            origin,
            color);
    }

    public static SceneObject Create(TextSource source, Position origin, RgbColor color)
    {
        ArgumentNullException.ThrowIfNull(source);
        var render = BuildLocalGeometry(source, color);

        // The click point is where the text starts, not where its middle lands, so the pivot ends up
        // offset from the click by half the text's own box.
        return new SceneObject
        {
            Name = BuildName(source),
            LocalShapes = render.Shapes,
            LocalBounds = render.Bounds,
            LocalPivot = Position.Zero,
            Text = source,
            Transform = ObjectTransform.Identity with
            {
                X = origin.X + render.CenterX,
                Y = origin.Y + render.CenterY,
            },
        };
    }

    /// <summary>
    /// Re-renders an existing text object from a changed <see cref="TextSource"/>. The object's
    /// identity, placement, layer assignment and flags all carry over, so the operator sees the
    /// letters change and nothing else move.
    ///
    /// Transform carries over unchanged, scale included. That means the height in the text section is
    /// the type size before any resizing the operator has done on the canvas; the height the job
    /// burns is the one in the object section. Re-deriving the scale here instead would make text
    /// jump the first time its wording was corrected after a manual resize.
    /// </summary>
    public static SceneObject Rebuild(SceneObject existing, TextSource source)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(source);
        if (existing.Text is null)
            throw new InvalidOperationException("Objekt není text, nelze ho znovu vysázet.");

        // Layer identity lives on the contours, so it has to be carried across rather than rebuilt:
        // re-rendering must not silently move text back to a default layer.
        var previous = existing.LocalShapes.FirstOrDefault();
        var color = previous?.LayerColor ?? DefaultColor;
        var render = BuildLocalGeometry(source, color);
        var localShapes = previous is null
            ? render.Shapes
            : render.Shapes
                .Select(shape => shape with
                {
                    LayerId = previous.LayerId,
                    PreferredMode = previous.PreferredMode,
                })
                .ToList();

        return new SceneObject
        {
            Id = existing.Id,
            Name = BuildName(source),
            LocalShapes = localShapes,
            LocalBounds = render.Bounds,
            LocalPivot = Position.Zero,
            Text = source,
            Transform = existing.Transform,
            IsVisible = existing.IsVisible,
            IsLocked = existing.IsLocked,
            IncludeInOutput = existing.IncludeInOutput,
        };
    }

    /// <summary>
    /// Where the top-left of the text's own line box sits in the object's local space, which is the
    /// point the inline editor has to start from for its letters to fall on this object's letters.
    ///
    /// Local geometry is centred on the ink's bounding box, not on where the type was laid out from, so
    /// the layout origin is simply that centring undone. For freshly created text it is the click point
    /// once the transform is applied; for any text it is the same measurement Create/Rebuild used, so it
    /// cannot drift from the contours. Distortion is left out on purpose: it warps the ink after
    /// measuring, an editor cannot show a warp, and the un-warped wording is centred on the object's
    /// pivot so the editor opens where the text is.
    /// </summary>
    public static Position LayoutOriginLocal(TextSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var render = BuildLocalGeometry(source with { Distortion = TextDistortion.None }, DefaultColor);
        return new Position(-render.CenterX, -render.CenterY, 0);
    }

    private static string BuildName(TextSource source)
    {
        var text = source.EffectiveText(CultureInfo.CurrentUICulture);
        return text.Length <= 40 ? text : $"{text[..37]}…";
    }

    /// <summary>
    /// Renders the text to contours centred on their own bounding box, which is the local space every
    /// SceneObject stores geometry in. CenterX/CenterY are where that centre sits relative to the
    /// text's starting point, so a caller placing new text can turn a click into a pivot.
    /// </summary>
    private static (List<ImportedShape> Shapes, BoundingBox2D Bounds, double CenterX, double CenterY)
        BuildLocalGeometry(TextSource source, RgbColor color)
    {
        var text = source.EffectiveText(CultureInfo.CurrentUICulture);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text nesmí být prázdný.", nameof(source));
        if (!source.IsHeightUsable)
            throw new ArgumentOutOfRangeException(
                nameof(source),
                $"Výška textu musí být mezi {TextSource.MinHeightMm:0} a {TextSource.MaxHeightMm:0} mm.");

        // A family the machine does not have would silently fall back to the WPF default, so resolve
        // it here and keep the fallback explicit.
        var family = string.IsNullOrWhiteSpace(source.FontFamily)
            ? new FontFamily(TextSource.DefaultFontFamily)
            : new FontFamily(source.FontFamily);

        var typeface = new Typeface(
            family,
            source.Italic ? FontStyles.Italic : FontStyles.Normal,
            source.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            source.HeightMm * DipsPerMillimetre,
            Brushes.Black,
            1);

        var geometry = formatted.BuildGeometry(new Point(0, 0));
        if (source.Weld) geometry = Weld(geometry);

        var flattened = geometry.GetFlattenedPathGeometry(FlattenToleranceMm, ToleranceType.Absolute);
        var contours = new List<(List<Position> Points, bool IsClosed)>();
        var bounds = BoundingBox2D.Empty;

        foreach (var figure in flattened.Figures)
        {
            var points = new List<Position> { ToMillimetres(figure.StartPoint) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment polyLine:
                        points.AddRange(polyLine.Points.Select(ToMillimetres));
                        break;
                    case LineSegment line:
                        points.Add(ToMillimetres(line.Point));
                        break;
                }
            }

            if (points.Count < 2) continue;
            foreach (var point in points) bounds = bounds.Include(point.X, point.Y);
            contours.Add((points, figure.IsClosed));
        }

        if (contours.Count == 0 || bounds.IsEmpty)
            throw new InvalidOperationException("Zadaný text neobsahuje žádné vykreslitelné znaky.");

        // Distortion is applied after measuring, using the undistorted box as the unit square the
        // corners are expressed in. Measuring the warped result instead would make the same corner
        // values mean something different for every string.
        if (!source.Distortion.IsIdentity && source.Distortion.IsUsable)
        {
            contours = contours
                .Select(contour => (
                    Points: contour.Points.Select(point => Distort(point, bounds, source.Distortion)).ToList(),
                    contour.IsClosed))
                .ToList();

            bounds = BoundingBox2D.Empty;
            foreach (var contour in contours)
            foreach (var point in contour.Points)
                bounds = bounds.Include(point.X, point.Y);

            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException("Zkreslení textu by vytvořilo prázdnou geometrii.");
        }

        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var geometrySetId = Guid.NewGuid();
        var shapes = contours
            .Select(contour => new ImportedShape
            {
                GeometrySetId = geometrySetId,
                Points = contour.Points
                    .Select(point => new Position(point.X - centerX, point.Y - centerY, point.Z))
                    .ToList(),
                IsClosed = contour.IsClosed,
                LayerColor = color,
                PreferredMode = LayerMode.Fill,
            })
            .ToList();

        return (
            shapes,
            new BoundingBox2D(
                bounds.MinX - centerX,
                bounds.MinY - centerY,
                bounds.MaxX - centerX,
                bounds.MaxY - centerY),
            centerX,
            centerY);
    }

    /// <summary>
    /// Unions the glyph outlines so overlapping letters become one contour. Combining the geometry
    /// with an empty geometry is what resolves the overlaps: the union of a self-overlapping figure
    /// with nothing is its own outer boundary, holes kept.
    /// </summary>
    private static Geometry Weld(Geometry geometry) => Geometry.Combine(
        geometry,
        Geometry.Empty,
        GeometryCombineMode.Union,
        null,
        WeldToleranceMm * DipsPerMillimetre,
        ToleranceType.Absolute);

    private static Position Distort(Position point, BoundingBox2D bounds, TextDistortion distortion)
    {
        var u = bounds.Width > 0 ? (point.X - bounds.MinX) / bounds.Width : 0;
        var v = bounds.Height > 0 ? (point.Y - bounds.MinY) / bounds.Height : 0;
        var (mappedU, mappedV) = distortion.Map(u, v);
        return new Position(
            bounds.MinX + mappedU * bounds.Width,
            bounds.MinY + mappedV * bounds.Height,
            point.Z);
    }

    /// <summary>WPF lays type out with Y growing downward; the document has Y growing up.</summary>
    private static Position ToMillimetres(Point point) => new(
        point.X / DipsPerMillimetre,
        -point.Y / DipsPerMillimetre,
        0);
}
