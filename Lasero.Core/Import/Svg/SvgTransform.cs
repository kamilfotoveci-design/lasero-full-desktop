using System.Globalization;
using System.Text.RegularExpressions;

namespace Lasero.Core.Import.Svg;

/// <summary>2D affine matrix [a b c d e f] matching SVG's `transform` attribute semantics.</summary>
internal readonly record struct SvgTransform(double A, double B, double C, double D, double E, double F)
{
    public static readonly SvgTransform Identity = new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) =>
        (A * x + C * y + E, B * x + D * y + F);

    public SvgTransform Multiply(SvgTransform o) => new(
        A * o.A + C * o.B, B * o.A + D * o.B,
        A * o.C + C * o.D, B * o.C + D * o.D,
        A * o.E + C * o.F + E, B * o.E + D * o.F + F);

    public double Determinant => A * D - B * C;

    /// <summary>True when the matrix collapses the plane (e.g. scale(0)): nothing under it can be seen.</summary>
    public bool IsDegenerate => !double.IsFinite(Determinant) || Math.Abs(Determinant) < 1e-12;

    private static readonly Regex FuncPattern = new(@"(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    public static SvgTransform Parse(string? value, Action<string>? warn = null)
    {
        var result = Identity;
        if (string.IsNullOrWhiteSpace(value)) return result;

        foreach (Match m in FuncPattern.Matches(value))
        {
            var name = m.Groups[1].Value;
            var args = NumberPattern.Matches(m.Groups[2].Value)
                .Select(n => double.Parse(n.Value, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();

            SvgTransform next;
            switch (name)
            {
                case "translate":
                    next = new SvgTransform(1, 0, 0, 1, args.ElementAtOrDefault(0), args.ElementAtOrDefault(1));
                    break;
                case "scale":
                {
                    var sx = args.Length > 0 ? args[0] : 1;
                    var sy = args.Length > 1 ? args[1] : sx;
                    next = new SvgTransform(sx, 0, 0, sy, 0, 0);
                    break;
                }
                case "rotate":
                    next = RotateTransform(args);
                    break;
                case "skewX":
                    next = new SvgTransform(1, 0, Math.Tan(args.ElementAtOrDefault(0) * Math.PI / 180.0), 1, 0, 0);
                    break;
                case "skewY":
                    next = new SvgTransform(1, Math.Tan(args.ElementAtOrDefault(0) * Math.PI / 180.0), 0, 1, 0, 0);
                    break;
                case "matrix" when args.Length == 6:
                    next = new SvgTransform(args[0], args[1], args[2], args[3], args[4], args[5]);
                    break;
                default:
                    warn?.Invoke($"Transformace „{name}“ není podporována a byla ignorována.");
                    next = Identity;
                    break;
            }
            result = result.Multiply(next);
        }
        return result;
    }

    private static SvgTransform RotateTransform(double[] args)
    {
        var deg = args.ElementAtOrDefault(0);
        var rad = deg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var rotation = new SvgTransform(cos, sin, -sin, cos, 0, 0);
        if (args.Length >= 3)
        {
            var cx = args[1]; var cy = args[2];
            var toOrigin = new SvgTransform(1, 0, 0, 1, -cx, -cy);
            var back = new SvgTransform(1, 0, 0, 1, cx, cy);
            // Multiply(o) means "apply o first, then this" — so to get
            // back(rotation(toOrigin(p))) we must nest apply-order right to left.
            return back.Multiply(rotation).Multiply(toOrigin);
        }
        return rotation;
    }
}
