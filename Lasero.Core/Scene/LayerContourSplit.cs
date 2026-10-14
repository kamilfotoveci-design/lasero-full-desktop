using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>
/// Divides one object into "its contours on a given layer" and "the rest", which deleting a layer
/// and selecting a layer's shapes both need for objects whose contours span several layers.
/// Only geometry with a 1:1 contour-to-subpath mapping can be divided; text and bitmaps cannot.
/// </summary>
public static class LayerContourSplit
{
    public static bool OnLayer(SceneObject item, int index, LayerSettings layer, IReadOnlyList<LayerSettings> layers) =>
        ReferenceEquals(LayerResolver.Resolve(item, item.LocalShapes[index], layers), layer);

    /// <summary>Indices of <paramref name="item"/>'s contours that do (or do not) belong to the layer.</summary>
    public static List<int> Indices(SceneObject item, LayerSettings layer, IReadOnlyList<LayerSettings> layers, bool onLayer) =>
        Enumerable.Range(0, item.LocalShapes.Count)
            .Where(index => OnLayer(item, index, layer, layers) == onLayer)
            .ToList();

    /// <summary>True when the object lies wholly on the layer, wholly off it, or can be divided safely.</summary>
    public static bool CanSplit(SceneObject item, LayerSettings layer, IReadOnlyList<LayerSettings> layers)
    {
        var on = Indices(item, layer, layers, onLayer: true).Count;
        return on == 0 || on == item.LocalShapes.Count || IsDivisible(item);
    }

    public static bool IsDivisible(SceneObject item) =>
        !item.IsRaster && item.Text is null &&
        (item.VectorPath is null || item.VectorPath.Subpaths.Count == item.LocalShapes.Count);

    /// <summary>A copy of <paramref name="item"/> holding only the contours at <paramref name="indices"/>,
    /// under <paramref name="id"/>. Placement, group membership, visibility and lock carry over; the
    /// pivot moves to the kept contours' centre with the transform compensated, so nothing moves on
    /// the canvas. Null when nothing is kept; the item itself when everything is kept under its own id.</summary>
    public static SceneObject? Keep(SceneObject item, IReadOnlyList<int> indices, Guid id, bool keepOffsetLink)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (indices.Count == 0) return null;
        if (indices.Count == item.LocalShapes.Count && id == item.Id) return item;
        if (!IsDivisible(item))
            throw new InvalidOperationException("Editable source cannot be split safely by layer.");

        var shapes = indices.Select(index => item.LocalShapes[index]).ToList();
        var bounds = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var point in shape.Points)
                bounds = bounds.Include(point.X, point.Y);
        if (bounds.IsEmpty) throw new InvalidOperationException("Remaining layer geometry is empty.");
        var pivot = new Position((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, item.LocalPivot.Z);
        return new SceneObject
        {
            Id = id,
            GroupId = item.GroupId,
            NestedGroupIds = item.NestedGroupIds.ToArray(),
            Name = item.Name,
            LocalShapes = shapes,
            LocalBounds = bounds,
            LocalPivot = pivot,
            Transform = item.Transform.WithPivotMoved(item.LocalPivot, pivot),
            VectorPath = item.VectorPath is null ? null : new VectorPath
            {
                Subpaths = indices.Select(index => item.VectorPath!.Subpaths[index]).ToList(),
            },
            IsVisible = item.IsVisible,
            IsLocked = item.IsLocked,
            IncludeInOutput = item.IncludeInOutput,
            OffsetLink = keepOffsetLink ? item.OffsetLink : null,
        };
    }
}
