using Lasero.App.Controls;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// The pure half of in-place text editing: where the TextBox goes, how big its type is and how it is
/// turned, from the object's placement and the view. Expected numbers are worked out by hand from the
/// canvas formulas (x = margin + (wx - offset) * scale, y = height - margin - (wy - offset) * scale)
/// rather than by calling the code under test, so a sign or corner mistake cannot cancel itself out.
/// The pixel-level proof that this really lands on the letters is InlineTextEditorRenderTests.
/// </summary>
public sealed class InlineTextEditorLayoutTests
{
    private const int Precision = 6;

    // A 12 mm "TEXT" laid out from local (-13, 4): the line box starts 13 mm left of and 4 mm above
    // the pivot, which is what centring the ink on the pivot produces.
    private static readonly Position LayoutOrigin = new(-13, 4, 0);
    private static readonly ObjectTransform Placed = ObjectTransform.Identity with { X = 100, Y = 100 };

    private static CanvasView View(double scale) => new(scale, 0, 0, 20, 600);

    private static InlineTextEditorLayout Compute(ObjectTransform transform, double scale = 4) =>
        InlineTextEditorLayout.Compute(transform, Position.Zero, LayoutOrigin, 12, View(scale));

    [Fact]
    public void UnrotatedTextStartsAtItsLineBoxWithTheTextBoxInsetTakenOff()
    {
        var layout = Compute(Placed);

        // World (87, 104) -> canvas (368, 164); the box starts 2 editor px before the first letter.
        Assert.Equal(368 - InlineTextEditorLayout.TextBoxHorizontalInset, layout.Origin.X, Precision);
        Assert.Equal(164, layout.Origin.Y, Precision);
        Assert.Equal(48, layout.FontSizePx, Precision);
        Assert.True(layout.Transform.IsIdentity, "an unscaled, unrotated object needs no transform");
    }

    [Fact]
    public void FontSizeIsTheUnscaledEmAtThisZoomAndNeverTheObjectScale()
    {
        // The old editor multiplied the em by ScaleY while sizing its box with both scales, which is
        // how a text stretched on one axis showed one huge letter. Scale belongs to the transform.
        var stretched = Compute(Placed with { ScaleX = 1, ScaleY = 3.5 });

        Assert.Equal(48, stretched.FontSizePx, Precision);
        Assert.Equal(1, stretched.Transform.M11, Precision);
        Assert.Equal(3.5, stretched.Transform.M22, Precision);
        Assert.Equal(0, stretched.Transform.M12, Precision);
        Assert.Equal(0, stretched.Transform.M21, Precision);
    }

    [Fact]
    public void UniformScaleScalesTheTransformAndMovesTheOrigin()
    {
        var layout = Compute(Placed with { ScaleX = 3, ScaleY = 3 });

        // Local (-13, 4) scaled by 3 is (-39, 12); world (61, 112) -> canvas (264, 132).
        Assert.Equal(264 - 3 * InlineTextEditorLayout.TextBoxHorizontalInset, layout.Origin.X, Precision);
        Assert.Equal(132, layout.Origin.Y, Precision);
        Assert.Equal(48, layout.FontSizePx, Precision);
        Assert.Equal(3, layout.Transform.M11, Precision);
        Assert.Equal(3, layout.Transform.M22, Precision);
    }

    [Fact]
    public void QuarterTurnRotatesTheEditorAroundTheObjectAndTurnsTheInset()
    {
        var layout = Compute(Placed with { RotationDeg = 90 });

        // Local (-13, 4) turned 90 degrees counter-clockwise is (-4, -13): world (96, 87) -> canvas
        // (404, 232). Along the text is now screen-up, so the inset that sits "before" the first letter
        // is below it: the box starts 2 px lower.
        Assert.Equal(404, layout.Origin.X, Precision);
        Assert.Equal(232 + InlineTextEditorLayout.TextBoxHorizontalInset, layout.Origin.Y, Precision);
        Assert.Equal(0, layout.Transform.M11, Precision);
        Assert.Equal(-1, layout.Transform.M12, Precision);
        Assert.Equal(1, layout.Transform.M21, Precision);
        Assert.Equal(0, layout.Transform.M22, Precision);
    }

    [Fact]
    public void MirroredTextReadsBackwardsAlongTheScreen()
    {
        var layout = Compute(Placed with { ScaleX = -1 });

        // Local x -13 becomes +13: world (113, 104) -> canvas (472, 164). The text now runs to the
        // left, so the inset is added rather than taken off.
        Assert.Equal(472 + InlineTextEditorLayout.TextBoxHorizontalInset, layout.Origin.X, Precision);
        Assert.Equal(164, layout.Origin.Y, Precision);
        Assert.Equal(-1, layout.Transform.M11, Precision);
        Assert.Equal(1, layout.Transform.M22, Precision);
    }

    [Fact]
    public void ZoomChangesTheFontSizeAndOriginButNotTheTransform()
    {
        var zoomedIn = Compute(Placed, scale: 8);

        // World (87, 104) at 8 px/mm -> canvas (716, -252).
        Assert.Equal(716 - InlineTextEditorLayout.TextBoxHorizontalInset, zoomedIn.Origin.X, Precision);
        Assert.Equal(-252, zoomedIn.Origin.Y, Precision);
        Assert.Equal(96, zoomedIn.FontSizePx, Precision);
        Assert.True(zoomedIn.Transform.IsIdentity);
    }

    [Fact]
    public void PanningMovesTheOriginByExactlyThePan()
    {
        var home = InlineTextEditorLayout.Compute(Placed, Position.Zero, LayoutOrigin, 12, new CanvasView(4, 0, 0, 20, 600));
        // Pan 10 mm right and 5 mm up: the artwork moves 40 px left and 20 px down.
        var panned = InlineTextEditorLayout.Compute(Placed, Position.Zero, LayoutOrigin, 12, new CanvasView(4, 10, 5, 20, 600));

        Assert.Equal(home.Origin.X - 40, panned.Origin.X, Precision);
        Assert.Equal(home.Origin.Y + 20, panned.Origin.Y, Precision);
        Assert.Equal(home.FontSizePx, panned.FontSizePx, Precision);
    }

    [Fact]
    public void TextHeightSetsTheFontSizeLinearly()
    {
        var small = InlineTextEditorLayout.Compute(Placed, Position.Zero, LayoutOrigin, 6, View(4));
        var large = InlineTextEditorLayout.Compute(Placed, Position.Zero, LayoutOrigin, 24, View(4));

        Assert.Equal(24, small.FontSizePx, Precision);
        Assert.Equal(96, large.FontSizePx, Precision);
    }
}
