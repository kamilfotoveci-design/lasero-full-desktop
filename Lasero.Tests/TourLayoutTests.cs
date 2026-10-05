using System.Windows;
using Lasero.App.Tour;

namespace Lasero.Tests;

/// <summary>The geometry of the coach mark as a pure function: the spotlight follows the target, the
/// card is always fully inside the window and clear of the spotlight, at every size the app runs at.</summary>
public sealed class TourLayoutTests
{
    public static readonly TheoryData<double, double> Viewports = new()
    {
        { 1080, 640 }, { 1366, 768 }, { 1920, 1080 }, { 2560, 1440 },
    };

    private static readonly Size Card = new(440, 330);

    /// <summary>Where the real controls sit, as fractions of a window, for the layout cases.</summary>
    private static Rect Rail(Size v) => new(0, 60, 56, v.Height - 60 - 48);
    private static Rect Inspector(Size v) => new(v.Width - 320, 60, 320, v.Height - 60 - 48);
    private static Rect HomeActions(Size v) => new(164 + 24, 60 + 24 + 52, 580, 44);
    private static Rect StripMachine(Size v) => new(16, v.Height - 48 + 6, 190, 36);
    private static Rect StripJob(Size v) => new(v.Width - 420, v.Height - 48 + 6, 400, 36);
    private static Rect Avatar(Size v) => new(v.Width - 320 - 20 - 48, v.Height - 48 - 70 - 48, 48, 48);

    private static IEnumerable<(TourPlacement Pref, Func<Size, Rect> Target, string Name)> Cases()
    {
        yield return (TourPlacement.Below, HomeActions, "home actions");
        yield return (TourPlacement.Right, Rail, "rail");
        yield return (TourPlacement.Left, Inspector, "inspector");
        yield return (TourPlacement.Right, v => new Rect(0, 340, 56, 48), "rail materials");
        yield return (TourPlacement.Above, StripMachine, "strip machine");
        yield return (TourPlacement.Above, StripJob, "strip job");
        yield return (TourPlacement.Left, Avatar, "kamil avatar");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public void CardIsAlwaysFullyInsideTheWindowAndClearOfTheSpotlight(double width, double height)
    {
        var viewport = new Size(width, height);
        foreach (var (pref, target, name) in Cases())
        {
            var result = TourLayout.Compute(viewport, target(viewport), Card, pref);
            var card = result.Card;

            Assert.True(card.Left >= TourLayout.EdgeMargin - 0.01 && card.Top >= TourLayout.EdgeMargin - 0.01, $"{name}: card starts inside margin");
            Assert.True(card.Right <= width - TourLayout.EdgeMargin + 0.01 && card.Bottom <= height - TourLayout.EdgeMargin + 0.01, $"{name}: card ends inside margin ({card})");
            Assert.NotNull(result.Spotlight);
            Assert.False(card.IntersectsWith(Rect.Inflate(result.Spotlight!.Value, -1, -1)), $"{name}: card does not cover the spotlight ({card} vs {result.Spotlight})");
        }
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public void SpotlightIsTheTargetBoundsPaddedAndClippedToTheWindow(double width, double height)
    {
        var viewport = new Size(width, height);
        var target = Rail(viewport);
        var result = TourLayout.Compute(viewport, target, Card, TourPlacement.Right);

        var spot = result.Spotlight!.Value;
        Assert.Equal(Math.Max(0, target.Left - TourLayout.SpotlightPadding), spot.Left, 3);
        Assert.Equal(target.Top - TourLayout.SpotlightPadding, spot.Top, 3);
        Assert.Equal(target.Right + TourLayout.SpotlightPadding, spot.Right, 3);
        Assert.Equal(target.Bottom + TourLayout.SpotlightPadding, spot.Bottom, 3);
        Assert.True(new Rect(0, 0, width, height).Contains(spot));
    }

    [Fact]
    public void PreferredSideIsUsedWhenItFits()
    {
        var v = new Size(1366, 768);
        Assert.Equal(TourPlacement.Right, TourLayout.Compute(v, Rail(v), Card, TourPlacement.Right).Placement);
        Assert.Equal(TourPlacement.Left, TourLayout.Compute(v, Inspector(v), Card, TourPlacement.Left).Placement);
        Assert.Equal(TourPlacement.Above, TourLayout.Compute(v, StripJob(v), Card, TourPlacement.Above).Placement);
        Assert.Equal(TourPlacement.Below, TourLayout.Compute(v, HomeActions(v), Card, TourPlacement.Below).Placement);
    }

    [Fact]
    public void CardFlipsToTheOppositeSideWhenThePreferredOneHasNoRoom()
    {
        var v = new Size(1366, 768);
        // The inspector sits against the right edge: a card preferred to the right has nowhere to go.
        var result = TourLayout.Compute(v, Inspector(v), Card, TourPlacement.Right);
        Assert.Equal(TourPlacement.Left, result.Placement);
    }

    [Fact]
    public void CaretPointsAtTheSpotlightFromTheCardEdgeFacingIt()
    {
        var v = new Size(1366, 768);
        Assert.Equal(TourCaretSide.Left, TourLayout.Compute(v, Rail(v), Card, TourPlacement.Right).Caret);
        Assert.Equal(TourCaretSide.Right, TourLayout.Compute(v, Inspector(v), Card, TourPlacement.Left).Caret);
        Assert.Equal(TourCaretSide.Bottom, TourLayout.Compute(v, StripJob(v), Card, TourPlacement.Above).Caret);
        Assert.Equal(TourCaretSide.Top, TourLayout.Compute(v, HomeActions(v), Card, TourPlacement.Below).Caret);

        var strip = TourLayout.Compute(v, StripJob(v), Card, TourPlacement.Above);
        Assert.InRange(strip.CaretOffset, TourLayout.CaretInset, strip.Card.Width - TourLayout.CaretInset);
    }

    [Fact]
    public void MissingOrEmptyTargetCentresTheCardWithoutASpotlight()
    {
        var v = new Size(1366, 768);
        foreach (Rect? target in new Rect?[] { null, Rect.Empty, new Rect(10, 10, 0, 0) })
        {
            var result = TourLayout.Compute(v, target, Card, TourPlacement.Right);
            Assert.Null(result.Spotlight);
            Assert.Equal(TourPlacement.Center, result.Placement);
            Assert.Equal(TourCaretSide.None, result.Caret);
            Assert.Equal((v.Width - Card.Width) / 2, result.Card.Left, 3);
            Assert.Equal((v.Height - Card.Height) / 2, result.Card.Top, 3);
        }
    }

    [Fact]
    public void TargetOutsideTheWindowIsTreatedAsMissing()
    {
        var v = new Size(1366, 768);
        var result = TourLayout.Compute(v, new Rect(5000, 5000, 100, 100), Card, TourPlacement.Right);
        Assert.Null(result.Spotlight);
    }

    [Fact]
    public void TargetThatFillsMostOfTheWindowStillGetsAnOnScreenCard()
    {
        var v = new Size(1080, 640);
        var huge = new Rect(0, 0, 1070, 630);
        var result = TourLayout.Compute(v, huge, Card, TourPlacement.Left);
        Assert.True(new Rect(0, 0, v.Width, v.Height).Contains(result.Card));
    }

    [Fact]
    public void CardWiderOrTallerThanTheWindowIsShrunkToFit()
    {
        var v = new Size(300, 300);
        var result = TourLayout.Compute(v, new Rect(100, 100, 50, 50), new Size(440, 500), TourPlacement.Right);
        Assert.True(result.Card.Width <= v.Width - 2 * TourLayout.EdgeMargin + 0.01);
        Assert.True(result.Card.Height <= v.Height - 2 * TourLayout.EdgeMargin + 0.01);
        Assert.True(new Rect(0, 0, v.Width, v.Height).Contains(result.Card));
    }

    [Fact]
    public void TargetNearAWindowCornerKeepsTheCardInsideOnBothAxes()
    {
        var v = new Size(1080, 640);
        foreach (var target in new[]
        {
            new Rect(0, 0, 40, 40), new Rect(v.Width - 40, 0, 40, 40),
            new Rect(0, v.Height - 40, 40, 40), new Rect(v.Width - 40, v.Height - 40, 40, 40),
        })
        {
            foreach (TourPlacement pref in Enum.GetValues<TourPlacement>())
            {
                var card = TourLayout.Compute(v, target, Card, pref).Card;
                Assert.True(new Rect(0, 0, v.Width, v.Height).Contains(card), $"{target} / {pref} -> {card}");
            }
        }
    }
}
