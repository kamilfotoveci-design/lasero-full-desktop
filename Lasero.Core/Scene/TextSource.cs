namespace Lasero.Core.Scene;

/// <summary>
/// Perspective warp of a text object, expressed as the four corners of its own unit box: (0,0) is
/// the bottom-left of the undistorted text and (1,1) the top-right. <see cref="None"/> is therefore
/// the unit square, and every corner can be dragged independently on the canvas.
///
/// Corners are stored rather than a single slant angle because that is what the operator
/// manipulates. A slant is one particular set of corners, not a separate mode.
/// </summary>
public readonly record struct TextDistortion(
    double BottomLeftX, double BottomLeftY,
    double BottomRightX, double BottomRightY,
    double TopRightX, double TopRightY,
    double TopLeftX, double TopLeftY)
{
    public static readonly TextDistortion None = new(0, 0, 1, 0, 1, 1, 0, 1);

    public bool IsIdentity => Equals(None);

    /// <summary>Every corner finite and inside a sane range. A NaN or a runaway corner from a hand
    /// edited project file would otherwise flatten the text to nothing or to an unusable smear.</summary>
    public bool IsUsable
    {
        get
        {
            foreach (var value in Corners())
            {
                if (!double.IsFinite(value) || value is < -4 or > 5) return false;
            }
            return true;
        }
    }

    private double[] Corners() =>
    [
        BottomLeftX, BottomLeftY, BottomRightX, BottomRightY,
        TopRightX, TopRightY, TopLeftX, TopLeftY,
    ];

    /// <summary>Maps a point given in unit-box coordinates through the warped quad, by bilinear
    /// interpolation between the four corners.</summary>
    public (double X, double Y) Map(double u, double v)
    {
        var bottomX = BottomLeftX + (BottomRightX - BottomLeftX) * u;
        var bottomY = BottomLeftY + (BottomRightY - BottomLeftY) * u;
        var topX = TopLeftX + (TopRightX - TopLeftX) * u;
        var topY = TopLeftY + (TopRightY - TopLeftY) * u;
        return (bottomX + (topX - bottomX) * v, bottomY + (topY - bottomY) * v);
    }

    /// <summary>The corner a canvas handle drags, moved to a new unit-box position.</summary>
    public TextDistortion WithCorner(TextDistortionCorner corner, double x, double y) => corner switch
    {
        TextDistortionCorner.BottomLeft => this with { BottomLeftX = x, BottomLeftY = y },
        TextDistortionCorner.BottomRight => this with { BottomRightX = x, BottomRightY = y },
        TextDistortionCorner.TopRight => this with { TopRightX = x, TopRightY = y },
        TextDistortionCorner.TopLeft => this with { TopLeftX = x, TopLeftY = y },
        _ => this,
    };

    public (double X, double Y) Corner(TextDistortionCorner corner) => corner switch
    {
        TextDistortionCorner.BottomLeft => (BottomLeftX, BottomLeftY),
        TextDistortionCorner.BottomRight => (BottomRightX, BottomRightY),
        TextDistortionCorner.TopRight => (TopRightX, TopRightY),
        TextDistortionCorner.TopLeft => (TopLeftX, TopLeftY),
        _ => (0, 0),
    };
}

/// <summary>
/// Which corner of the distortion quad a canvas handle belongs to. Named in document space, where
/// "bottom" is the smaller Y — the canvas flips Y for display, so do not read these as screen
/// positions. Getting that backwards is what put the resize cursors on the wrong diagonals.
/// </summary>
public enum TextDistortionCorner
{
    BottomLeft,
    BottomRight,
    TopRight,
    TopLeft,
}

/// <summary>
/// What a piece of text says and how it is set. A text object keeps this alongside its flattened
/// contours, so the wording, font, height and style stay editable after the text is placed: the
/// contours are a cached render of this record rather than the object's only truth.
///
/// Before this existed, text was flattened to curves on creation and the font could never be
/// changed again.
/// </summary>
public sealed record TextSource
{
    public required string Text { get; init; }

    /// <summary>Cap height of the type in millimetres, before the object's own transform. The height
    /// the job actually burns is the object's world bounds — a text object that has been scaled on
    /// the canvas keeps that scale when it is re-rendered.</summary>
    public double HeightMm { get; init; } = DefaultHeightMm;

    public string FontFamily { get; init; } = DefaultFontFamily;
    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>Renders the text in capitals without changing what the operator typed, so the
    /// original wording survives turning it back off.</summary>
    public bool Uppercase { get; init; }

    /// <summary>Merges overlapping glyph outlines into single contours. Script and display faces
    /// overlap their letters by design; without welding, the laser traces every buried edge inside
    /// the overlap, which shows as a seam through the letters.</summary>
    public bool Weld { get; init; }

    public TextDistortion Distortion { get; init; } = TextDistortion.None;

    public const double DefaultHeightMm = 12;
    public const double MinHeightMm = 1;
    public const double MaxHeightMm = 200;
    public const string DefaultFontFamily = "Segoe UI";

    public bool IsHeightUsable => double.IsFinite(HeightMm) && HeightMm >= MinHeightMm && HeightMm <= MaxHeightMm;

    /// <summary>The string that is actually set, with <see cref="Uppercase"/> applied.</summary>
    public string EffectiveText(System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var trimmed = Text?.Trim() ?? string.Empty;
        return Uppercase ? trimmed.ToUpper(culture) : trimmed;
    }
}
