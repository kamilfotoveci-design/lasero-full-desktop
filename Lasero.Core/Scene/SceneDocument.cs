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
        var shapes = GetOutputShapes(offsetX, offsetY).Select(entry => entry.Shape).ToList();

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

    /// <summary>The world-space contours of every visible, included object together with the object
    /// that owns them, so callers can resolve layers with <see cref="LayerResolver"/>'s owner-aware
    /// rule (a bitmap's outline is never vector output).</summary>
    public IEnumerable<(SceneObject Owner, ImportedShape Shape)> GetOutputShapes(double offsetX = 0, double offsetY = 0) =>
        Objects
            .Where(o => o.IsVisible && o.IncludeInOutput)
            // Guid.Empty means "one compound path per object"; keep that meaning once shapes from
            // several objects are pooled, so the fill contract never merges unrelated objects.
            .SelectMany(o => o.GetWorldShapes().Select(shape => (Owner: o, Shape: shape with
            {
                GeometrySetId = shape.GeometrySetId == Guid.Empty ? o.Id : shape.GeometrySetId,
                Points = shape.Points
                    .Select(point => new Lasero.Core.Grbl.Position(point.X + offsetX, point.Y + offsetY, point.Z))
                    .ToList(),
            })));

    /// <summary>Whether any contour of <paramref name="item"/> belongs to <paramref name="layer"/>
    /// under the one resolution rule (<see cref="LayerResolver"/>).</summary>
    public bool UsesLayer(SceneObject item, LayerSettings layer) => LayerResolver.UsesLayer(item, layer, Layers);

    /// <summary>Adds each of <paramref name="candidates"/> not already matched (by id, or by colour + mode) by an existing
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
            // Colour + mode identify an operation (LightBurn model): a fill and an outline in the same
            // colour are two different operations, so the same colour only shares a layer when the mode
            // also agrees. A colour-only match would put a later outline import on a same-colour fill
            // layer and the outline would be scan-filled.
            var resolved = Layers.FirstOrDefault(layer => layer.Id == candidate.Id)
                ?? Layers.FirstOrDefault(layer => layer.Mode == candidate.Mode && layer.IsRaster == candidate.IsRaster
                    && layer.Color.IsApproximately(candidate.Color));
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
