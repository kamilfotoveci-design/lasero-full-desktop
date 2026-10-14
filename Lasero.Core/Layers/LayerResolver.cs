using Lasero.Core.Import;
using Lasero.Core.Scene;

namespace Lasero.Core.Layers;

/// <summary>
/// The one rule for which operation layer owns a contour. The canvas (visibility, lock, fill),
/// selection, layer usage counts, framing and the job all ask this, so what is drawn, selected,
/// framed and burned can never disagree.
/// <list type="number">
/// <item>A <see cref="ImportedShape.LayerId"/> that exists in the document wins.</item>
/// <item>Legacy colour-only contours (<see cref="Guid.Empty"/>) and orphans (an id the document no
/// longer has) fall back to the non-raster layer of the NEAREST colour within
/// <see cref="RgbColor.LayerMatchTolerance"/>; on a tie the contour's own mode wins, then list order.
/// It is always exactly one layer, so a contour can never be emitted twice.</item>
/// <item>A bitmap belongs to a raster layer: its own by id, otherwise the first raster layer.</item>
/// </list>
/// </summary>
public static class LayerResolver
{
    public static LayerSettings? Resolve(Guid layerId, RgbColor color, LayerMode? preferredMode, IReadOnlyList<LayerSettings> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layerId != Guid.Empty)
        {
            foreach (var layer in layers)
                if (layer.Id == layerId) return layer;
        }

        LayerSettings? best = null;
        var bestDistance = int.MaxValue;
        var bestModeMatches = false;
        foreach (var layer in layers)
        {
            if (layer.IsRaster || !layer.Color.IsApproximately(color)) continue;
            var distance = Math.Abs(layer.Color.R - color.R) + Math.Abs(layer.Color.G - color.G) + Math.Abs(layer.Color.B - color.B);
            var modeMatches = preferredMode is { } mode && layer.Mode == mode;
            if (distance < bestDistance || distance == bestDistance && modeMatches && !bestModeMatches)
            {
                best = layer;
                bestDistance = distance;
                bestModeMatches = modeMatches;
            }
        }
        return best;
    }

    public static LayerSettings? Resolve(ImportedShape shape, IReadOnlyList<LayerSettings> layers)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return Resolve(shape.LayerId, shape.LayerColor, shape.PreferredMode, layers);
    }

    /// <summary>Owner-aware form: a bitmap's outline is a placement aid, never vector output, so it
    /// resolves to a raster layer rather than to a vector layer that happens to share its grey.</summary>
    public static LayerSettings? Resolve(SceneObject owner, ImportedShape shape, IReadOnlyList<LayerSettings> layers)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsRaster) return Resolve(shape, layers);
        if (shape.LayerId != Guid.Empty)
        {
            foreach (var layer in layers)
                if (layer.Id == shape.LayerId) return layer;
        }
        foreach (var layer in layers)
            if (layer.IsRaster) return layer;
        return null;
    }

    public static bool UsesLayer(SceneObject item, LayerSettings layer, IReadOnlyList<LayerSettings> layers)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var shape in item.LocalShapes)
            if (ReferenceEquals(Resolve(item, shape, layers), layer)) return true;
        return false;
    }

    /// <summary>Every layer at least one of <paramref name="objects"/> resolves to.</summary>
    public static HashSet<LayerSettings> UsedLayers(IEnumerable<SceneObject> objects, IReadOnlyList<LayerSettings> layers)
    {
        var used = new HashSet<LayerSettings>(ReferenceEqualityComparer.Instance);
        foreach (var item in objects)
            foreach (var shape in item.LocalShapes)
                if (Resolve(item, shape, layers) is { } layer) used.Add(layer);
        return used;
    }
}
