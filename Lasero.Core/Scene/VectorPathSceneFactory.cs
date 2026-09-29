using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>
/// Wraps a VectorPath into a SceneObject, flattening it to ImportedShape.Points at creation/rebuild
/// time (see the boundary comment at the top of VectorPath.cs). Coordinates are kept in absolute mm
/// exactly as clicked on the canvas and Transform stays Identity, rather than the "recenter the
/// geometry around its own bounds" convention CreateRectangle/CreateEllipse use. A vector path is
/// built incrementally over many clicks, so there is no natural "drag rectangle" to center around;
/// recentering would also mean the object's Transform.X/Y stop matching where the operator actually
/// clicked, which the node-edit overlay would then have to un-do on every read.
///
/// The pivot is a different matter: it must be the centre of the bounds (SceneObject.IsPivotAtBoundsCenter),
/// because the selection grips, rotate, flip and resize all assume it. It used to stay at the origin,
/// which drew a selected path's edge grips, pivot marker and rotate grip on the document's X=0/Y=0
/// axes, nowhere near the path, and made rotate/flip orbit the origin. Moving only the pivot is free:
/// with an identity transform Apply(p, pivot) is p wherever the pivot sits, so the coordinates the
/// operator clicked are still the coordinates stored.
/// </summary>
public static class VectorPathSceneFactory
{
    public static SceneObject Create(VectorPath path, RgbColor color, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        var (shapes, bounds) = BuildShapes(path, color, geometrySetId: Guid.NewGuid());

        return new SceneObject
        {
            Name = name,
            LocalShapes = shapes,
            LocalBounds = bounds,
            LocalPivot = CenterOf(bounds),
            VectorPath = path,
            Transform = ObjectTransform.Identity,
        };
    }

    /// <summary>Re-flattens an existing vector-path object after a node edit (move/add/delete/convert
    /// node, drag handle, close path). Identity, transform, layer assignment and flags all carry
    /// over — only the geometry changes, the same contract VectorTextFactory.Rebuild uses for text.
    /// Editing moves the bounds, so the pivot follows their centre; Transform is adjusted for the pivot
    /// move (ObjectTransform.WithPivotMoved) so a rotated or scaled object does not jump on the canvas.</summary>
    public static SceneObject Rebuild(SceneObject existing, VectorPath path)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(path);
        if (existing.VectorPath is null)
            throw new InvalidOperationException("Objekt není editovatelná vektorová cesta.");

        var previous = existing.LocalShapes.FirstOrDefault();
        var color = previous?.LayerColor ?? RgbColor.Black;
        var geometrySetId = previous?.GeometrySetId ?? Guid.NewGuid();
        var (shapes, bounds) = BuildShapes(path, color, geometrySetId);
        var localShapes = previous is null
            ? shapes
            : shapes.Select(shape => shape with
            {
                LayerId = previous.LayerId,
                PreferredMode = previous.PreferredMode,
            }).ToList();

        var pivot = CenterOf(bounds);

        return new SceneObject
        {
            Id = existing.Id,
            Name = existing.Name,
            LocalShapes = localShapes,
            LocalBounds = bounds,
            LocalPivot = pivot,
            VectorPath = path,
            Transform = existing.Transform.WithPivotMoved(existing.LocalPivot, pivot),
            IsVisible = existing.IsVisible,
            IsLocked = existing.IsLocked,
            IncludeInOutput = existing.IncludeInOutput,
        };
    }

    private static Position CenterOf(BoundingBox2D bounds) =>
        new((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, 0);

    private static (IReadOnlyList<ImportedShape> Shapes, BoundingBox2D Bounds) BuildShapes(
        VectorPath path, RgbColor color, Guid geometrySetId)
    {
        var flattened = path.FlattenAll();
        var bounds = BoundingBox2D.Empty;
        var shapes = new List<ImportedShape>(flattened.Count);

        for (var index = 0; index < flattened.Count; index++)
        {
            var points = flattened[index];
            if (points.Count < 2) continue;
            foreach (var point in points) bounds = bounds.Include(point.X, point.Y);

            var isClosed = path.Subpaths[index].IsClosed;
            shapes.Add(new ImportedShape
            {
                GeometrySetId = geometrySetId,
                Points = points,
                IsClosed = isClosed,
                LayerColor = color,
                // Closed vector paths behave like every other closed primitive (rectangle, ellipse,
                // polygon): eligible for Fill/Fill+Line wherever the layer they land on says so. Open
                // paths only ever support Line — see SceneObject/LayerMode usage elsewhere; PreferredMode
                // here is only the "no layer decided yet" default, not an override of the layer's mode.
                PreferredMode = isClosed ? LayerMode.Fill : LayerMode.Cut,
            });
        }

        if (bounds.IsEmpty) bounds = new BoundingBox2D(0, 0, 0, 0);
        return (shapes, bounds);
    }
}
