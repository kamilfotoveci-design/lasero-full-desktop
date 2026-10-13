using Lasero.Core.Raster;

namespace Lasero.Core.Trace;

/// <summary>
/// The one contour engine of the bitmap trace: it thresholds the grey values with sub-pixel
/// interpolation instead of binarising them, so the outline sits on the true edge of the artwork.
///
/// 1. The brightness field is padded with a mirrored background ring, so a shape touching the image
///    border is closed exactly on the border.
/// 2. Marching squares finds the iso-line at the threshold. The crossing on every cell edge is placed by
///    linear interpolation between the two pixel centres, so an anti-aliased edge lands within a few
///    hundredths of a pixel of where it was drawn, and a hard edge lands exactly between two pixels.
///    Saddles are resolved by the cell-centre value.
/// 3. Segments are oriented (ink always on the right), so every loop's signed area says whether it is an
///    outer contour or a hole. Nesting comes from containment, smallest enclosing loop first.
/// 4. <see cref="ContourSmoother"/> removes pixel staircases and snaps corners.
///
/// Coordinates leave the engine in 1/<see cref="CoordinateScale"/> pixel units inside the existing
/// <see cref="ContourTree"/>, which is what <see cref="CompoundPathBuilder"/> already consumes.
/// </summary>
public sealed class SubpixelContourEngine : IContourExtractionEngine
{
    /// <summary>Integer contour units per source pixel (precision of 1/256 px).</summary>
    public const int CoordinateScale = 256;

    private const double BlurSigmaPerSmoothness = 0.4;
    private const double BoundaryMarginPx = 0.75;
    private const double BoundaryCoverage = 0.95;

    public ContourTree Extract(GrayscaleImage image, BitmapTraceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        if (image.Width < 1 || image.Height < 1)
            throw new ArgumentException("Image does not contain any pixels.", nameof(image));
        cancellationToken.ThrowIfCancellationRequested();

        var field = BuildField(image, options);
        cancellationToken.ThrowIfCancellationRequested();
        var loops = TraceLoops(field, options.MinimumContourAreaPx, cancellationToken);

        var smoothness = options.Smoothness;
        var settings = new SmootherSettings(
            SigmaPx: 4.0 * smoothness,
            CornerSigmaPx: smoothness > 0 ? Math.Max(1.0, 1.5 * smoothness) : 0,
            CornerWindowPx: Math.Clamp(6.0 * smoothness, 6, 10),
            CornerAngleRad: options.CornerAngleThresholdDegrees * Math.PI / 180.0,
            StraightTolerancePx: Math.Max(0.05, Math.Min(options.OptimizeTolerance * 0.75, 0.2)),
            MaxShiftPx: 2.0 * smoothness);

        var roots = Nest(loops, image.Width, image.Height, options.DeleteBoundary, settings, cancellationToken);
        return new ContourTree
        {
            Roots = roots,
            Width = checked(image.Width * CoordinateScale),
            Height = checked(image.Height * CoordinateScale),
            SubpixelScale = CoordinateScale,
        };
    }

    // -------------------------------------------------------------------------------------------
    // Brightness field
    // -------------------------------------------------------------------------------------------

    private sealed class Field
    {
        public required float[] Samples { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required float Iso { get; init; }
    }

    /// <summary>Ink is wherever the field is below the iso level. Pixels are one sample each; the padded grid
    /// has one extra background sample on every side.</summary>
    private static Field BuildField(GrayscaleImage image, BitmapTraceOptions options)
    {
        var w = image.Width;
        var h = image.Height;
        var luminance = image.Luminance as byte[] ?? image.Luminance.ToArray();
        var inverted = options.Invert;
        var cutoff = inverted ? 0 : (int)options.Cutoff;

        var values = new float[w * h];
        for (var i = 0; i < values.Length; i++)
        {
            var v = luminance[i];
            float f = inverted ? 255 - v : v;
            // Pixels brighter than the cutoff are ignored: they count as plain background.
            if (cutoff > 0 && v > cutoff) f = 255;
            values[i] = f;
        }

        var sigma = options.Smoothness * BlurSigmaPerSmoothness;
        if (sigma >= 0.2) values = GaussianBlur(values, w, h, sigma);

        // Ink is "darker than the threshold" (lighter when inverted); the half step puts the iso level
        // exactly between two neighbouring byte values.
        var iso = inverted ? 254.5f - options.Threshold : options.Threshold - 0.5f;

        var wp = w + 2;
        var hp = h + 2;
        var padded = new float[wp * hp];
        Array.Fill(padded, 255f);
        for (var y = 0; y < h; y++)
            Array.Copy(values, y * w, padded, (y + 1) * wp + 1, w);

        // The pad sample mirrors an ink border sample about the iso level, so the crossing lands exactly on
        // the image border whatever the border brightness; a background border sample just repeats.
        float Pad(float sample) => sample < iso ? 2 * iso - sample : sample;
        for (var x = 0; x < w; x++)
        {
            padded[x + 1] = Pad(values[x]);
            padded[(hp - 1) * wp + x + 1] = Pad(values[(h - 1) * w + x]);
        }
        for (var y = 0; y < h; y++)
        {
            padded[(y + 1) * wp] = Pad(values[y * w]);
            padded[(y + 1) * wp + wp - 1] = Pad(values[y * w + w - 1]);
        }

        return new Field { Samples = padded, Width = wp, Height = hp, Iso = iso };
    }

    private static float[] GaussianBlur(float[] source, int w, int h, double sigma)
    {
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[radius + 1];
        double sum = 0;
        for (var k = 0; k <= radius; k++)
        {
            var weight = Math.Exp(-(k * k) / (2 * sigma * sigma));
            kernel[k] = (float)weight;
            sum += k == 0 ? weight : 2 * weight;
        }
        for (var k = 0; k <= radius; k++) kernel[k] = (float)(kernel[k] / sum);

        var horizontal = new float[source.Length];
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var acc = kernel[0] * source[row + x];
                for (var k = 1; k <= radius; k++)
                {
                    var left = Math.Max(0, x - k);
                    var right = Math.Min(w - 1, x + k);
                    acc += kernel[k] * (source[row + left] + source[row + right]);
                }
                horizontal[row + x] = acc;
            }
        }

        var result = new float[source.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var acc = kernel[0] * horizontal[y * w + x];
                for (var k = 1; k <= radius; k++)
                {
                    var up = Math.Max(0, y - k);
                    var down = Math.Min(h - 1, y + k);
                    acc += kernel[k] * (horizontal[up * w + x] + horizontal[down * w + x]);
                }
                result[y * w + x] = acc;
            }
        }
        return result;
    }

    // -------------------------------------------------------------------------------------------
    // Marching squares
    // -------------------------------------------------------------------------------------------

    private sealed class Loop
    {
        public required double[] X { get; init; }
        public required double[] Y { get; init; }
        /// <summary>Signed area in image coordinates (y down): positive for an outer contour (ink inside),
        /// negative for a hole.</summary>
        public required double Area { get; init; }
        public double MinX, MinY, MaxX, MaxY;
        public int Parent = -1;
        public int Depth;
        public List<int> Children { get; } = [];
    }

    private static List<Loop> TraceLoops(Field field, double minimumAreaPx, CancellationToken cancellationToken)
    {
        var f = field.Samples;
        var wp = field.Width;
        var hp = field.Height;
        var iso = field.Iso;

        var segmentStart = new List<int>();
        var segmentEnd = new List<int>();
        var edge = new int[4];
        var ink = new bool[4];

        for (var j = 0; j < hp - 1; j++)
        {
            if ((j & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = j * wp;
            for (var i = 0; i < wp - 1; i++)
            {
                var ia = row + i;
                var id = ia + wp;
                var fa = f[ia];
                var fb = f[ia + 1];
                var fc = f[id + 1];
                var fd = f[id];
                ink[0] = fa < iso;
                ink[1] = fb < iso;
                ink[2] = fc < iso;
                ink[3] = fd < iso;
                if (ink[0] == ink[1] && ink[1] == ink[2] && ink[2] == ink[3]) continue;

                // Cell edges in clockwise order: top, right, bottom, left. Edge ids: horizontal edge
                // between samples (i,j)-(i+1,j) is 2*index, vertical edge (i,j)-(i,j+1) is 2*index+1.
                edge[0] = ia * 2;
                edge[1] = (ia + 1) * 2 + 1;
                edge[2] = id * 2;
                edge[3] = ia * 2 + 1;

                if (ink[0] == ink[2] && ink[1] == ink[3])
                {
                    // Saddle: the cell centre decides whether the ink corners are joined.
                    var centreInk = (fa + fb + fc + fd) * 0.25f < iso;
                    for (var k = 0; k < 4; k++)
                    {
                        if (centreInk)
                        {
                            if (ink[k]) continue;
                            segmentStart.Add(edge[(k + 3) & 3]);
                            segmentEnd.Add(edge[k]);
                        }
                        else
                        {
                            if (!ink[k]) continue;
                            segmentStart.Add(edge[k]);
                            segmentEnd.Add(edge[(k + 3) & 3]);
                        }
                    }
                    continue;
                }

                // Two crossings: the segment runs from where the cell perimeter leaves the ink to where it
                // re-enters it, which keeps the ink on the right of travel.
                var exit = -1;
                var enter = -1;
                for (var k = 0; k < 4; k++)
                {
                    var next = (k + 1) & 3;
                    if (ink[k] && !ink[next]) exit = k;
                    else if (!ink[k] && ink[next]) enter = k;
                }
                segmentStart.Add(edge[exit]);
                segmentEnd.Add(edge[enter]);
            }
        }

        var count = segmentStart.Count;
        var startIndex = new Dictionary<int, int>(count);
        for (var s = 0; s < count; s++) startIndex[segmentStart[s]] = s;

        var visited = new bool[count];
        var loops = new List<Loop>();
        var xs = new List<double>();
        var ys = new List<double>();
        for (var s0 = 0; s0 < count; s0++)
        {
            if (visited[s0]) continue;
            if ((s0 & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();

            xs.Clear();
            ys.Clear();
            var s = s0;
            var closed = false;
            while (true)
            {
                visited[s] = true;
                Crossing(f, wp, hp, iso, segmentStart[s], out var x, out var y);
                xs.Add(x);
                ys.Add(y);
                if (!startIndex.TryGetValue(segmentEnd[s], out var next)) break;
                if (next == s0) { closed = true; break; }
                if (visited[next]) break;
                s = next;
            }
            if (!closed || xs.Count < 3) continue;

            var loopX = xs.ToArray();
            var loopY = ys.ToArray();
            var area = SignedArea(loopX, loopY);
            if (Math.Abs(area) < minimumAreaPx) continue;

            var loop = new Loop { X = loopX, Y = loopY, Area = area };
            loop.MinX = loopX.Min();
            loop.MaxX = loopX.Max();
            loop.MinY = loopY.Min();
            loop.MaxY = loopY.Max();
            loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Image-pixel position of the contour crossing on one cell edge. Sample (i,j) of the padded
    /// grid is the centre of pixel (i-1, j-1), i.e. at (i - 0.5, j - 0.5).</summary>
    private static void Crossing(float[] f, int wp, int hp, float iso, int edgeId, out double x, out double y)
    {
        var index = edgeId >> 1;
        var j = index / wp;
        var i = index - j * wp;
        if ((edgeId & 1) == 0)
        {
            var t = EdgePosition(f, index, 1, i, wp, iso);
            x = i - 0.5 + t;
            y = j - 0.5;
        }
        else
        {
            var t = EdgePosition(f, index, wp, j, hp, iso);
            x = i - 0.5;
            y = j - 0.5 + t;
        }
    }

    /// <summary>Where along the edge between two samples the contour crosses, 0 at the first sample and 1 at
    /// the second. Normally that is where the interpolated brightness equals the threshold. On a steep edge
    /// (a hard or one-pixel anti-aliased step) the visible edge is where brightness is halfway between the
    /// ink and the background level next to it, so for a grey fill the threshold level would sit up to
    /// half a pixel inside the shape; the halfway level is used there instead. For black on white both
    /// levels coincide, and soft gradients keep the plain threshold interpolation.</summary>
    private static double EdgePosition(float[] f, int index, int stride, int position, int count, float iso)
    {
        var a = f[index];
        var b = f[index + stride];
        var aIsInk = a < iso;
        var inkPlateau = aIsInk ? a : b;
        var backgroundPlateau = aIsInk ? b : a;
        for (var k = 1; k <= 2; k++)
        {
            if (position - k >= 0)
            {
                var v = f[index - k * stride];
                if (aIsInk) inkPlateau = Math.Min(inkPlateau, v);
                else backgroundPlateau = Math.Max(backgroundPlateau, v);
            }
            if (position + 1 + k < count)
            {
                var v = f[index + (1 + k) * stride];
                if (aIsInk) backgroundPlateau = Math.Max(backgroundPlateau, v);
                else inkPlateau = Math.Min(inkPlateau, v);
            }
        }

        var contrast = backgroundPlateau - inkPlateau;
        var level = iso;
        if (contrast >= 32 && Math.Abs(b - a) >= 0.5f * contrast) level = (inkPlateau + backgroundPlateau) / 2;
        return Math.Clamp((level - a) / (double)(b - a), 0, 1);
    }

    private static double SignedArea(double[] x, double[] y)
    {
        var area = 0d;
        for (var i = 0; i < x.Length; i++)
        {
            var j = (i + 1) % x.Length;
            area += x[i] * y[j] - x[j] * y[i];
        }
        return area / 2;
    }

    // -------------------------------------------------------------------------------------------
    // Nesting
    // -------------------------------------------------------------------------------------------

    private static List<RawContour> Nest(
        List<Loop> loops, int imageWidth, int imageHeight, bool deleteBoundary,
        SmootherSettings settings, CancellationToken cancellationToken)
    {
        var order = Enumerable.Range(0, loops.Count)
            .OrderByDescending(index => Math.Abs(loops[index].Area))
            .ToArray();

        // The smallest enclosing loop is the nearest larger loop that contains this one, so scan from
        // the neighbour with the next larger area outwards. One vertex decides containment because
        // iso-lines of one field never cross.
        for (var position = 1; position < order.Length; position++)
        {
            if ((position & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var child = loops[order[position]];
            for (var candidatePosition = position - 1; candidatePosition >= 0; candidatePosition--)
            {
                var candidate = loops[order[candidatePosition]];
                if (candidate.MinX > child.MinX || candidate.MaxX < child.MaxX ||
                    candidate.MinY > child.MinY || candidate.MaxY < child.MaxY) continue;
                if (!Contains(candidate, child.X[0], child.Y[0])) continue;
                child.Parent = order[candidatePosition];
                break;
            }
        }

        var removed = new bool[loops.Count];
        for (var index = 0; index < loops.Count; index++)
            if (loops[index].Parent >= 0) loops[loops[index].Parent].Children.Add(index);

        if (deleteBoundary)
        {
            for (var index = 0; index < loops.Count; index++)
            {
                var loop = loops[index];
                if (loop.Parent >= 0 || loop.Area <= 0 || !HugsBorder(loop, imageWidth, imageHeight)) continue;
                removed[index] = true;
                foreach (var holeIndex in loop.Children)
                {
                    removed[holeIndex] = true;
                    foreach (var islandIndex in loops[holeIndex].Children) loops[islandIndex].Parent = -1;
                }
            }
        }

        foreach (var index in order)
        {
            var loop = loops[index];
            loop.Depth = loop.Parent < 0 ? 0 : loops[loop.Parent].Depth + 1;
        }

        var roots = new List<RawContour>();
        foreach (var index in order)
        {
            var loop = loops[index];
            if (loop.Parent >= 0 || removed[index]) continue;
            if (loop.Area < 0) continue; // a hole with no enclosing shape cannot occur; never emit one as a fill
            roots.Add(BuildContour(loops, index, removed, settings, cancellationToken));
        }
        return roots;
    }

    private static RawContour BuildContour(
        List<Loop> loops, int index, bool[] removed, SmootherSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loop = loops[index];
        var smoothed = ContourSmoother.Smooth(loop.X, loop.Y, settings);
        var points = new List<PixelPoint>(smoothed.Count);
        foreach (var p in smoothed)
            points.Add(new PixelPoint((int)Math.Round(p.X * CoordinateScale), (int)Math.Round(p.Y * CoordinateScale)));

        var children = new List<RawContour>();
        foreach (var childIndex in loop.Children)
        {
            if (removed[childIndex] || loops[childIndex].Parent != index) continue;
            children.Add(BuildContour(loops, childIndex, removed, settings, cancellationToken));
        }
        var reference = new List<PixelPoint>(loop.X.Length);
        for (var i = 0; i < loop.X.Length; i++)
            reference.Add(new PixelPoint((int)Math.Round(loop.X[i] * CoordinateScale), (int)Math.Round(loop.Y[i] * CoordinateScale)));
        return new RawContour { Points = points, Depth = loop.Depth, Children = children, Reference = reference };
    }

    private static bool Contains(Loop loop, double px, double py)
    {
        var x = loop.X;
        var y = loop.Y;
        var inside = false;
        for (int i = 0, j = x.Length - 1; i < x.Length; j = i++)
        {
            if ((y[i] > py) != (y[j] > py) &&
                px < (x[j] - x[i]) * (py - y[i]) / (y[j] - y[i]) + x[i])
                inside = !inside;
        }
        return inside;
    }

    private static bool HugsBorder(Loop loop, int imageWidth, int imageHeight)
    {
        var near = 0;
        for (var i = 0; i < loop.X.Length; i++)
        {
            if (loop.X[i] <= BoundaryMarginPx || loop.X[i] >= imageWidth - BoundaryMarginPx ||
                loop.Y[i] <= BoundaryMarginPx || loop.Y[i] >= imageHeight - BoundaryMarginPx) near++;
        }
        return near >= loop.X.Length * BoundaryCoverage;
    }
}
