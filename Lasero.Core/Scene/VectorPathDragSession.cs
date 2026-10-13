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
    /// shapes' layer, colour and geometry-set metadata by contour index, so a drag in progress
    /// cannot temporarily move contours onto another layer.</summary>
    public IReadOnlyList<ImportedShape> BuildPreviewShapes(VectorPath path, RgbColor fallbackColor)
    {
        ArgumentNullException.ThrowIfNull(path);
        var reference = OriginalShapes.FirstOrDefault();
        var fallbackGeometrySetId = Guid.NewGuid();
        var distinctMetadata = OriginalShapes
            .Select(shape => (shape.GeometrySetId, shape.LayerId, shape.LayerColor, shape.PreferredMode))
            .Distinct().Count();
        if (path.Subpaths.Count != OriginalShapes.Count && distinctMetadata > 1)
            throw new InvalidOperationException(
                "Změna počtu obrysů u vícevrstvého objektu vyžaduje přiřazení vrstvy pro nové obrysy.");

        return path.Subpaths.Select((subpath, index) =>
        {
            var source = path.Subpaths.Count == OriginalShapes.Count ? OriginalShapes[index] : reference;
            return new ImportedShape
            {
                GeometrySetId = source?.GeometrySetId ?? fallbackGeometrySetId,
                LayerId = source?.LayerId ?? Guid.Empty,
                Points = subpath.Flatten(),
                IsClosed = subpath.IsClosed,
                LayerColor = source?.LayerColor ?? fallbackColor,
                PreferredMode = source?.PreferredMode ?? (subpath.IsClosed ? LayerMode.Fill : LayerMode.Cut),
            };
        }).ToList();
    }
}
