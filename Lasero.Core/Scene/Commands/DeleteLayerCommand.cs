using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Removes the layer and only the contours assigned to it. Mixed-layer objects keep their
/// other contours, placement and editable subpaths; Undo restores the original object instances.
/// Like <see cref="DeleteObjectsCommand"/>, an object that disappears completely does not leave its
/// linked offset outputs as zombies: a live output is detached, a stale suspended one goes with it.</summary>
public sealed class DeleteLayerCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly LayerSettings _layer;
    private readonly int _layerIndex;
    private readonly List<(SceneObject Original, SceneObject? Remaining, int Index)> _affected;
    private readonly List<(SceneObject Object, OffsetLink Link)> _detached = [];

    public DeleteLayerCommand(SceneDocument scene, LayerSettings layer)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layer);
        _scene = scene;
        _layer = layer;
        _layerIndex = scene.Layers.IndexOf(layer);
        if (_layerIndex < 0) throw new ArgumentException("Layer is not in the scene.", nameof(layer));
        var layers = scene.Layers.ToList();
        _affected = scene.Objects.Select((item, index) => (Item: item, Index: index))
            .Where(entry => LayerResolver.UsesLayer(entry.Item, layer, layers))
            .Select(entry =>
            {
                if (!LayerContourSplit.CanSplit(entry.Item, layer, layers))
                    throw new InvalidOperationException("Mixed editable source cannot be split safely by layer.");
                var kept = entry.Item.IsRaster ? [] : LayerContourSplit.Indices(entry.Item, layer, layers, onLayer: false);
                // A linked offset result keeps its recipe: dropping it here would silently turn a live,
                // source-driven output into a frozen copy just because one of its contours left with a layer.
                return (entry.Item, Remaining: LayerContourSplit.Keep(entry.Item, kept, entry.Item.Id, keepOffsetLink: true), entry.Index);
            })
            .ToList();

        var deletedIds = _affected.Where(entry => entry.Remaining is null).Select(entry => entry.Original.Id).ToHashSet();
        var affectedObjects = _affected.Select(entry => entry.Original).ToHashSet();
        foreach (var (candidate, index) in scene.Objects.Select((item, index) => (item, index)))
        {
            if (affectedObjects.Contains(candidate) || candidate.OffsetLink is not { } link) continue;
            if (!link.SourceIds.Any(deletedIds.Contains)) continue;
            if (link.IsSuspended) _affected.Add((candidate, null, index));
            else _detached.Add((candidate, link));
        }
        // Ascending index order lets Undo reinsert every removed object at its recorded position.
        _affected.Sort((left, right) => left.Index.CompareTo(right.Index));
    }

    public void Do()
    {
        foreach (var (original, remaining, _) in _affected)
        {
            var index = _scene.Objects.IndexOf(original);
            if (index < 0) continue;
            if (remaining is null) _scene.Objects.RemoveAt(index);
            else if (!ReferenceEquals(remaining, original)) _scene.Objects[index] = remaining;
        }
        foreach (var (item, _) in _detached)
            item.OffsetLink = null;
        _scene.Layers.Remove(_layer);
    }

    public void Undo()
    {
        if (!_scene.Layers.Contains(_layer))
            _scene.Layers.Insert(Math.Min(_layerIndex, _scene.Layers.Count), _layer);
        foreach (var (original, remaining, index) in _affected)
        {
            if (remaining is not null)
            {
                var current = _scene.Objects.IndexOf(remaining);
                if (current >= 0 && !ReferenceEquals(remaining, original)) _scene.Objects[current] = original;
            }
            else if (!_scene.Objects.Contains(original))
                _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), original);
        }
        foreach (var (item, link) in _detached)
            item.OffsetLink = link;
    }

    public static bool CanKeepOtherContours(SceneObject item, LayerSettings layer, IReadOnlyList<LayerSettings> layers) =>
        LayerContourSplit.CanSplit(item, layer, layers);
}
