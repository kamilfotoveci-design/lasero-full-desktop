using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// The idle loop: the finished mark with a breathing red dot, a single thin ripple per breath and a
/// short laser line travelling slowly along the rule underneath. Periodic in <see cref="Period"/>, so a
/// video of exactly one period loops without a seam. A pure function of the phase.
///
/// Cost: everything that does not move (the mark, rule, grid, tagline) is recorded once into a frozen
/// DrawingGroup and replayed by the render thread; each frame only adds the dot, the ripple and the line.
/// </summary>
internal sealed class PulseRenderer
{
    /// <summary>One loop in seconds; two breaths per loop.</summary>
    public const double Period = 5.0;
    private const int BreathsPerLoop = 2;

    private readonly IntroRenderer _hold = new() { DrawTagline = true };
    private DrawingGroup? _static;
    private (Size Size, double Dip, MotionPalette Palette, bool Wordmark, bool Background)? _staticKey;

    public MotionPalette Palette { get => _hold.Palette; set => _hold.Palette = value; }
    public double PixelsPerDip { get => _hold.PixelsPerDip; set => _hold.PixelsPerDip = value; }
    public bool DrawBackground { get => _hold.DrawBackground; set => _hold.DrawBackground = value; }

    /// <summary>True: the full wordmark with rule, grid and tagline (hero, video loop). False: just the laser
    /// head, beam and dot with a short track (login, empty states, waiting screens).</summary>
    public bool ShowWordmark { get; set; }

    /// <summary>Reduced motion: draw the resting mark only, no swell, ripple or travelling line.</summary>
    public bool Static { get; set; }

    private readonly record struct Frame(double Scale, double OriginX, double OriginY, double TrackY, double TrackX0, double TrackX1);

    private Frame Layout(Size size)
    {
        if (ShowWordmark)
        {
            var layout = IntroLayout.For(size);
            return new Frame(layout.Scale, layout.OriginX, layout.OriginY, IntroLayout.UnderlineY, 2.7, 657);
        }
        // symbol only: head + beam + dot, scaled to leave room for the track below it
        var symbol = WordmarkModel.Head.Bounds;
        var symbolBottom = WordmarkData.DotCenterY + WordmarkData.DotRadius * 2.6;
        var h = symbolBottom - symbol.Top + 34; // + track
        var scale = Math.Min(size.Height * 0.92 / h, size.Width * 0.92 / (symbol.Width * 2.4));
        return new Frame(scale,
            size.Width / 2 - (symbol.Left + symbol.Width / 2) * scale,
            (size.Height - h * scale) / 2 - symbol.Top * scale,
            symbolBottom + 18,
            symbol.Left + symbol.Width / 2 - symbol.Width * 1.2,
            symbol.Left + symbol.Width / 2 + symbol.Width * 1.2);
    }

    private static double LineWidth(Size size, double pixel) =>
        Math.Max(pixel, Math.Round(Math.Min(size.Width, size.Height * 1.6) * 0.0011 / pixel) * pixel);

    private Drawing StaticLayer(Size size, Frame f)
    {
        var key = (size, PixelsPerDip, Palette, ShowWordmark, DrawBackground);
        if (_static is not null && _staticKey == key) return _static;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var pal = Palette;
            if (ShowWordmark)
            {
                _hold.Render(dc, _hold.Timeline.Total, size);
            }
            else
            {
                if (DrawBackground) dc.DrawRectangle(MotionPalette.Solid(pal.Background), null, new Rect(size));
                dc.PushTransform(new TranslateTransform(f.OriginX, f.OriginY));
                dc.PushTransform(new ScaleTransform(f.Scale, f.Scale));
                dc.DrawGeometry(MotionPalette.Solid(pal.Ink), null, WordmarkModel.Head.Fill);
                dc.DrawRectangle(MotionPalette.Solid(pal.Brand), null,
                    new Rect(WordmarkData.BeamX - WordmarkData.BeamWidth / 2, WordmarkData.BeamTop,
                             WordmarkData.BeamWidth, WordmarkData.DotCenterY - WordmarkData.BeamTop));
                var stroke = LineWidth(size, 1.0 / PixelsPerDip) / f.Scale;
                dc.DrawLine(pal.Stroke(pal.Hairline, stroke), new Point(f.TrackX0, f.TrackY), new Point(f.TrackX1, f.TrackY));
                dc.Pop();
                dc.Pop();
            }
        }
        group.Freeze();
        _static = group;
        _staticKey = key;
        return group;
    }

    public void Render(DrawingContext dc, double seconds, Size size)
    {
        if (size.Width < 1 || size.Height < 1) return;
        var phase = ((seconds % Period) + Period) % Period / Period; // 0..1
        var pal = Palette;
        var pixel = 1.0 / PixelsPerDip;
        var f = Layout(size);

        dc.DrawDrawing(StaticLayer(size, f));

        dc.PushTransform(new TranslateTransform(f.OriginX, f.OriginY));
        dc.PushTransform(new ScaleTransform(f.Scale, f.Scale));
        var stroke = LineWidth(size, pixel) / f.Scale;
        var center = new Point(WordmarkData.BeamX, WordmarkData.DotCenterY);

        if (Static)
        {
            if (!ShowWordmark)
                dc.DrawEllipse(MotionPalette.Solid(pal.Brand), null, center, WordmarkData.DotRadius, WordmarkData.DotRadius);
            dc.Pop();
            dc.Pop();
            return;
        }

        // breathing dot: it grows to 1.12x and back, an overlay in the same red so the base dot stays put
        var breath = (phase * BreathsPerLoop) % 1.0;
        var swell = 0.5 - 0.5 * Math.Cos(2 * Math.PI * breath);
        var r = WordmarkData.DotRadius * (1 + 0.12 * swell);
        dc.DrawEllipse(MotionPalette.Solid(pal.Brand), null, center, r, r);

        // one thin ripple per breath, starting as the dot is at its largest
        var ring = (breath + 0.5) % 1.0;
        var ringR = WordmarkData.DotRadius * (1.12 + 1.7 * MotionEase.OutQuart(ring));
        dc.DrawEllipse(null, pal.Stroke(pal.Brand, stroke, 0.42 * Math.Pow(1 - ring, 1.5)), center, ringR, ringR);

        // the travelling line: slow, eased, invisible at the loop seam
        var u = MotionEase.Phase(phase, 0.08, 0.84);
        if (u > 0 && u < 1)
        {
            var len = (f.TrackX1 - f.TrackX0) * 0.16;
            var head = MotionEase.Lerp(f.TrackX0, f.TrackX1 + len, MotionEase.InOutSine(u));
            var tail = Math.Max(f.TrackX0, head - len);
            head = Math.Min(head, f.TrackX1);
            var envelope = MotionEase.SmoothStep(0, 0.12, u) * (1 - MotionEase.SmoothStep(0.88, 1, u));
            if (head > tail)
                dc.DrawLine(pal.Stroke(pal.Brand, stroke * 2, envelope, PenLineCap.Round), new Point(tail, f.TrackY), new Point(head, f.TrackY));
        }

        dc.Pop();
        dc.Pop();
    }
}
