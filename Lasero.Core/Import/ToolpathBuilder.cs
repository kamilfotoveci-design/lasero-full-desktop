using System.Globalization;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;

namespace Lasero.Core.Import;

/// <summary>
/// Turns imported shapes + their layer settings into real G-code. Fill layers
/// are scanline-filled (even-odd rule across all of a layer's shape edges
/// combined, so holes in letters like "O"/"A" render correctly); Cut layers
/// trace the shape outline directly. Layers run in the explicit manufacturing
/// order selected by the operator in the Layers panel.
/// </summary>
public static class ToolpathBuilder
{
    public static List<string> BuildGCode(ImportedDocument document, double controllerMaximumS)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = new List<string> { "G90", "G21", "M5" };

        // Collection order is the explicit manufacturing order shown in the Layers panel.
        // Never silently move cut layers after fill layers here; preflight may warn, but the
        // operator remains in control of the physical sequence.
        var orderedLayers = document.Layers.Where(l => l.IsEnabled).ToList();

        foreach (var layer in orderedLayers)
        {
            var shapes = document.Shapes.Where(shape => BelongsToLayer(shape, layer)).ToList();
            if (shapes.Count == 0) continue;
            var powerS = GrblPowerScale.PercentToSValue(layer.Power, controllerMaximumS);

            lines.Add($"; --- Vrstva {layer.Name} ({layer.Mode}) ---");

            switch (layer.Mode)
            {
                case LayerMode.Cut:
                    lines.Add("; Operace: Čára");
                    AppendCutLayer(lines, shapes, layer, powerS);
                    break;
                case LayerMode.Fill:
                    lines.Add("; Operace: Výplň");
                    AppendFillLayer(lines, shapes, layer, powerS);
                    break;
                case LayerMode.FillAndCut:
                    // Engrave first so the final contour cannot shift an already cut-out part.
                    lines.Add("; Operace: Výplň");
                    AppendFillLayer(lines, shapes, layer, powerS);
                    lines.Add("; Operace: Čára");
                    AppendCutLayer(lines, shapes, layer, powerS);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(layer.Mode), layer.Mode, "Neznámý režim vrstvy.");
            }
        }

        lines.Add("M5");
        return lines;
    }

    private static bool BelongsToLayer(ImportedShape shape, LayerSettings layer) =>
        shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color);

    private static void AppendCutLayer(List<string> lines, List<ImportedShape> shapes, LayerSettings layer, double powerS)
    {
        var feed = Fmt(layer.Speed);
        for (int pass = 0; pass < layer.Passes; pass++)
        {
            foreach (var shape in shapes)
            {
                if (shape.Points.Count < 2) continue;
                var first = shape.Points[0];
                lines.Add($"G0 X{Fmt(first.X)} Y{Fmt(first.Y)}");
                lines.Add($"M4 S{Fmt(powerS)}");
                foreach (var p in shape.Points.Skip(1))
                    lines.Add($"G1 X{Fmt(p.X)} Y{Fmt(p.Y)} F{feed}");
                if (shape.IsClosed && !SamePoint(shape.Points[^1], first))
                    lines.Add($"G1 X{Fmt(first.X)} Y{Fmt(first.Y)} F{feed}");
                lines.Add("M5");
            }
        }
    }

    private static void AppendFillLayer(List<string> lines, List<ImportedShape> shapes, LayerSettings layer, double powerS)
    {
        var closedShapes = shapes.Where(s => s.IsClosed && s.Points.Count >= 3).ToList();
        if (closedShapes.Count == 0) return;

        var minY = closedShapes.SelectMany(s => s.Points).Min(p => p.Y);
        var maxY = closedShapes.SelectMany(s => s.Points).Max(p => p.Y);
        var interval = Math.Max(0.02, layer.FillLineIntervalMm);
        var feed = Fmt(layer.Speed);

        var leftToRight = true;

        for (int pass = 0; pass < layer.Passes; pass++)
        {
            for (var y = minY + interval / 2; y <= maxY; y += interval)
            {
                var xs = new List<double>();
                foreach (var shape in closedShapes)
                {
                    var pts = shape.Points;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var a = pts[i];
                        var b = pts[(i + 1) % pts.Count];
                        var crosses = (a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y);
                        if (!crosses) continue;
                        var t = (y - a.Y) / (b.Y - a.Y);
                        xs.Add(a.X + t * (b.X - a.X));
                    }
                }

                // Even-odd rule across ALL of this layer's shape edges combined —
                // pairs of consecutive crossings are "inside" runs. This is what
                // makes holes in letters (O, A, ...) fill correctly.
                xs.Sort();

                // Serpentine: alternate scan direction line by line, so the head ends each pass
                // where the next one begins. Every line used to be cut left-to-right with a full-width
                // rapid back to the left margin in between, which on a solid fill meant the machine
                // travelled roughly as far repositioning as it did engraving — a 150x100mm fill spent
                // about half its run time flying back and forth doing nothing.
                //
                // Only the order of the runs changes. Each run is still bracketed by M4 before and M5
                // after, the laser is still off for every G0, and the set of engraved spans is
                // identical — this cannot burn anything the previous version did not.
                var reverse = leftToRight is false;
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    var pairIndex = reverse ? xs.Count - 2 - (i / 2) * 2 : i;
                    var from = reverse ? xs[pairIndex + 1] : xs[pairIndex];
                    var to = reverse ? xs[pairIndex] : xs[pairIndex + 1];

                    lines.Add($"G0 X{Fmt(from)} Y{Fmt(y)}");
                    lines.Add($"M4 S{Fmt(powerS)}");
                    lines.Add($"G1 X{Fmt(to)} Y{Fmt(y)} F{feed}");
                    lines.Add("M5");
                }

                leftToRight = !leftToRight;
            }
        }
    }

    private static bool SamePoint(Position first, Position second) =>
        Math.Abs(first.X - second.X) < 0.000001 && Math.Abs(first.Y - second.Y) < 0.000001;

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
