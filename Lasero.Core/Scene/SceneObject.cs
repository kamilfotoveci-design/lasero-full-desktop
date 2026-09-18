using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene;

/// <summary>
/// One movable/rotatable/scalable item on the design canvas — everything imported from one file
/// (SVG or raster), regardless of how many colors/shapes it contains, becomes a single SceneObject
/// so it moves as one piece (see SceneObjectFactory). Geometry is stored once in local space,
/// anchored at LocalPivot (that geometry's own bounding-box center); Transform maps it into world
/// (absolute mm) space on demand via GetWorldShapes/WorldBounds.
/// </summary>
public sealed partial class SceneObject : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required IReadOnlyList<ImportedShape> LocalShapes { get; set; }
    public required Position LocalPivot { get; init; }
    public required BoundingBox2D LocalBounds { get; init; }

    /// <summary>Set only for raster (PNG/JPG/BMP) imports — see SceneObjectFactory.FromRaster. LocalShapes
    /// for a raster object is just its placement outline (for canvas display/hit-testing); the actual
    /// engraving G-code is produced separately by RasterImporter from the source file + these options.
    /// Translation and scale are derived from Transform when output is generated; raster rotation and
    /// mirroring stay disabled until the image-processing pipeline supports them. DPI/power/threshold
    /// are still edited in the raster import flow.</summary>
    public string? RasterFilePath { get; init; }
    public RasterImportOptions? RasterOptions { get; init; }
    public bool IsRaster => RasterFilePath is not null;

    /// <summary>Set once "Odstranit pozadí" has pointed RasterFilePath at a generated transparent-
    /// background file — holds the untouched original import so "Obnovit pozadí" (and a project
    /// reload) can point RasterFilePath back at it. Null means RasterFilePath already IS the original
    /// (background removal was never run, or has been restored). Both display and G-code generation
    /// read whichever file RasterFilePath currently points at, so swapping this one field is the
    /// entire "apply/restore" operation — see BackgroundRemovalService for how the new file is
    /// produced and SceneViewModel.CommitBackgroundRemoval/RestoreBackground for the undo step.</summary>
    public string? OriginalRasterFilePath { get; init; }
    public bool HasBackgroundRemoved => OriginalRasterFilePath is not null;

    /// <summary>Set only for text created with the text tool. LocalShapes is then a cached render of
    /// this record: changing the wording, font or style re-renders the contours instead of leaving
    /// the operator with curves they can no longer edit. Objects loaded from projects saved before
    /// text became editable have no TextSource and stay plain curves, which is correct — there is no
    /// wording to recover from a flattened outline.</summary>
    public TextSource? Text { get; init; }
    public bool IsText => Text is not null;

    /// <summary>Set only for objects created/edited with the vector path (node edit) tool. LocalShapes
    /// is a cached flattening of this curve-preserving model (see the boundary comment at the top of
    /// VectorPath.cs) — editing a node re-flattens instead of trying to recover control points from a
    /// polyline. Objects without a VectorPath (rectangles, ellipses, imported SVGs, ...) stay plain
    /// curves and are not eligible for Node Edit mode this pass.</summary>
    public VectorPath? VectorPath { get; init; }
    public bool IsVectorPath => VectorPath is not null;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private ObjectTransform _transform = ObjectTransform.Identity;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isLocked;

    /// <summary>Distinct from IsVisible — a reference/alignment object can stay visible on the canvas
    /// without ever being included in the generated job. See SceneDocument.ToImportedDocument.</summary>
    [ObservableProperty] private bool _includeInOutput = true;

    /// <summary>Moves every contour using one layer color to another color without changing geometry.</summary>
    public bool ReplaceLayerColor(RgbColor source, RgbColor target)
    {
        var changed = false;
        LocalShapes = LocalShapes.Select(shape =>
        {
            if (!shape.LayerColor.IsApproximately(source)) return shape;
            changed = true;
            return shape with { LayerColor = target };
        }).ToList();
        if (changed) OnPropertyChanged(nameof(LocalShapes));
        return changed;
    }

    /// <summary>Moves geometry from one stable layer to another. Color is retained as a visual
    /// snapshot and legacy fallback, never as the primary identity.</summary>
    public bool ReplaceLayer(Guid sourceId, RgbColor legacySourceColor, LayerSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var changed = false;
        LocalShapes = LocalShapes.Select(shape =>
        {
            var matches = shape.LayerId != Guid.Empty
                ? shape.LayerId == sourceId
                : shape.LayerColor.IsApproximately(legacySourceColor);
            if (!matches) return shape;
            if (shape.LayerId == target.Id && shape.LayerColor.Equals(target.Color)) return shape;
            changed = true;
            return shape with { LayerId = target.Id, LayerColor = target.Color };
        }).ToList();
        if (changed) OnPropertyChanged(nameof(LocalShapes));
        return changed;
    }

    /// <summary>Assigns the complete logical object to one processing layer.</summary>
    public bool AssignToLayer(RgbColor target)
    {
        if (LocalShapes.Count == 0 || LocalShapes.All(shape => shape.LayerColor.IsApproximately(target)))
            return false;
        LocalShapes = LocalShapes.Select(shape => shape with { LayerColor = target }).ToList();
        OnPropertyChanged(nameof(LocalShapes));
        return true;
    }

    public bool AssignToLayer(LayerSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (LocalShapes.Count == 0 || LocalShapes.All(shape =>
                shape.LayerId == target.Id && shape.LayerColor.Equals(target.Color)))
            return false;
        LocalShapes = LocalShapes.Select(shape => shape with
        {
            LayerId = target.Id,
            LayerColor = target.Color,
        }).ToList();
        OnPropertyChanged(nameof(LocalShapes));
        return true;
    }

    /// <summary>Resolves imported/candidate layer identities to the canonical layers owned by the
    /// scene. Existing non-empty IDs that are not part of this import remain untouched.</summary>
    internal bool RemapLayers(
        IReadOnlyDictionary<Guid, LayerSettings> candidatesById,
        IReadOnlyList<(RgbColor Color, LayerSettings Layer)> candidatesByColor)
    {
        var changed = false;
        LocalShapes = LocalShapes.Select(shape =>
        {
            LayerSettings? target = null;
            if (shape.LayerId != Guid.Empty)
                candidatesById.TryGetValue(shape.LayerId, out target);
            else
                target = candidatesByColor.FirstOrDefault(item => item.Color.IsApproximately(shape.LayerColor)).Layer;

            if (target is null || (shape.LayerId == target.Id && shape.LayerColor.Equals(target.Color)))
                return shape;

            changed = true;
            return shape with { LayerId = target.Id, LayerColor = target.Color };
        }).ToList();

        if (changed) OnPropertyChanged(nameof(LocalShapes));
        return changed;
    }

    /// <summary>Requests a redraw when layer settings such as mode or visibility change.</summary>
    public void InvalidateLayerAppearance() => OnPropertyChanged(nameof(LocalShapes));

    /// <summary>This object's shapes with Transform applied — absolute mm, ready for ToolpathBuilder.</summary>
    public IReadOnlyList<ImportedShape> GetWorldShapes() => LocalShapes
        .Select(shape => shape with { Points = shape.Points.Select(p => Transform.Apply(p, LocalPivot)).ToList() })
        .ToList();

    /// <summary>This object's VectorPath with Transform applied to every node's anchor AND both
    /// handles — the curve-preserving counterpart to GetWorldShapes, used when an operation (Group)
    /// needs to combine several objects' editable geometry into one shared coordinate space rather
    /// than just their already-flattened polylines. Handles are stored as absolute local positions,
    /// the same convention Anchor uses (see VectorNode's own doc comment and DrawHandle in
    /// SceneCanvas.VectorPathTool.cs), so the identical Transform.Apply call already used for anchors
    /// applies to them unchanged — no separate vector/direction transform is needed. Null when this
    /// object has no VectorPath (not node-editable).</summary>
    public VectorPath? GetWorldVectorPath()
    {
        if (VectorPath is null) return null;
        Position ToWorld(Position local) => Transform.Apply(local, LocalPivot);

        var subpaths = VectorPath.Subpaths.Select(subpath => subpath with
        {
            Nodes = subpath.Nodes.Select(node => new VectorNode(
                ToWorld(node.Anchor),
                node.HandleIn is { } hi ? ToWorld(hi) : null,
                node.HandleOut is { } ho ? ToWorld(ho) : null,
                node.Type)).ToList(),
        }).ToList();

        return VectorPath with { Subpaths = subpaths };
    }

    /// <summary>Axis-aligned world-space bounding box — recomputed from LocalBounds' 4 corners since
    /// rotation means it generally isn't just LocalBounds shifted by Transform's translation.</summary>
    public BoundingBox2D WorldBounds()
    {
        var corners = new[]
        {
            new Position(LocalBounds.MinX, LocalBounds.MinY, 0),
            new Position(LocalBounds.MaxX, LocalBounds.MinY, 0),
            new Position(LocalBounds.MaxX, LocalBounds.MaxY, 0),
            new Position(LocalBounds.MinX, LocalBounds.MaxY, 0),
        };

        var box = BoundingBox2D.Empty;
        foreach (var corner in corners)
        {
            var world = Transform.Apply(corner, LocalPivot);
            box = box.Include(world.X, world.Y);
        }
        return box;
    }

    /// <summary>Builds raster planner options matching the current canvas placement. The canvas keeps
    /// raster rotation and mirroring disabled, so its transformed world bounds map directly to the
    /// planner's axis-aligned width, height and lower-left offset.</summary>
    public RasterImportOptions? BuildRasterOutputOptions(double offsetX = 0, double offsetY = 0)
    {
        if (RasterOptions is null || RasterFilePath is null) return null;

        var bounds = WorldBounds();
        return RasterOptions with
        {
            TargetWidthMm = LocalBounds.Width * Math.Abs(Transform.ScaleX),
            TargetHeightMm = LocalBounds.Height * Math.Abs(Transform.ScaleY),
            OffsetX = bounds.MinX + offsetX,
            OffsetY = bounds.MinY + offsetY,
        };
    }

    /// <summary>Builds placement-aware raster options and overlays the shared editable bitmap layer.
    /// This makes the layer panel the single source of truth for the parameters sent to GRBL.</summary>
    public RasterImportOptions? BuildRasterOutputOptions(LayerSettings layer, double offsetX = 0, double offsetY = 0)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!layer.IsEnabled) return null;
        var options = BuildRasterOutputOptions(offsetX, offsetY);
        if (options is null) return null;

        return options with
        {
            FeedRatePerMinute = layer.Speed,
            MaxPower = layer.Power,
            Passes = layer.Passes,
            Dpi = layer.FillLineIntervalMm > 0 ? 25.4 / layer.FillLineIntervalMm : options.Dpi,
        };
    }

    /// <summary>A copy with a new Id, sharing the same (immutable) local geometry — used by duplicate.</summary>
    public SceneObject Clone() => new()
    {
        LocalShapes = LocalShapes,
        LocalPivot = LocalPivot,
        LocalBounds = LocalBounds,
        RasterFilePath = RasterFilePath,
        RasterOptions = RasterOptions,
        OriginalRasterFilePath = OriginalRasterFilePath,
        Text = Text,
        VectorPath = VectorPath,
        Name = Name,
        Transform = Transform,
        IsVisible = IsVisible,
        IsLocked = IsLocked,
        IncludeInOutput = IncludeInOutput,
    };
}
