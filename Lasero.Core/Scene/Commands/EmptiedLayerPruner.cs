using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>
/// The single layer-lifetime rule for commands that move or remove objects (delete, assign to layer,
/// replace for boolean/join/trace/convert-to-bitmap): a layer that had users before the command and
/// has none after it leaves the Layers list in the same undo step. A layer that was already empty
/// stays (the operator made it on purpose), and the raster layer follows the same rule, so it goes
/// only with its last bitmap.
/// <para>Usage: <see cref="CaptureBefore"/> at the start of Do, <see cref="PruneAfter"/> at its end,
/// <see cref="Restore"/> first thing in Undo, so the list is back in its post-Do shape before the
/// command undoes its own changes. A snapshot-based layer mutation (the palette swatch) calls only
/// CaptureBefore/PruneAfter; its snapshot already restores the list.</para>
/// </summary>
public sealed class EmptiedLayerPruner
{
    private readonly SceneDocument _scene;
    private HashSet<LayerSettings> _usedBefore = [];
    private List<(LayerSettings Layer, int Index)> _removed = [];

    public EmptiedLayerPruner(SceneDocument scene) => _scene = scene;

    public void CaptureBefore() => _usedBefore = LayerResolver.UsedLayers(_scene.Objects, _scene.Layers);

    public void PruneAfter()
    {
        var usedAfter = LayerResolver.UsedLayers(_scene.Objects, _scene.Layers);
        // Indices are taken from the list as it stands before any removal and restored in ascending
        // order, which puts every layer back exactly where it was.
        _removed = _scene.Layers
            .Select((layer, index) => (Layer: layer, Index: index))
            .Where(entry => _usedBefore.Contains(entry.Layer) && !usedAfter.Contains(entry.Layer))
            .ToList();
        foreach (var (layer, _) in _removed)
            _scene.Layers.Remove(layer);
    }

    public void Restore()
    {
        foreach (var (layer, index) in _removed)
            if (!_scene.Layers.Contains(layer))
                _scene.Layers.Insert(Math.Min(index, _scene.Layers.Count), layer);
        _removed = [];
    }
}
