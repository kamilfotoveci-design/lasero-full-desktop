using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>When each beat of the brand intro happens, in seconds. One definition feeds the in-app
/// control, the MP4 renderer and the installer frame sequences, so they cannot drift apart.</summary>
internal sealed record IntroTimeline
{
    /// <summary>Start of the contour tracing for L, A, S, E, R, O and the laser head.</summary>
    public required double[] PieceStart { get; init; }
    public required double Outline { get; init; }
    public required double HeadOutline { get; init; }
    /// <summary>Fraction of the outline pass after which the interior starts to fill.</summary>
    public required double FillDelay { get; init; }
    public required double Fill { get; init; }
    public required double BeamStart { get; init; }
    public required double BeamDuration { get; init; }
    public required double DotStart { get; init; }
    public required double DotDuration { get; init; }
    public required double UnderlineStart { get; init; }
    public required double UnderlineDuration { get; init; }
    public required double GridStart { get; init; }
    public required double GridDuration { get; init; }
    public required double TaglineStart { get; init; }
    public required double TaglineDuration { get; init; }
    public required double TaglineStagger { get; init; }
    public required double Total { get; init; }

    /// <summary>The 8 second brand intro: stroke by stroke, then beam, dot, workbench, tagline, hold.</summary>
    public static readonly IntroTimeline Full = new()
    {
        PieceStart = new[] { 0.40, 0.90, 1.40, 1.90, 2.40, 2.90, 3.35 },
        Outline = 0.80, HeadOutline = 0.90, FillDelay = 0.70, Fill = 0.50,
        BeamStart = 4.55, BeamDuration = 0.35,
        DotStart = 4.85, DotDuration = 0.55,
        UnderlineStart = 5.35, UnderlineDuration = 0.90,
        GridStart = 5.20, GridDuration = 1.50,
        TaglineStart = 6.05, TaglineDuration = 0.55, TaglineStagger = 0.035,
        Total = 8.0,
    };

    /// <summary>The same beats compressed to 2.8 s for the installer side panel, where only a few dozen frames fit.</summary>
    public static readonly IntroTimeline Quick = new()
    {
        PieceStart = new[] { 0.08, 0.28, 0.48, 0.68, 0.88, 1.08, 1.20 },
        Outline = 0.45, HeadOutline = 0.50, FillDelay = 0.65, Fill = 0.30,
        BeamStart = 1.70, BeamDuration = 0.20,
        DotStart = 1.85, DotDuration = 0.30,
        UnderlineStart = 1.95, UnderlineDuration = 0.40,
        GridStart = 1.80, GridDuration = 0.60,
        TaglineStart = 2.00, TaglineDuration = 0.35, TaglineStagger = 0.015,
        Total = 2.8,
    };
}

/// <summary>Where the mark sits inside a given area. Wide areas centre one composition; tall ones (the
/// installer side panel) place it in the upper part and let the grid run down.</summary>
internal readonly record struct IntroLayout(double Scale, double OriginX, double OriginY, bool Portrait)
{
    /// <summary>Scene height of the whole block: wordmark, underline and tagline.</summary>
    public const double BlockHeight = 292;
    public const double UnderlineY = 226;
    public const double TaglineTop = 246;
    public const double TaglineSize = 34;

    /// <summary>markOnly: fit just the wordmark (no rule or tagline) into the area, centred, for banners.</summary>
    public static IntroLayout For(Size size, bool markOnly = false)
    {
        if (markOnly)
        {
            var m = Math.Min(size.Width * 0.92 / WordmarkData.Width, size.Height * 0.92 / WordmarkData.Height);
            return new IntroLayout(m, (size.Width - WordmarkData.Width * m) / 2, (size.Height - WordmarkData.Height * m) / 2, false);
        }
        var portrait = size.Height > size.Width * 1.25;
        if (portrait)
        {
            var s = size.Width * 0.80 / WordmarkData.Width;
            return new IntroLayout(s, (size.Width - WordmarkData.Width * s) / 2, size.Height * 0.30, true);
        }
        var scale = Math.Min(size.Width * 0.52 / WordmarkData.Width, size.Height * 0.52 / BlockHeight);
        var oy = (size.Height - BlockHeight * scale) / 2 - size.Height * 0.02;
        return new IntroLayout(scale, (size.Width - WordmarkData.Width * scale) / 2, oy, false);
    }
}

/// <summary>
/// Draws the brand intro at an arbitrary time. A pure function of (time, size): no clocks, no state that
/// depends on call order, so any frame can be rendered on its own, which is what makes the MP4 and the
/// installer frames reproducible. Allocation is a few small geometries per frame; call from OnRender.
/// </summary>
internal sealed class IntroRenderer
{
    private const string Tagline = "Tvořte s jistotou";

    private static readonly Typeface TaglineFace = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    private (double Size, double Dip, FormattedText[] Glyphs, double[] X)? _taglineCache;

    public MotionPalette Palette { get; set; } = MotionPalette.Default;
    public IntroTimeline Timeline { get; set; } = IntroTimeline.Full;
    public double PixelsPerDip { get; set; } = 1.0;
    public bool DrawBackground { get; set; }
    public bool DrawGrid { get; set; } = true;
    public bool DrawTagline { get; set; } = true;
    /// <summary>Fit only the wordmark into the area (banners); the rule and tagline are not drawn.</summary>
    public bool MarkOnly { get; set; }

    public void Render(DrawingContext dc, double t, Size size)
    {
        if (size.Width < 1 || size.Height < 1) return;
        var tl = Timeline;
        t = Math.Clamp(t, 0, tl.Total);
        var layout = IntroLayout.For(size, MarkOnly);
        var pal = Palette;
        var pixel = 1.0 / PixelsPerDip;
        var line = Math.Max(pixel, Math.Round(size.Width * 0.0011 / pixel) * pixel); // hairline in DIPs, never thinner than a device pixel

        if (DrawBackground) dc.DrawRectangle(MotionPalette.Solid(pal.Background), null, new Rect(size));

        if (DrawGrid)
        {
            var g = MotionEase.OutCubic(MotionEase.Phase(t, tl.GridStart, tl.GridDuration));
            if (g > 0) DrawWorkbench(dc, size, layout, g, pixel);
        }

        dc.PushTransform(new TranslateTransform(layout.OriginX, layout.OriginY));
        dc.PushTransform(new ScaleTransform(layout.Scale, layout.Scale));
        var strokeScene = line / layout.Scale;
        var px = pixel / layout.Scale;

        DrawPieces(dc, t, tl, strokeScene);
        DrawBeamAndDot(dc, t, tl, strokeScene);
        if (!MarkOnly) DrawUnderline(dc, t, tl, strokeScene, px);
        dc.Pop();
        dc.Pop();

        if (DrawTagline && !MarkOnly) DrawTaglineText(dc, t, tl, layout);
    }

    // ---- wordmark ---------------------------------------------------------------------------------

    private void DrawPieces(DrawingContext dc, double t, IntroTimeline tl, double stroke)
    {
        var pal = Palette;
        var inkBrush = MotionPalette.Solid(pal.Ink);
        for (var i = 0; i < WordmarkModel.Pieces.Length; i++)
        {
            var piece = WordmarkModel.Pieces[i];
            var outlineDur = i == WordmarkModel.LetterCount ? tl.HeadOutline : tl.Outline;
            var start = tl.PieceStart[i];
            var outlineP = MotionEase.Phase(t, start, outlineDur);
            if (outlineP <= 0) continue;
            var fillP = MotionEase.Phase(t, start + outlineDur * tl.FillDelay, tl.Fill);

            if (fillP < 1)
            {
                var d = piece.TotalLength * MotionEase.InOutSine(outlineP);
                var (path, tip, hasTip) = piece.Partial(d, 0);
                if (path is not null)
                    dc.DrawGeometry(null, pal.Stroke(pal.Ink, stroke, 0.78 * (1 - MotionEase.OutCubic(fillP)), PenLineCap.Round), path);

                var tipAlpha = 1 - MotionEase.SmoothStep(0.90, 1.0, outlineP);
                if (hasTip && tipAlpha > 0)
                {
                    var tailLength = Math.Max(12, piece.TotalLength * 0.06);
                    var tail = piece.Tail(d, tailLength);
                    if (tail is not null) dc.DrawGeometry(null, pal.Stroke(pal.Brand, stroke * 1.9, 0.85 * tipAlpha, PenLineCap.Round), tail);
                    dc.DrawEllipse(MotionPalette.Solid(pal.Brand, tipAlpha), null, tip, stroke * 1.9, stroke * 1.9);
                }
            }

            if (fillP > 0)
            {
                var reveal = MotionEase.OutCubic(fillP);
                var b = piece.Bounds;
                var h = (b.Height + 2) * reveal;
                dc.PushClip(new RectangleGeometry(new Rect(b.X - 2, b.Y - 2, b.Width + 4, h)));
                dc.DrawGeometry(inkBrush, null, piece.Fill);
                dc.Pop();
                if (fillP < 1)
                {
                    // the engraving scan line: a hairline at the leading edge of the fill, fading as it lands
                    var y = b.Y - 2 + h;
                    dc.DrawLine(pal.Stroke(pal.Ink, stroke, 0.4 * (1 - fillP)), new Point(b.X - 2, y), new Point(b.Right + 2, y));
                }
            }
        }
    }

    private void DrawBeamAndDot(DrawingContext dc, double t, IntroTimeline tl, double stroke)
    {
        var pal = Palette;
        var beamP = MotionEase.OutCubic(MotionEase.Phase(t, tl.BeamStart, tl.BeamDuration));
        if (beamP > 0)
        {
            var len = (WordmarkData.DotCenterY - WordmarkData.BeamTop) * beamP;
            dc.DrawRectangle(MotionPalette.Solid(pal.Brand), null,
                new Rect(WordmarkData.BeamX - WordmarkData.BeamWidth / 2, WordmarkData.BeamTop, WordmarkData.BeamWidth, len));
        }

        var dotP = MotionEase.Phase(t, tl.DotStart, tl.DotDuration);
        if (dotP > 0)
        {
            var scale = MotionEase.Lerp(0.30, 1.0, MotionEase.OutCubic(dotP));
            var alpha = MotionEase.Clamp01(dotP * 4);
            var r = WordmarkData.DotRadius * scale;
            dc.DrawEllipse(MotionPalette.Solid(pal.Brand, alpha), null, new Point(WordmarkData.BeamX, WordmarkData.DotCenterY), r, r);
        }

        // a single thin ripple leaves the dot as it ignites; no glow, no particles
        var ringP = MotionEase.Phase(t, tl.DotStart + 0.05, tl.DotDuration + 0.4);
        if (ringP > 0 && ringP < 1)
        {
            var r = WordmarkData.DotRadius * (1 + 1.9 * MotionEase.OutQuart(ringP));
            var a = 0.55 * Math.Pow(1 - ringP, 1.4);
            dc.DrawEllipse(null, Palette.Stroke(pal.Brand, stroke, a), new Point(WordmarkData.BeamX, WordmarkData.DotCenterY), r, r);
        }
    }

    private void DrawUnderline(DrawingContext dc, double t, IntroTimeline tl, double stroke, double px)
    {
        var p = MotionEase.InOutSine(MotionEase.Phase(t, tl.UnderlineStart, tl.UnderlineDuration));
        if (p <= 0) return;
        const double x0 = 2.7, x1 = 657.0, y = IntroLayout.UnderlineY;
        var x = x0 + (x1 - x0) * p;
        dc.DrawLine(Palette.Stroke(Palette.Hairline, stroke), new Point(x0, y), new Point(x, y));
        // red lead-in, the one accent on the rule
        dc.DrawLine(Palette.Stroke(Palette.Brand, stroke * 2), new Point(x0, y), new Point(Math.Min(x, x0 + 46), y));
    }

    // ---- workbench grid and rulers -----------------------------------------------------------------

    private void DrawWorkbench(DrawingContext dc, Size size, IntroLayout layout, double g, double pixel)
    {
        var pal = Palette;
        var cell = Math.Max(14, 40 * layout.Scale);
        var cx = size.Width / 2;
        var cy = layout.Portrait ? layout.OriginY + 100 * layout.Scale : size.Height / 2;
        double Snap(double v) => Math.Round(v / pixel) * pixel + pixel / 2;

        double Alpha(double distFraction, double baseAlpha)
        {
            // reveal travels outward from the centre; the grid is quieter right behind the mark
            var reveal = MotionEase.Clamp01(g * 1.8 - distFraction * 0.8);
            var calm = 0.30 + 0.70 * MotionEase.SmoothStep(0.10, 0.65, distFraction);
            return baseAlpha * reveal * calm;
        }

        var minor = pal.Grid;
        for (var k = -(int)Math.Ceiling(cx / cell); k <= (int)Math.Ceiling(cx / cell); k++)
        {
            var x = cx + k * cell;
            if (x < 0 || x > size.Width) continue;
            var major = k % 4 == 0;
            var a = Alpha(Math.Abs(x - cx) / (size.Width / 2), major ? 0.20 : 0.10);
            if (a > 0.004) dc.DrawLine(pal.Stroke(minor, pixel, a), new Point(Snap(x), 0), new Point(Snap(x), size.Height));
        }
        for (var k = -(int)Math.Ceiling(cy / cell); k <= (int)Math.Ceiling((size.Height - cy) / cell); k++)
        {
            var y = cy + k * cell;
            if (y < 0 || y > size.Height) continue;
            var major = k % 4 == 0;
            var a = Alpha(Math.Abs(y - cy) / (size.Height / 2), major ? 0.20 : 0.10);
            if (a > 0.004) dc.DrawLine(pal.Stroke(minor, pixel, a), new Point(0, Snap(y)), new Point(size.Width, Snap(y)));
        }

        // ruler ticks on the top and left edges, every grid cell; every fourth one longer
        var tick = Math.Max(4, 5 * layout.Scale);
        for (var k = -(int)Math.Ceiling(cx / cell); k <= (int)Math.Ceiling(cx / cell); k++)
        {
            var x = cx + k * cell;
            if (x < 0 || x > size.Width) continue;
            var len = k % 4 == 0 ? tick * 2 : tick;
            dc.DrawLine(pal.Stroke(pal.Grid, pixel, 0.55 * g), new Point(Snap(x), 0), new Point(Snap(x), len));
        }
        for (var k = -(int)Math.Ceiling(cy / cell); k <= (int)Math.Ceiling((size.Height - cy) / cell); k++)
        {
            var y = cy + k * cell;
            if (y < 0 || y > size.Height) continue;
            var len = k % 4 == 0 ? tick * 2 : tick;
            dc.DrawLine(pal.Stroke(pal.Grid, pixel, 0.55 * g), new Point(0, Snap(y)), new Point(len, Snap(y)));
        }

        // registration marks in the four corners
        var inset = Math.Min(size.Width, size.Height) * 0.045;
        var arm = Math.Min(size.Width, size.Height) * 0.022;
        var pen = pal.Stroke(pal.Grid, pixel * 1.5, 0.50 * g);
        foreach (var (px, py, sx, sy) in new[] { (inset, inset, 1, 1), (size.Width - inset, inset, -1, 1), (inset, size.Height - inset, 1, -1), (size.Width - inset, size.Height - inset, -1, -1) })
        {
            dc.DrawLine(pen, new Point(Snap(px), Snap(py)), new Point(Snap(px + sx * arm), Snap(py)));
            dc.DrawLine(pen, new Point(Snap(px), Snap(py)), new Point(Snap(px), Snap(py + sy * arm)));
        }
    }

    // ---- tagline ------------------------------------------------------------------------------------

    private void DrawTaglineText(DrawingContext dc, double t, IntroTimeline tl, IntroLayout layout)
    {
        if (t < tl.TaglineStart) return;
        var fontSize = IntroLayout.TaglineSize * layout.Scale;
        var cache = TaglineGlyphs(fontSize);
        var total = cache.X[^1];
        var left = layout.OriginX + (WordmarkData.Width * layout.Scale - total) / 2;
        var top = layout.OriginY + IntroLayout.TaglineTop * layout.Scale;
        for (var i = 0; i < cache.Glyphs.Length; i++)
        {
            var p = MotionEase.OutCubic(MotionEase.Phase(t, tl.TaglineStart + i * tl.TaglineStagger, tl.TaglineDuration));
            if (p <= 0) continue;
            dc.PushOpacity(p);
            dc.DrawText(cache.Glyphs[i], new Point(left + cache.X[i], top + (1 - p) * 6 * layout.Scale));
            dc.Pop();
        }
    }

    private (double Size, double Dip, FormattedText[] Glyphs, double[] X) TaglineGlyphs(double fontSize)
    {
        if (_taglineCache is { } c && Math.Abs(c.Size - fontSize) < 0.01 && Math.Abs(c.Dip - PixelsPerDip) < 0.001) return c;
        var brush = MotionPalette.Solid(Palette.Muted);
        FormattedText Make(string s) => new(s, CultureInfo.GetCultureInfo("cs-CZ"), FlowDirection.LeftToRight, TaglineFace, fontSize, brush, PixelsPerDip);
        var spacing = fontSize * 0.04;
        var glyphs = new FormattedText[Tagline.Length];
        var x = new double[Tagline.Length + 1];
        for (var i = 0; i < Tagline.Length; i++)
        {
            glyphs[i] = Make(Tagline[i].ToString());
            x[i] = i == 0 ? 0 : Make(Tagline[..i] + "​").WidthIncludingTrailingWhitespace + i * spacing;
        }
        x[^1] = Make(Tagline).WidthIncludingTrailingWhitespace + (Tagline.Length - 1) * spacing;
        _taglineCache = (fontSize, PixelsPerDip, glyphs, x);
        return _taglineCache.Value;
    }

    /// <summary>The words of the tagline, exposed so tests can pin the copy rules (neutral form, no ? or !).</summary>
    public static string TaglineText => Tagline;
}
