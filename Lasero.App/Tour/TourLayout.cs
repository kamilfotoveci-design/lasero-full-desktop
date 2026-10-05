using System.Windows;

namespace Lasero.App.Tour;

/// <summary>Which edge of the callout card faces the spotlight, where the little pointer is drawn.</summary>
public enum TourCaretSide
{
    None,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// Where the pieces of one coach mark go, all in the overlay's own device-independent coordinates.
/// <see cref="Spotlight"/> is the cut-out (null when the target is missing or fills nothing),
/// <see cref="Card"/> is the callout, <see cref="CaretOffset"/> is the distance along the card edge
/// facing the spotlight where the pointer sits.
/// </summary>
public readonly record struct TourLayoutResult(
    Rect? Spotlight,
    Rect Card,
    TourPlacement Placement,
    TourCaretSide Caret,
    double CaretOffset);

/// <summary>
/// Pure geometry for the coach mark: no controls, so the "card never leaves the window" promise is a
/// plain function that can be tested over every window size and DPI. Device-independent units mean DPI
/// does not enter the maths; the render test checks the same result survives rasterisation at 125 and
/// 150 percent.
///
/// The card goes on the preferred side of the target when it fits entirely inside the window without
/// touching the spotlight, then on the other sides in a fixed order, then (for a target so large that
/// nothing fits beside it, such as the whole inspector in a small window) inside the window in the
/// corner with the most room. It is always clamped to the window, so the worst case is a card laid over
/// part of the spotlight, never one cut off.
/// </summary>
public static class TourLayout
{
    public const double EdgeMargin = 16;
    public const double SpotlightPadding = 6;
    public const double Gap = 18;
    public const double CaretInset = 28;

    public static TourLayoutResult Compute(Size viewport, Rect? target, Size card, TourPlacement preferred)
    {
        var cardWidth = Math.Min(card.Width, Math.Max(0, viewport.Width - 2 * EdgeMargin));
        var cardHeight = Math.Min(card.Height, Math.Max(0, viewport.Height - 2 * EdgeMargin));
        var cardSize = new Size(cardWidth, cardHeight);
        var bounds = new Rect(EdgeMargin, EdgeMargin,
            Math.Max(0, viewport.Width - 2 * EdgeMargin), Math.Max(0, viewport.Height - 2 * EdgeMargin));

        if (target is not { } rawTarget || rawTarget.IsEmpty || rawTarget.Width < 1 || rawTarget.Height < 1)
            return Centered(cardSize, viewport);

        var view = new Rect(0, 0, viewport.Width, viewport.Height);
        var spot = Rect.Intersect(Inflate(rawTarget, SpotlightPadding), view);
        if (spot.IsEmpty) return Centered(cardSize, viewport);

        foreach (var side in Order(preferred))
        {
            var rect = Place(side, spot, cardSize, bounds);
            if (!bounds.Contains(rect) && !NearlyContains(bounds, rect)) continue;
            if (rect.IntersectsWith(Rect.Inflate(spot, -0.5, -0.5))) continue;
            return Result(side, spot, rect);
        }

        // Nothing fits beside the target: take the side with the most free room and clamp the card
        // into the window. It may overlap the spotlight, but it stays fully visible.
        var best = Order(preferred)
            .Select(side => (Side: side, Free: FreeSpace(side, spot, viewport)))
            .OrderByDescending(x => x.Free)
            .First().Side;
        var placed = Place(best, spot, cardSize, bounds);
        var fallback = new Rect(
            Math.Clamp(placed.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - cardSize.Width)),
            Math.Clamp(placed.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - cardSize.Height)),
            cardSize.Width, cardSize.Height);
        return Result(best, spot, fallback);
    }

    private static TourLayoutResult Centered(Size card, Size viewport)
    {
        var rect = new Rect((viewport.Width - card.Width) / 2, (viewport.Height - card.Height) / 2, card.Width, card.Height);
        return new TourLayoutResult(null, rect, TourPlacement.Center, TourCaretSide.None, 0);
    }

    private static TourLayoutResult Result(TourPlacement side, Rect spot, Rect card)
    {
        var caret = side switch
        {
            TourPlacement.Right => TourCaretSide.Left,
            TourPlacement.Left => TourCaretSide.Right,
            TourPlacement.Below => TourCaretSide.Top,
            TourPlacement.Above => TourCaretSide.Bottom,
            _ => TourCaretSide.None,
        };

        double offset = 0;
        if (caret is TourCaretSide.Left or TourCaretSide.Right)
        {
            var center = spot.Top + spot.Height / 2;
            offset = Math.Clamp(center - card.Top, CaretInset, Math.Max(CaretInset, card.Height - CaretInset));
        }
        else if (caret is TourCaretSide.Top or TourCaretSide.Bottom)
        {
            var center = spot.Left + spot.Width / 2;
            offset = Math.Clamp(center - card.Left, CaretInset, Math.Max(CaretInset, card.Width - CaretInset));
        }

        // A pointer that would sit outside the card's own edge, because the card had to be clamped
        // away from the target, is dropped rather than drawn pointing at nothing.
        if (caret != TourCaretSide.None && GapBetween(side, spot, card) > Gap * 3) caret = TourCaretSide.None;
        return new TourLayoutResult(spot, card, side, caret, offset);
    }

    private static double GapBetween(TourPlacement side, Rect spot, Rect card) => side switch
    {
        TourPlacement.Right => card.Left - spot.Right,
        TourPlacement.Left => spot.Left - card.Right,
        TourPlacement.Below => card.Top - spot.Bottom,
        TourPlacement.Above => spot.Top - card.Bottom,
        _ => 0,
    };

    private static Rect Place(TourPlacement side, Rect spot, Size card, Rect bounds)
    {
        double x, y;
        switch (side)
        {
            case TourPlacement.Right:
                x = spot.Right + Gap;
                y = spot.Top + spot.Height / 2 - card.Height / 2;
                break;
            case TourPlacement.Left:
                x = spot.Left - Gap - card.Width;
                y = spot.Top + spot.Height / 2 - card.Height / 2;
                break;
            case TourPlacement.Below:
                x = spot.Left + spot.Width / 2 - card.Width / 2;
                y = spot.Bottom + Gap;
                break;
            case TourPlacement.Above:
                x = spot.Left + spot.Width / 2 - card.Width / 2;
                y = spot.Top - Gap - card.Height;
                break;
            default:
                x = bounds.Left + (bounds.Width - card.Width) / 2;
                y = bounds.Top + (bounds.Height - card.Height) / 2;
                break;
        }

        // Cross-axis clamping only: sliding along the target edge keeps the card beside the target, while
        // the main axis position decides whether this side fits at all.
        if (side is TourPlacement.Right or TourPlacement.Left)
            y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - card.Height));
        else if (side is TourPlacement.Below or TourPlacement.Above)
            x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - card.Width));
        else
        {
            x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - card.Width));
            y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - card.Height));
        }

        return new Rect(x, y, card.Width, card.Height);
    }

    private static double FreeSpace(TourPlacement side, Rect spot, Size viewport) => side switch
    {
        TourPlacement.Right => viewport.Width - spot.Right,
        TourPlacement.Left => spot.Left,
        TourPlacement.Below => viewport.Height - spot.Bottom,
        TourPlacement.Above => spot.Top,
        _ => 0,
    };

    /// <summary>Preferred side first, then the opposite, then the two across, so a card that cannot sit
    /// beside a target at the window edge flips to the other side before moving on.</summary>
    private static IEnumerable<TourPlacement> Order(TourPlacement preferred)
    {
        TourPlacement[] all = [TourPlacement.Right, TourPlacement.Left, TourPlacement.Below, TourPlacement.Above];
        var first = preferred == TourPlacement.Center ? TourPlacement.Below : preferred;
        var opposite = first switch
        {
            TourPlacement.Right => TourPlacement.Left,
            TourPlacement.Left => TourPlacement.Right,
            TourPlacement.Below => TourPlacement.Above,
            _ => TourPlacement.Below,
        };
        yield return first;
        yield return opposite;
        foreach (var other in all)
            if (other != first && other != opposite) yield return other;
    }

    private static Rect Inflate(Rect rect, double by) =>
        new(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);

    private static bool NearlyContains(Rect bounds, Rect rect) =>
        rect.Left >= bounds.Left - 0.01 && rect.Top >= bounds.Top - 0.01 &&
        rect.Right <= bounds.Right + 0.01 && rect.Bottom <= bounds.Bottom + 0.01;
}
