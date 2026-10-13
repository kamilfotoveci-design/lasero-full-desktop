using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Removes only contours assigned to the deleted layer. Mixed-layer objects keep their
/// other contours, placement and editable subpaths; Undo restores the original object instances.</summary>
public sealed class DeleteLayerCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly LayerSettings _layer;
    private readonly int _layerIndex;
    private readonly List<(SceneObject Original, SceneObject? Remaining, int Index)> _affected;

    public DeleteLayerCommand(SceneDocument scene, LayerSettings layer)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layer);
        _scene = scene;
        _layer = layer;
        _layerIndex = scene.Layers.IndexOf(layer);
        if (_layerIndex < 0) throw new ArgumentException("Layer is not in the scene.", nameof(layer));
        _affected = scene.Objects.Select((item, index) => (Item: item, Index: index))
            .Where(entry => UsesLayer(entry.Item, layer))
            .Select(entry =>
            {
                if (!CanKeepOtherContours(entry.Item, layer))
                    throw new InvalidOperationException("Mixed editable source cannot be split safely by layer.");
                return (entry.Item, Remaining: KeepOtherContours(entry.Item, layer), entry.Index);
            })
            .ToList();
    }

    public void Do()
    {
        foreach (var (original, remaining, _) in _affected)
        {
            var index = _scene.Objects.IndexOf(original);
            if (index < 0) continue;
            if (remaining is null) _scene.Objects.RemoveAt(index);
            else _scene.Objects[index] = remaining;
        }
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
                if (current >= 0) _scene.Objects[current] = original;
            }
            else if (!_scene.Objects.Contains(original))
                _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), original);
        }
    }

    private static SceneObject? KeepOtherContours(SceneObject item, LayerSettings layer)
    {
        if (item.IsRaster) return null;
        var keptIndices = Enumerable.Range(0, item.LocalShapes.Count)
            .Where(index => !ShapeUsesLayer(item.LocalShapes[index], layer)).ToList();
        if (keptIndices.Count == 0) return null;
        if (keptIndices.Count == item.LocalShapes.Count) return item;
        if (item.Text is not null || item.VectorPath is not null && item.VectorPath.Subpaths.Count != item.LocalShapes.Count)
            throw new InvalidOperationException("Editable source cannot be split safely by layer.");

        var shapes = keptIndices.Select(index => item.LocalShapes[index]).ToList();
        var bounds = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var point in shape.Points)
                bounds = bounds.Include(point.X, point.Y);
        if (bounds.IsEmpty) throw new InvalidOperationException("Remaining layer geometry is empty.");
        var pivot = new Position((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, item.LocalPivot.Z);
        return new SceneObject
        {
            Id = item.Id,
            GroupId = item.GroupId,
            NestedGroupIds = item.NestedGroupIds.ToArray(),
            Name = item.Name,
            LocalShapes = shapes,
            LocalBounds = bounds,
            LocalPivot = pivot,
            Transform = item.Transform.WithPivotMoved(item.LocalPivot, pivot),
            VectorPath = item.VectorPath is null ? null : new VectorPath
            {
                Subpaths = keptIndices.Select(index => item.VectorPath!.Subpaths[index]).ToList(),
            },
            IsVisible = item.IsVisible,
            IsLocked = item.IsLocked,
            IncludeInOutput = item.IncludeInOutput,
        };
    }

    public static bool CanKeepOtherContours(SceneObject item, LayerSettings layer) =>
        !UsesLayer(item, layer) ||
        item.LocalShapes.All(shape => ShapeUsesLayer(shape, layer)) ||
        !item.IsRaster && item.Text is null &&
        (item.VectorPath is null || item.VectorPath.Subpaths.Count == item.LocalShapes.Count);

    private static bool ShapeUsesLayer(ImportedShape shape, LayerSettings layer) => shape.LayerId != Guid.Empty
        ? shape.LayerId == layer.Id
        : shape.LayerColor.IsApproximately(layer.Color);

    private static bool UsesLayer(SceneObject item, LayerSettings layer) =>
        layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty) ||
        item.LocalShapes.Any(shape => ShapeUsesLayer(shape, layer));
}
