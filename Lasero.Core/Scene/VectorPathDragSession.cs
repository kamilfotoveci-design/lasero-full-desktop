using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>
/// Owns the mutation-safety contract for one Node Edit drag gesture on a VectorPath SceneObject:
/// capture an immutable pre-drag snapshot of LocalShapes, then build every live-preview frame from
/// that snapshot's layer/geometry-set identity instead of whatever the previous preview frame (or a
/// hardcoded default) happened to leave behind. See SceneCanvas.VectorPathTool.cs for the caller —
/// it restores <see cref="OriginalShapes"/> onto the live object before Cancel repaints and again
/// immediately before a commit hands the object to VectorPathSceneFactory.Rebuild/ReplaceObjectsCommand,
/// so neither the undo snapshot nor the rebuilt object's LayerId/GeometrySetId can be corrupted by a
/// drag that never finishes, or by Rebuild reading a preview frame instead of the true original.
/// </summary>
public sealed class VectorPathDragSession(IReadOnlyList<ImportedShape> originalShapes)
{
    public IReadOnlyList<ImportedShape> OriginalShapes { get; } = originalShapes;

    /// <summary>Flattens <paramref name="path"/> into live-preview shapes, carrying the pre-drag
    /// shapes' layer id/color/geometry-set uniformly onto every resulting shape — the same
    /// "whole object, one layer" contract VectorPathSceneFactory.Create/Rebuild already use for a
    /// vector-path object, so a drag in progress never shows (or risks persisting) unassigned
    /// metadata just because the node count or open/closed state changed mid-drag.</summary>
    public IReadOnlyList<ImportedShape> BuildPreviewShapes(VectorPath path, RgbColor fallbackColor)
    {
        ArgumentNullException.ThrowIfNull(path);
        var reference = OriginalShapes.FirstOrDefault();
        var color = reference?.LayerColor ?? fallbackColor;
        var layerId = reference?.LayerId ?? Guid.Empty;
        var geometrySetId = reference?.GeometrySetId ?? Guid.NewGuid();

        return path.Subpaths.Select(subpath => new ImportedShape
        {
            GeometrySetId = geometrySetId,
            LayerId = layerId,
            Points = subpath.Flatten(),
            IsClosed = subpath.IsClosed,
            LayerColor = color,
            PreferredMode = subpath.IsClosed ? LayerMode.Fill : LayerMode.Cut,
        }).ToList();
    }
}
