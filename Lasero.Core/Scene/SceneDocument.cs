using System.Collections.ObjectModel;
using Lasero.Core.GCode;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>
/// The whole design workspace — every SceneObject placed on the sheet, plus shared processing layers
/// identified by stable IDs. Color is presentation and a one-time legacy/import matching hint.
/// Objects/Layers order is z-order
/// (last = topmost / most-recently-added), matching how they'll render on a WPF Canvas.
/// </summary>
public sealed class SceneDocument
{
    public ObservableCollection<SceneObject> Objects { get; } = new();
    public ObservableCollection<LayerSettings> Layers { get; } = new();

    /// <summary>Flattens every visible object's transformed geometry into one ImportedDocument —
    /// the bridge from scene geometry into the GRBL-range-aware ToolpathBuilder.</summary>
    public ImportedDocument ToImportedDocument(double offsetX = 0, double offsetY = 0)
    {
        var shapes = Objects
            .Where(o => o.IsVisible && o.IncludeInOutput)
            // Guid.Empty means "one compound path per object"; keep that meaning once shapes from
            // several objects are pooled, so the fill contract never merges unrelated objects.
            .SelectMany(o => o.GetWorldShapes().Select(shape =>
                shape.GeometrySetId == Guid.Empty ? shape with { GeometrySetId = o.Id } : shape))
            .Select(shape => shape with
            {
                Points = shape.Points
                    .Select(point => new Lasero.Core.Grbl.Position(point.X + offsetX, point.Y + offsetY, point.Z))
                    .ToList()
            })
            .ToList();

        var bbox = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var p in shape.Points)
                bbox = bbox.Include(p.X, p.Y);

        return new ImportedDocument
        {
            Shapes = shapes,
            Layers = Layers.ToList(),
            BoundingBox = bbox,
        };
    }

    /// <summary>Adds each of <paramref name="candidates"/> not already matched (by color) by an existing
    /// layer — used when a new object is added, so a second red-stroke import shares the existing "Rez"
    /// layer instead of creating a duplicate. Returns exactly the layers that were newly added, so
    /// AddObjectCommand can remove exactly those on undo.</summary>
    public IReadOnlyList<LayerSettings> EnsureLayers(IReadOnlyList<LayerSettings> candidates)
        => EnsureLayers(candidates, []);

    /// <summary>Ensures candidate layers exist and rewrites imported geometry to the canonical
    /// scene layer IDs. Color is used only once as a legacy/import migration hint.</summary>
    public IReadOnlyList<LayerSettings> EnsureLayers(
        IReadOnlyList<LayerSettings> candidates,
        IReadOnlyList<SceneObject> objects)
    {
        var added = new List<LayerSettings>();
        var candidatesById = new Dictionary<Guid, LayerSettings>();
        var candidatesByColor = new List<(RgbColor Color, LayerSettings Layer)>();
        foreach (var candidate in candidates)
        {
            var resolved = Layers.FirstOrDefault(layer => layer.Id == candidate.Id)
                ?? Layers.FirstOrDefault(layer => layer.Color.IsApproximately(candidate.Color));
            if (resolved is null)
            {
                resolved = candidate;
                Layers.Add(resolved);
                added.Add(resolved);
            }

            candidatesById[candidate.Id] = resolved;
            candidatesByColor.Add((candidate.Color, resolved));
        }

        foreach (var item in objects)
            item.RemapLayers(candidatesById, candidatesByColor);

        return added;
    }
}
