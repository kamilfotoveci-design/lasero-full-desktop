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
    public required Position LocalPivot { get; set; }
    public required BoundingBox2D LocalBounds { get; set; }

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
    public VectorPath? VectorPath { get; set; }
    public bool IsVectorPath => VectorPath is not null;

    /// <summary>Present only while this outline follows its source objects.</summary>
    public OffsetLink? OffsetLink { get; set; }

    /// <summary>Refreshes only the cached outline of a linked offset. Object identity must stay
    /// stable: the scene command that inserted this object owns the same instance for Undo.</summary>
    public void RefreshDerivedVectorPath(VectorPath path)
    {
        if (OffsetLink is null || VectorPath is null)
            throw new InvalidOperationException("Objekt není prepojený offset.");
        var rebuilt = VectorPathSceneFactory.Rebuild(this, path, preserveOffsetLink: true);
        LocalShapes = rebuilt.LocalShapes;
        LocalBounds = rebuilt.LocalBounds;
        LocalPivot = rebuilt.LocalPivot;
        VectorPath = rebuilt.VectorPath;
        // The refreshed path is already in world millimetres, so any placement the output carried
        // (a nudge, align or flip applied to the output alone) must not be applied on top of it.
        Transform = ObjectTransform.Identity;
        OnPropertyChanged(nameof(LocalShapes));
        OnPropertyChanged(nameof(LocalBounds));
        OnPropertyChanged(nameof(LocalPivot));
        OnPropertyChanged(nameof(VectorPath));
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private ObjectTransform _transform = ObjectTransform.Identity;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isLocked;

    /// <summary>Shared identity of an editor group. Members remain separate editable objects with
    /// their own layers, curves, text and output settings; the canvas selects them together.</summary>
    [ObservableProperty] private Guid? _groupId;
    /// <summary>Earlier containing groups, innermost first. Ungroup releases only the current
    /// outer group and restores the previous membership without flattening any child.</summary>
    public IReadOnlyList<Guid> NestedGroupIds { get; set; } = [];

    /// <summary>Distinct from IsVisible — a reference/alignment object can stay visible on the canvas
    /// without ever being included in the generated job. See SceneDocument.ToImportedDocument.</summary>
    [ObservableProperty] private bool _includeInOutput = true;

    /// <summary>Moves every contour using one layer color to another color without changing geometry.</summary>
    public bool ReplaceLayerColor(RgbColor source, RgbColor target)
    {
        var changed = false;
        var rewritten = LocalShapes.Select(shape =>
        {
            if (!shape.LayerColor.IsApproximately(source)) return shape;
            changed = true;
            return shape with { LayerColor = target };
        }).ToList();
        if (!changed) return false; // keep the list instance: undo/redo and callers rely on its identity
        LocalShapes = rewritten;
        OnPropertyChanged(nameof(LocalShapes));
        return true;
    }

    /// <summary>Moves geometry from one stable layer to another. Color is retained as a visual
    /// snapshot and legacy fallback, never as the primary identity.</summary>
    public bool ReplaceLayer(Guid sourceId, RgbColor legacySourceColor, LayerSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var changed = false;
        var rewritten = LocalShapes.Select(shape =>
        {
            var matches = shape.LayerId != Guid.Empty
                ? shape.LayerId == sourceId
                : shape.LayerColor.IsApproximately(legacySourceColor);
            if (!matches) return shape;
            if (shape.LayerId == target.Id && shape.LayerColor.Equals(target.Color)) return shape;
            changed = true;
            return shape with { LayerId = target.Id, LayerColor = target.Color };
        }).ToList();
        if (!changed) return false;
        LocalShapes = rewritten;
        OnPropertyChanged(nameof(LocalShapes));
        return true;
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

    /// <summary>Puts back a previously captured shape list and tells listeners the geometry changed -
    /// the undo half of any command that rewrote LocalShapes in place (see AssignObjectsToLayerCommand).</summary>
    public void RestoreLocalShapes(IReadOnlyList<ImportedShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        LocalShapes = shapes;
        OnPropertyChanged(nameof(LocalShapes));
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
        var rewritten = LocalShapes.Select(shape =>
        {
            LayerSettings? target = null;
            if (shape.LayerId != Guid.Empty)
                candidatesById.TryGetValue(shape.LayerId, out target);
            else
                target = candidatesByColor.FirstOrDefault(item =>
                             item.Layer.Mode == shape.PreferredMode && item.Color.IsApproximately(shape.LayerColor)).Layer
                         ?? candidatesByColor.FirstOrDefault(item => item.Color.IsApproximately(shape.LayerColor)).Layer;

            if (target is null || (shape.LayerId == target.Id && shape.LayerColor.Equals(target.Color)))
                return shape;

            changed = true;
            return shape with { LayerId = target.Id, LayerColor = target.Color };
        }).ToList();

        if (!changed) return false;
        LocalShapes = rewritten;
        OnPropertyChanged(nameof(LocalShapes));
        return true;
    }

    /// <summary>Requests a redraw when layer settings such as mode or visibility change.</summary>
    public void InvalidateLayerAppearance() => OnPropertyChanged(nameof(LocalShapes));

    /// <summary>This object's shapes with Transform applied — absolute mm, ready for ToolpathBuilder.</summary>
    public IReadOnlyList<ImportedShape> GetWorldShapes() => ShapesForWorldOutput()
        .Select(shape => shape with { Points = shape.Points.Select(p => Transform.Apply(p, LocalPivot)).ToList() })
        .ToList();

    private VectorPath? _refinedFor;
    private double _refinedTolerance;
    private IReadOnlyList<ImportedShape>? _refinedSource;
    private IReadOnlyList<ImportedShape>? _refinedShapes;

    /// <summary>LocalShapes are flattened at 0.05 mm in LOCAL units, so an object enlarged x10 would
    /// carry 0.5 mm chord error into the toolpath. For editable curves the local flattening tolerance
    /// is tightened by the object's scale so the error stays 0.05 mm in the world. The result is cached
    /// per (path, shapes, tolerance); when the cached shapes do not line up with the path, or the
    /// object is not enlarged, the plain LocalShapes are used.</summary>
    private IReadOnlyList<ImportedShape> ShapesForWorldOutput()
    {
        var path = VectorPath;
        var scale = Math.Max(Math.Abs(Transform.ScaleX), Math.Abs(Transform.ScaleY));
        if (path is null || scale <= 1.0001 || double.IsNaN(scale) || double.IsInfinity(scale))
            return LocalShapes;

        var tolerance = Math.Max(VectorPath.DefaultFlattenToleranceMm / scale, 0.0002);
        if (ReferenceEquals(_refinedFor, path) && ReferenceEquals(_refinedSource, LocalShapes)
            && _refinedShapes is not null
            && _refinedTolerance <= tolerance && _refinedTolerance >= tolerance * 0.25)
            return _refinedShapes;

        if (!LocalShapesAreFlatteningOf(path)) return LocalShapes;

        var contours = VectorPathSceneFactory.FlattenRetained(path, tolerance);
        if (contours.Count != LocalShapes.Count) return LocalShapes;

        var refined = LocalShapes.Select((shape, index) => shape with { Points = contours[index] }).ToList();
        _refinedFor = path;
        _refinedSource = LocalShapes;
        _refinedTolerance = tolerance;
        _refinedShapes = refined;
        return refined;
    }

    private VectorPath? _alignedPath;
    private IReadOnlyList<ImportedShape>? _alignedShapes;
    private bool _alignedResult;

    /// <summary>True when LocalShapes really are the stock flattening of the path. A live node-drag
    /// preview swaps in shapes of moved nodes while VectorPath still holds the old nodes; refining
    /// from the path then would draw the pre-drag geometry.</summary>
    private bool LocalShapesAreFlatteningOf(VectorPath path)
    {
        if (ReferenceEquals(_alignedPath, path) && ReferenceEquals(_alignedShapes, LocalShapes))
            return _alignedResult;

        var coarse = VectorPathSceneFactory.FlattenRetained(path, VectorPath.DefaultFlattenToleranceMm);
        var aligned = coarse.Count == LocalShapes.Count;
        for (var s = 0; aligned && s < coarse.Count; s++)
        {
            var expected = coarse[s];
            var actual = LocalShapes[s].Points;
            if (expected.Count != actual.Count) { aligned = false; break; }
            for (var p = 0; p < expected.Count; p++)
            {
                if (Math.Abs(expected[p].X - actual[p].X) > 1e-6 || Math.Abs(expected[p].Y - actual[p].Y) > 1e-6)
                {
                    aligned = false;
                    break;
                }
            }
        }

        _alignedPath = path;
        _alignedShapes = LocalShapes;
        _alignedResult = aligned;
        return aligned;
    }

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

    private ObjectTransform _geometricBoundsTransform;
    private IReadOnlyList<ImportedShape>? _geometricBoundsShapes;
    private VectorPath? _geometricBoundsPath;
    private BoundingBox2D _geometricBounds;

    /// <summary>Axis-aligned world box of the drawn geometry itself (every transformed contour point),
    /// which is what align, distribute and flip need: <see cref="WorldBounds"/> rotates the local box's
    /// corners, so a rotated circle reports a box larger than the circle. Cached until the transform,
    /// the contours or the path change. Falls back to <see cref="WorldBounds"/> for an object with no
    /// contour points.</summary>
    public BoundingBox2D GeometricWorldBounds()
    {
        if (_geometricBoundsShapes is not null && ReferenceEquals(_geometricBoundsShapes, LocalShapes) &&
            ReferenceEquals(_geometricBoundsPath, VectorPath) && _geometricBoundsTransform.Equals(Transform))
            return _geometricBounds;

        var box = BoundingBox2D.Empty;
        foreach (var shape in GetWorldShapes())
            foreach (var point in shape.Points)
                box = box.Include(point.X, point.Y);
        if (box.IsEmpty) box = WorldBounds();

        _geometricBoundsShapes = LocalShapes;
        _geometricBoundsPath = VectorPath;
        _geometricBoundsTransform = Transform;
        _geometricBounds = box;
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

    /// <summary>Why this bitmap cannot be engraved as placed, or null. The raster planner scans
    /// axis-aligned rows only, so a rotated or mirrored bitmap would silently burn upright and unflipped;
    /// output is withheld with this message instead.</summary>
    public string? RasterOutputBlockReason
    {
        get
        {
            if (!IsRaster) return null;
            var turns = Math.Abs(Transform.RotationDeg) % 360;
            var rotated = turns > 0.01 && Math.Abs(turns - 360) > 0.01;
            return rotated || Transform.ScaleX < 0 || Transform.ScaleY < 0
                ? $"Bitmapa „{Name}“ je otočená nebo převrácená – výstup není podporován. Je potřeba ji vrátit do původní polohy."
                : null;
        }
    }

    /// <summary>Raster options for re-opening the import dialog: the stored options at the bitmap's
    /// CURRENT canvas size (not the size it was imported at). The height is only set explicitly when the
    /// current proportions differ from the picture's own, so the dialog's aspect lock reflects reality.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public RasterImportOptions? BuildRasterEditOptions()
    {
        if (RasterOptions is null || RasterFilePath is null) return null;
        var width = LocalBounds.Width * Math.Abs(Transform.ScaleX);
        var height = LocalBounds.Height * Math.Abs(Transform.ScaleY);
        if (width <= 0 || height <= 0) return RasterOptions;
        var natural = RasterImporter.GetPlacedSizeMm(RasterFilePath, width).HeightMm;
        var uniform = Math.Abs(natural - height) <= 1e-3 * Math.Max(height, 1);
        return RasterOptions with { TargetWidthMm = width, TargetHeightMm = uniform ? null : height };
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

    /// <summary>Centre of <see cref="LocalBounds"/>, in the same local space as <see cref="LocalPivot"/>.</summary>
    public Position LocalBoundsCenter => new(
        (LocalBounds.MinX + LocalBounds.MaxX) / 2,
        (LocalBounds.MinY + LocalBounds.MaxY) / 2,
        LocalPivot.Z);

    /// <summary>
    /// Whether <see cref="LocalPivot"/> is the centre of <see cref="LocalBounds"/>. Every factory keeps
    /// this true and everything that draws or drags a selection depends on it: edge-midpoint grips sit
    /// at the pivot's coordinate on the axis they do not move, the pivot marker and rotate grip hang off
    /// it, and rotate, flip and resize all scale about it. Off-centre, the grips are drawn on the
    /// pivot's own X/Y lines and rotating orbits the shape around that point. Within a millionth of a
    /// millimetre counts as centred, far below anything a laser can place.
    /// </summary>
    public bool IsPivotAtBoundsCenter
    {
        get
        {
            var centre = LocalBoundsCenter;
            return Math.Abs(centre.X - LocalPivot.X) <= PivotTolerance
                && Math.Abs(centre.Y - LocalPivot.Y) <= PivotTolerance;
        }
    }

    private const double PivotTolerance = 1e-6;

    /// <summary>
    /// This object with its pivot moved to the centre of its bounds and Transform adjusted so nothing
    /// moves on the canvas (same Id, same world geometry). Returns this object unchanged when the pivot
    /// is already there. Heals objects saved while the pen tool still built them with a pivot at the
    /// document origin.
    /// </summary>
    public SceneObject WithPivotAtBoundsCenter()
    {
        if (IsPivotAtBoundsCenter) return this;
        var pivot = LocalBoundsCenter;
        return CopyWith(Id, pivot, Transform.WithPivotMoved(LocalPivot, pivot));
    }

    /// <summary>
    /// For an editable path, makes sure the cached LocalShapes really are the flattening of
    /// <see cref="VectorPath"/>: same contour count and closedness, same bounds. A project edited by
    /// hand, written by an older build or partly corrupted can disagree, and every node edit would then
    /// misalign layer metadata or jump. When they disagree the shapes are re-derived from the nodes
    /// (layer/mode metadata is kept per contour where counts allow, else from the first contour) and
    /// LocalBounds is recomputed; the pivot is NOT touched here, so call <see cref="WithPivotAtBoundsCenter"/>
    /// afterwards. Returns true when anything was changed. No-op for objects without a VectorPath.
    /// </summary>
    public bool ReconcileVectorGeometry()
    {
        if (VectorPath is not { } path || path.Subpaths.Count == 0) return false;

        var contours = VectorPathSceneFactory.FlattenRetained(path, VectorPath.DefaultFlattenToleranceMm);
        if (contours.Count == 0) return false;
        var retained = VectorPathSceneFactory.RetainedSubpathIndices(path, VectorPath.DefaultFlattenToleranceMm);

        var bounds = BoundingBox2D.Empty;
        foreach (var contour in contours)
            foreach (var point in contour)
                bounds = bounds.Include(point.X, point.Y);

        const double boundsToleranceMm = 0.01;
        var consistent = contours.Count == LocalShapes.Count
            && Enumerable.Range(0, contours.Count).All(index =>
                LocalShapes[index].IsClosed == path.Subpaths[retained[index]].IsClosed)
            && Math.Abs(bounds.MinX - LocalBounds.MinX) <= boundsToleranceMm
            && Math.Abs(bounds.MinY - LocalBounds.MinY) <= boundsToleranceMm
            && Math.Abs(bounds.MaxX - LocalBounds.MaxX) <= boundsToleranceMm
            && Math.Abs(bounds.MaxY - LocalBounds.MaxY) <= boundsToleranceMm;
        if (consistent) return false;

        var fallback = LocalShapes.FirstOrDefault();
        var rebuilt = new List<ImportedShape>(contours.Count);
        for (var index = 0; index < contours.Count; index++)
        {
            var template = contours.Count == LocalShapes.Count ? LocalShapes[index] : fallback;
            var closed = path.Subpaths[retained[index]].IsClosed;
            rebuilt.Add(new ImportedShape
            {
                GeometrySetId = template?.GeometrySetId ?? Guid.NewGuid(),
                LayerId = template?.LayerId ?? Guid.Empty,
                LayerColor = template?.LayerColor ?? RgbColor.Black,
                PreferredMode = template?.PreferredMode ?? (closed ? LayerMode.Fill : LayerMode.Cut),
                Points = contours[index],
                IsClosed = closed,
            });
        }

        LocalShapes = rebuilt;
        LocalBounds = bounds;
        OnPropertyChanged(nameof(LocalShapes));
        OnPropertyChanged(nameof(LocalBounds));
        return true;
    }

    /// <summary>A copy with a new Id, sharing the same (immutable) local geometry — used by duplicate.</summary>
    public SceneObject Clone() => CopyWith(Guid.NewGuid(), LocalPivot, Transform);

    private SceneObject CopyWith(Guid id, Position pivot, ObjectTransform transform) => new()
    {
        Id = id,
        LocalShapes = LocalShapes,
        LocalPivot = pivot,
        LocalBounds = LocalBounds,
        RasterFilePath = RasterFilePath,
        RasterOptions = RasterOptions,
        OriginalRasterFilePath = OriginalRasterFilePath,
        Text = Text,
        VectorPath = VectorPath,
        OffsetLink = id == Id ? OffsetLink : null,
        Name = Name,
        Transform = transform,
        IsVisible = IsVisible,
        IsLocked = IsLocked,
        IncludeInOutput = IncludeInOutput,
        GroupId = GroupId,
        NestedGroupIds = NestedGroupIds.ToArray(),
    };
}
