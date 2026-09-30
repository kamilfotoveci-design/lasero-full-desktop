using System.Windows;
using System.Windows.Media;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>
/// How the design canvas maps a document point (millimetres, Y up) to a canvas pixel: the same three
/// formulas SceneCanvas.ToCanvasX/ToCanvasY apply to its own pan/zoom fields, lifted out so the inline
/// text editor can be laid out (and unit-tested) without a live canvas.
/// </summary>
internal readonly record struct CanvasView(
    double ScalePxPerMm, double OffsetXMm, double OffsetYMm, double MarginPx, double HeightPx)
{
    public Point ToCanvas(Position world) => new(
        MarginPx + (world.X - OffsetXMm) * ScalePxPerMm,
        HeightPx - MarginPx - (world.Y - OffsetYMm) * ScalePxPerMm);
}

/// <summary>
/// Where the inline TextBox must sit, how big its type must be, and how it must be transformed so that
/// its glyphs land exactly on the glyphs SceneCanvas draws for the same text object.
///
/// The editor is laid out in "editor pixels": its origin is the top-left of the text's own line box
/// (the point <see cref="VectorTextFactory"/> lays the type out from), its font size is the unscaled em
/// of the text at the current zoom, and one editor pixel is 1/scale millimetres of unscaled text. The
/// object's own scale, rotation and flip are then a single 2x2 transform on top. Nothing is derived by
/// reasoning about signs: the origin and both axes are found by pushing three local points through the
/// very same <see cref="ObjectTransform.Apply"/> and <see cref="CanvasView.ToCanvas"/> the canvas uses
/// to draw the contours, so the editor cannot disagree with the artwork about Y flip, mirroring or
/// which corner is which.
///
/// What this replaced took the object's "TopLeft" corner as the screen top-left. That enum names the
/// minimum-Y corner, which the canvas draws at the bottom, so the editor hung one full box-height
/// below the text it was editing. It also multiplied the type size by ScaleY while sizing the box by
/// both scales, so a text stretched on one axis showed one huge letter in a box too small for it. And
/// it was laid out once and never again, so panning or zooming while editing left it stranded.
/// </summary>
internal readonly record struct InlineTextEditorLayout(Point Origin, double FontSizePx, Matrix Transform)
{
    /// <summary>
    /// Space WPF's TextBoxView keeps before the first glyph (and after the last) for the caret, in the
    /// TextBox's own units. It is not Padding or Margin and a template cannot remove it:
    /// GetRectFromCharacterIndex(0).Left is 2 and ActualWidth is the wording's advance plus 4, whatever
    /// the font. The origin is moved back by this much so the first letter, not the box, is what starts
    /// on the object's line box; the render tests fail by exactly this amount without it.
    /// </summary>
    public const double TextBoxHorizontalInset = 2;

    /// <param name="transform">The object's placement, exactly as SceneCanvas draws it.</param>
    /// <param name="pivot">The object's LocalPivot.</param>
    /// <param name="layoutOriginLocal">Where the text's line box starts, in the object's local space
    /// (<see cref="VectorTextFactory.LayoutOriginLocal"/>).</param>
    /// <param name="heightMm">TextSource.HeightMm: the em of the unscaled type, in millimetres.</param>
    public static InlineTextEditorLayout Compute(
        ObjectTransform transform, Position pivot, Position layoutOriginLocal, double heightMm, CanvasView view)
    {
        Point Screen(double localX, double localY) =>
            view.ToCanvas(transform.Apply(new Position(localX, localY, 0), pivot));

        // Local space is Y up, the editor's is Y down, so "one line further down the page" is -Y.
        var origin = Screen(layoutOriginLocal.X, layoutOriginLocal.Y);
        var alongText = Screen(layoutOriginLocal.X + 1, layoutOriginLocal.Y);
        var downThePage = Screen(layoutOriginLocal.X, layoutOriginLocal.Y - 1);

        // One editor pixel is 1/scale millimetre, so a millimetre step is divided down to a pixel step.
        var pixel = 1 / Math.Max(view.ScalePxPerMm, 1e-9);
        var matrix = new Matrix(
            (alongText.X - origin.X) * pixel, (alongText.Y - origin.Y) * pixel,
            (downThePage.X - origin.X) * pixel, (downThePage.Y - origin.Y) * pixel,
            0, 0);

        // The TextBox itself starts TextBoxHorizontalInset editor pixels before the text does.
        var inset = matrix.Transform(new Vector(TextBoxHorizontalInset, 0));
        var boxOrigin = new Point(origin.X - inset.X, origin.Y - inset.Y);

        return new InlineTextEditorLayout(boxOrigin, heightMm * view.ScalePxPerMm, matrix);
    }
}
