using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Lasero.Core.Trace;

namespace Lasero.App.ViewModels;

/// <summary>
/// Owns the design workspace: every placed SceneObject, the shared per-color Layers list, selection,
/// and undo/redo. Importing a file ADDS an object (with a cascade offset so repeated imports don't
/// stack exactly on top of each other) rather than replacing the whole document, which is what makes
/// multi-object layout fall out of the vector-editor work almost for free.
/// </summary>
public partial class SceneViewModel : ObservableObject
{
    private const double CascadeOffsetMm = 10;
    private const double BooleanGeometryToleranceMm = 0.002;
    private const double MinimumUnitedAreaSquareMm = 0.00000001;

    private static readonly IVectorBooleanService BooleanService = Clipper2VectorBooleanService.Default;
    private static readonly IVectorOffsetService OffsetService = Clipper2VectorOffsetService.Default;

    private readonly SceneCommandStack _commandStack = new();
    private int _importCount;
    private List<SceneObject> _clipboard = [];

    public SceneDocument Scene { get; } = new();

    public ObservableCollection<SceneObject> Objects => Scene.Objects;
    public ObservableCollection<Lasero.Core.Layers.LayerSettings> Layers => Scene.Layers;

    /// <summary>Multi-select set the canvas maintains (rubber-band / shift-click). The property panel
    /// (round 1) only edits a single object, so Selected mirrors this when exactly one is selected.</summary>
    public ObservableCollection<SceneObject> SelectedObjects { get; } = new();

    public IReadOnlyList<RgbColor> LayerPalette { get; } =
    [
        // Second entry is KAMIL's own cap green (#394E3B, sampled from Assets/LaseroAvatar.png), not
        // a generic swatch pick — replaced the old orange at the user's request. Deliberately a much
        // darker, more muted green than the existing bright green further along (42,157,82), so the
        // two remain visually distinct rather than reading as a near-duplicate.
        new(18, 18, 18), new(214, 42, 42), new(57, 78, 59), new(232, 180, 0),
        new(42, 157, 82), new(0, 151, 167), new(31, 95, 204), new(92, 76, 196),
        new(179, 62, 153), new(117, 72, 42), new(98, 105, 113), new(173, 181, 189),
    ];

    [ObservableProperty] private SceneObject? _selected;
    [ObservableProperty] private Lasero.Core.Layers.LayerSettings? _selectedLayer;
    [ObservableProperty] private bool _lockAspectRatio = true;
    [ObservableProperty] private DesignerTool _activeTool = DesignerTool.Select;

    /// <summary>
    /// The shape the toolbar shape-tool button currently represents: whichever of the eight closed
    /// shapes was last activated, defaulting to Rectangle. Unlike ActiveTool this never reverts to
    /// Select - it is what a short click on the shape button activates, and what the shape picker
    /// shows as selected.
    /// </summary>
    [ObservableProperty] private DesignerTool _currentShapeTool = DesignerTool.Rectangle;

    /// <summary>Drives the OBRÁZEK section's inline progress UI while MainWindow sends the image to
    /// the authenticated Gemini service and awaits its result.</summary>
    [ObservableProperty] private bool _isRemovingBackground;
    [ObservableProperty] private bool _backgroundRemovalIsIndeterminate;
    [ObservableProperty] private string? _backgroundRemovalStatus;
    [ObservableProperty] private string? _backgroundRemovalError;
    [ObservableProperty] private double _backgroundRemovalProgress;

    public bool HasSelection => SelectedObjects.Count > 0;
    public bool HasMultipleSelection => SelectedObjects.Count > 1;
    public int SelectionCount => SelectedObjects.Count;
    public string SelectionSummary => SelectedObjects.Count switch
    {
        0 => "Žádný výběr",
        1 => $"1 objekt · {SelectedObjects[0].Name}",
        >= 2 and <= 4 => $"{SelectedObjects.Count} objekty ve výběru",
        _ => $"{SelectedObjects.Count} objektů ve výběru",
    };
    public bool CanEditSelectedPosition => Selected is { IsLocked: false };
    public bool CanTransformSelectedObject => Selected is { IsLocked: false };
    public bool CanRotateSelectedObject => Selected is { IsLocked: false, IsRaster: false };
    public bool IsSelectedLocked => Selected?.IsLocked == true;
    public bool CanGroupSelection => SelectedObjects.Count >= 2 && SelectedObjects.All(item => !item.IsRaster && !item.IsLocked);
    public bool CanUngroupSelection => SelectedObjects.Count == 1 && Selected is { IsRaster: false, IsLocked: false } item && item.LocalShapes.Count > 1;
    /// <summary>Null when Union/Subtract/Intersect/Exclude are available; otherwise a specific Czech
    /// explanation of whichever precondition actually fails. CanUniteSelection derives from this so
    /// there is one source of truth — the menu items that used to bind straight to CanUniteSelection's
    /// Visibility now bind to this being null/non-null for IsEnabled and show the text as a ToolTip,
    /// instead of disappearing outright with no way to tell why (the "Sjednotit nefunguje" bug class —
    /// see HANDOFF.md's 2026-09-14 entry and docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md).
    /// Checks are ordered most-general-first so a selection failing several checks at once reports the
    /// first, most actionable one rather than the most specific.</summary>
    public string? UniteSelectionDisabledReason
    {
        get
        {
            if (SelectedObjects.Count == 0) return "Nic není vybráno.";
            if (SelectedObjects.Any(item => item.IsRaster)) return "Bitmapu nelze sjednotit ani kombinovat — vyberte pouze vektory.";
            if (SelectedObjects.Any(item => item.IsLocked)) return "Zamknuté objekty nelze sjednotit ani kombinovat — nejprve je odemkněte.";
            if (SelectedObjects.SelectMany(item => item.LocalShapes).Count() < 2) return "Vyberte alespoň dva tvary.";
            if (SelectedObjects.SelectMany(item => item.LocalShapes).Any(shape => !shape.IsClosed || shape.Points.Count < 3))
                return "Výběr obsahuje otevřenou dráhu — booleovské operace vyžadují uzavřené tvary.";
            return null;
        }
    }
    /// <summary>Why Align is unavailable, or null when it works. Alignment is relative to the other
    /// selected objects, so a single object has nothing to align to.</summary>
    public string? AlignDisabledReason => SelectedObjects.Count switch
    {
        0 => "Vyberte alespoň dva objekty, aby je šlo zarovnat",
        1 => "Vyberte alespoň dva objekty, aby je šlo zarovnat. Jeden objekt nemá k čemu zarovnat",
        _ => null,
    };

    public bool CanUniteSelection => UniteSelectionDisabledReason is null;
    public bool CanTraceSelectedRaster => SelectedObjects.Count == 1 && Selected is { IsRaster: true, IsLocked: false };

    /// <summary>Null when Offset Path is available; otherwise a specific Czech explanation of whichever
    /// precondition fails. CanOffsetSelection derives from this — see UniteSelectionDisabledReason for
    /// why this exists. Unlike Unite, offset does not require two-or-more closed shapes: a single
    /// object, or a selection that includes open paths, is still a valid offset input (an open path
    /// offsets into a butt-capped buffer region — see IVectorOffsetService.OffsetOpenPath).</summary>
    public string? OffsetSelectionDisabledReason
    {
        get
        {
            if (SelectedObjects.Count == 0) return "Nic není vybráno.";
            if (SelectedObjects.Any(item => item.IsRaster)) return "Bitmapu nelze posunout offsetem — vyberte vektor.";
            if (SelectedObjects.Any(item => item.IsLocked)) return "Zamknuté objekty nelze upravit offsetem — nejprve je odemkněte.";
            if (!SelectedObjects.SelectMany(item => item.GetWorldShapes()).Any(shape => shape.Points.Count >= 2))
                return "Vybraný tvar neobsahuje žádnou dráhu k posunutí.";
            return null;
        }
    }
    public bool CanOffsetSelection => OffsetSelectionDisabledReason is null;
    public bool CanRemoveSelectedBackground => SelectedObjects.Count == 1 && Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false };
    public bool CanRestoreSelectedBackground => SelectedObjects.Count == 1 && Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: true };

    /// <summary>Gates the inspector's OBRÁZEK section — true for exactly one selected raster object,
    /// regardless of lock state (unlike CanRemove/CanRestoreSelectedBackground, a locked image should
    /// still show the section, just with its action disabled, not disappear entirely).</summary>
    public bool IsSelectedRaster => SelectedObjects.Count == 1 && Selected?.IsRaster == true;
    public bool IsSelectedLayerRaster => SelectedLayer?.IsRaster == true;
    public bool CanEditSelectedLayerColor => SelectedLayer is not null && !IsSelectedLayerRaster;
    public bool CanAssignSelectionToLayer => HasSelection && SelectedObjects.All(item => !item.IsRaster) &&
        SelectedLayer is not null && !IsSelectedLayerRaster;
    /// <summary>Whether the selection may be moved onto a layer at all - unlike CanAssignSelectionToLayer
    /// this does not depend on which layer row is currently highlighted, so a context menu can offer
    /// every layer by name.</summary>
    public bool CanAssignToAnyLayer => HasSelection && SelectedObjects.All(item => !item.IsRaster && !item.IsLocked);
    public bool CanDeleteSelectedLayer => SelectedLayer is not null && CountObjectsUsingLayer(SelectedLayer) == 0;
    public bool CanMoveSelectedLayerUp => SelectedLayer is not null && Layers.IndexOf(SelectedLayer) > 0;
    public bool CanMoveSelectedLayerDown => SelectedLayer is not null && Layers.IndexOf(SelectedLayer) is var index && index >= 0 && index < Layers.Count - 1;

    /// <summary>Whether reordering is meaningful at all. Drives the visibility of the up/down
    /// controls so they are not permanently parked above a list with nothing to reorder.</summary>
    public bool CanReorderLayers => Layers.Count > 1;
    public string SelectedLayerUsageLabel
    {
        get
        {
            if (SelectedLayer is null) return "Není vybraná vrstva";
            var count = CountObjectsUsingLayer(SelectedLayer);
            return count switch
            {
                0 => "Vrstva zatím není přiřazená žádnému objektu",
                1 => "Vrstva je přiřazená 1 objektu",
                >= 2 and <= 4 => $"Vrstva je přiřazená {count} objektům",
                _ => $"Vrstva je přiřazená {count} objektům",
            };
        }
    }

    public bool CanUndo => _commandStack.CanUndo;
    public bool CanRedo => _commandStack.CanRedo;

    public event Action? Changed;
    public event Action<SceneObject>? TraceRasterRequested;
    public event Action<SceneObject>? BackgroundRemovalRequested;
    public event Action? BackgroundRemovalCancelRequested;
    public event Action<string>? VectorOperationRejected;
    /// <summary>Raised by OffsetSelectionCommand — MainWindow owns opening OffsetPathWindow and
    /// calling ApplyOffset back with its result, the same hand-off shape TraceRasterRequested already
    /// uses for BitmapTraceWindow.</summary>
    public event Action<IReadOnlyList<SceneObject>>? OffsetRequested;

    public SceneViewModel()
    {
        _commandStack.Changed += OnCommandStackChanged;
        SelectedObjects.CollectionChanged += OnSelectionChanged;
        Layers.CollectionChanged += OnLayersCollectionChanged;
        Objects.CollectionChanged += (_, _) => NotifyLayerStateChanged();
        foreach (var layer in Layers) HookLayer(layer);
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Selected = SelectedObjects.Count == 1 ? SelectedObjects[0] : null;
        var selectedColors = SelectedObjects
            .SelectMany(item => item.LocalShapes)
            .Select(shape => shape.LayerColor)
            .ToList();
        if (selectedColors.Count > 0 && selectedColors.All(color => color.IsApproximately(selectedColors[0])))
            SelectedLayer = Layers.FirstOrDefault(layer => layer.Color.IsApproximately(selectedColors[0]));
        BackgroundRemovalError = null;
        NotifySelectionStateChanged();
        NotifyLayerStateChanged();
        RefreshCommands();
    }

    private void OnCommandStackChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        NotifyLayerStateChanged();
        RefreshCommands();
        Changed?.Invoke();
    }

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (Lasero.Core.Layers.LayerSettings layer in e.OldItems) layer.PropertyChanged -= OnLayerPropertyChanged;
        if (e.NewItems is not null) foreach (Lasero.Core.Layers.LayerSettings layer in e.NewItems) HookLayer(layer);
        if (SelectedLayer is null || !Layers.Contains(SelectedLayer))
            SelectedLayer = Layers.FirstOrDefault();
        if (e.Action != NotifyCollectionChangedAction.Reset) Changed?.Invoke();
    }

    private void HookLayer(Lasero.Core.Layers.LayerSettings layer) => layer.PropertyChanged += OnLayerPropertyChanged;
    private void OnLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var item in Objects)
            item.InvalidateLayerAppearance();
        NotifyLayerStateChanged();
        Changed?.Invoke();
    }

    private int CountObjectsUsingLayer(LayerSettings layer) => Objects.Count(item => UsesLayer(item, layer));

    // Shapes reference a layer by id, but projects saved before layer ids existed only carry a colour,
    // so both routes have to count. Raster objects belong to the raster layer by kind rather than by
    // reference. Deleting a layer and selecting its shapes have to agree on this, or "Odstranit lze
    // pouze vrstvu, kterou nepoužívá žádný objekt" would refuse a layer whose shapes cannot be found.
    private static bool UsesLayer(SceneObject item, LayerSettings layer) =>
        (layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty)) ||
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color));

    private void NotifyLayerStateChanged()
    {
        OnPropertyChanged(nameof(IsSelectedLayerRaster));
        OnPropertyChanged(nameof(CanEditSelectedLayerColor));
        OnPropertyChanged(nameof(CanAssignSelectionToLayer));
        OnPropertyChanged(nameof(CanDeleteSelectedLayer));
        OnPropertyChanged(nameof(CanMoveSelectedLayerUp));
        OnPropertyChanged(nameof(CanMoveSelectedLayerDown));
        OnPropertyChanged(nameof(CanReorderLayers));
        OnPropertyChanged(nameof(SelectedLayerUsageLabel));
        DeleteSelectedLayerCommand.NotifyCanExecuteChanged();
        MoveSelectedLayerUpCommand.NotifyCanExecuteChanged();
        MoveSelectedLayerDownCommand.NotifyCanExecuteChanged();
    }

    public bool IsLayerVisible(Lasero.Core.Layers.RgbColor color) =>
        Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color))?.IsVisible ?? true;

    public bool IsLayerVisible(Guid layerId, Lasero.Core.Layers.RgbColor legacyColor) =>
        (layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor)))?.IsVisible ?? true;

    public LayerMode LayerModeFor(RgbColor color) =>
        Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color))?.Mode ?? LayerMode.Cut;

    public LayerMode LayerModeFor(Guid layerId, RgbColor legacyColor) =>
        (layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor)))?.Mode ?? LayerMode.Cut;

    /// <summary>Executed by every mutating gesture the canvas performs (drag-move/rotate/resize on
    /// MouseUp, keyboard nudge) — kept public so SceneCanvas doesn't need its own reference to the stack.</summary>
    public void Execute(ISceneCommand command) => _commandStack.Execute(command);

    private (double X, double Y) NextCascadeOffset()
    {
        _importCount++;
        var offset = CascadeOffsetMm * _importCount;
        return (offset, -offset); // down-right cascade in canvas terms = +X, -Y in work coordinates (Y grows up)
    }

    public void ImportSvgFile(string path, double targetWidthMm)
    {
        var svgText = File.ReadAllText(path);
        var doc = SvgImporter.Import(svgText, targetWidthMm, Path.GetFileName(path));
        var obj = SceneObjectFactory.FromImportedDocument(doc, doc.SourceFileName ?? Path.GetFileName(path));
        PlaceAndAdd(obj, doc.Layers);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ImportRasterFile(string path, RasterImportOptions options)
    {
        var layer = SceneObjectFactory.CreateRasterLayer(options);
        var obj = SceneObjectFactory.FromRaster(path, options, Path.GetFileName(path), layer);
        PlaceAndAdd(obj, [layer]);
    }

    [RelayCommand(CanExecute = nameof(CanTraceSelectedRaster))]
    private void TraceSelectedRaster()
    {
        if (Selected is { IsRaster: true, IsLocked: false } source)
            TraceRasterRequested?.Invoke(source);
    }

    /// <summary>Requests cloud background removal — MainWindow owns consent, HTTP work and the
    /// processing-state UI, while this view model remains responsible for document undo/redo.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedBackground))]
    private void RemoveSelectedBackground()
    {
        if (Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false } source)
            BackgroundRemovalRequested?.Invoke(source);
    }

    [RelayCommand]
    private void CancelBackgroundRemoval() => BackgroundRemovalCancelRequested?.Invoke();

    /// <summary>Replaces a traced raster with one node-editable SceneObject per BitmapTraceResult.
    /// VectorPaths entry (several when the source bitmap held several disconnected shapes — see
    /// CompoundPathBuilder). Each traced VectorPath comes back in the trace's own local frame (its
    /// pixel grid scaled to mm, origin at the bitmap's corner — see BitmapTracer/CompoundPathBuilder),
    /// so ToWorldSpace bakes the source raster's placement (position, and its scale if it was ever
    /// resized) into the node/handle coordinates themselves before handing off to
    /// VectorPathSceneFactory.Create, which always builds with Transform.Identity and a pivot at the
    /// result's own bounds centre — carrying the source's ObjectTransform over unchanged would
    /// rotate/scale the result around the wrong pivot (the source's, not the result's), which is
    /// exactly the same reason ScenePrimitiveFactory's other object types don't pair a
    /// borrowed Transform with newly built local geometry either.</summary>
    public void ReplaceRasterWithTrace(SceneObject source, BitmapTraceResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        if (!source.IsRaster || !Objects.Contains(source))
            throw new InvalidOperationException("The bitmap is no longer available in the scene.");
        if (result.VectorPaths.Count == 0)
            throw new InvalidOperationException("Tracing did not produce any vector contours.");

        var multipleObjects = result.VectorPaths.Count > 1;
        var tracedObjects = new List<SceneObject>(result.VectorPaths.Count);
        for (var index = 0; index < result.VectorPaths.Count; index++)
        {
            var traced = result.VectorPaths[index];
            var worldPath = ToWorldSpace(traced.Path, source.Transform, source.LocalPivot);
            var name = multipleObjects ? $"Trasování · {source.Name} ({index + 1})" : $"Trasování · {source.Name}";
            var tracedObject = VectorPathSceneFactory.Create(worldPath, traced.Color, name);
            var layer = result.Document.Layers.FirstOrDefault(candidate => candidate.Color == traced.Color);
            if (layer is not null) tracedObject.AssignToLayer(layer);
            tracedObject.IsVisible = source.IsVisible;
            tracedObject.IncludeInOutput = source.IncludeInOutput;
            tracedObjects.Add(tracedObject);
        }

        Execute(new ReplaceObjectsCommand(Scene, [source], tracedObjects, result.Document.Layers));
        SelectedObjects.Clear();
        foreach (var tracedObject in tracedObjects) SelectedObjects.Add(tracedObject);
        ActiveTool = DesignerTool.Select;
    }

    /// <summary>Maps every anchor/handle of a locally-framed VectorPath into world mm space through
    /// one ObjectTransform — affine, so applying it to all of a cubic Bezier's control points yields
    /// exactly the correctly transformed curve.</summary>
    private static VectorPath ToWorldSpace(VectorPath localPath, ObjectTransform transform, Position pivot)
    {
        var subpaths = localPath.Subpaths.Select(subpath => subpath with
        {
            Nodes = subpath.Nodes.Select(node => new VectorNode(
                transform.Apply(node.Anchor, pivot),
                node.HandleIn is { } handleIn ? transform.Apply(handleIn, pivot) : null,
                node.HandleOut is { } handleOut ? transform.Apply(handleOut, pivot) : null,
                node.Type)).ToList(),
        }).ToList();
        return new VectorPath { Subpaths = subpaths };
    }

    public void DrawPrimitive(DesignerTool tool, Position start, Position end)
    {
        var color = new Lasero.Core.Layers.RgbColor(18, 18, 18);
        var obj = DesignerPrimitiveFactory.Create(tool, start, end, color);

        AddDrawingObject(
            obj,
            [Lasero.Core.Layers.LayerSettings.CreateDefault(color, Lasero.Core.Layers.LayerMode.Cut, "Vektor")]);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AddText(string text, Position origin, double heightMm) =>
        AddText(text, origin, heightMm, VectorTextStyle.Default);

    public void AddText(string text, Position origin, double heightMm, VectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        AddText(
            new TextSource
            {
                Text = text,
                HeightMm = heightMm,
                FontFamily = style.FontFamily,
                Bold = style.Bold,
                Italic = style.Italic,
                Uppercase = style.Uppercase,
                Weld = style.Weld,
            },
            origin);
    }

    public void AddText(TextSource source, Position origin)
    {
        // Text stays visually near-black while going onto its own processing layer, so filled text is
        // not merged with the default black cut layer.
        var color = VectorTextFactory.DefaultColor;
        var obj = VectorTextFactory.Create(source, origin, color);
        AddDrawingObject(
            obj,
            [Lasero.Core.Layers.LayerSettings.CreateDefault(color, Lasero.Core.Layers.LayerMode.Fill, "Text")]);
    }

    /// <summary>Adds a finished vector path (from the "Čára" multi-click path tool) as one new
    /// SceneObject/AddObjectCommand -- the whole draw-this-path gesture is one undo step, matching
    /// AddText/DrawPrimitive's precedent for every other drawing tool.</summary>
    public void AddVectorPath(VectorPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var color = new Lasero.Core.Layers.RgbColor(18, 18, 18);
        var obj = VectorPathSceneFactory.Create(path, color, "Křivka");
        AddDrawingObject(
            obj,
            [Lasero.Core.Layers.LayerSettings.CreateDefault(color, Lasero.Core.Layers.LayerMode.Cut, "Vektor")]);
    }

    /// <summary>Commits one Node Edit mode edit (move/add/delete/convert node, drag handle, close path)
    /// as one ReplaceObjectsCommand -- the same "this object's geometry changed" contract CommitTextEdit
    /// already uses for text, so both share one undo history and one command shape.</summary>
    public void CommitVectorPathEdit(SceneObject item, VectorPath path)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(path);
        if (item.VectorPath is null || !Objects.Contains(item)) return;

        SceneObject replacement;
        try
        {
            replacement = VectorPathSceneFactory.Rebuild(item, path);
        }
        catch (InvalidOperationException ex)
        {
            VectorOperationRejected?.Invoke(ex.Message);
            return;
        }

        var wasSelected = SelectedObjects.Contains(item);
        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        if (wasSelected)
        {
            SelectedObjects.Clear();
            SelectedObjects.Add(replacement);
        }
    }

    /// <summary>Commits a completed background-removal run as one ReplaceObjectsCommand — the same
    /// "this object's raster content changed" contract CommitTextEdit/CommitVectorPathEdit use for
    /// their own kind of content edit, so Ctrl+Z restores the original file and Ctrl+Y reapplies the
    /// removal exactly like those. removedBackgroundFilePath is the new file background-removal service
    /// already wrote (with real alpha transparency); this method only ever swaps which file
    /// RasterFilePath points at — geometry, layer, transform and every other property are untouched.</summary>
    public bool CommitBackgroundRemoval(SceneObject item, string removedBackgroundFilePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(removedBackgroundFilePath);
        if (!item.IsRaster || item.HasBackgroundRemoved || !Objects.Contains(item)) return false;

        var replacement = WithRasterFile(item, removedBackgroundFilePath, item.RasterFilePath);
        var wasSelected = SelectedObjects.Contains(item);
        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        if (wasSelected)
        {
            SelectedObjects.Clear();
            SelectedObjects.Add(replacement);
        }
        NotifySelectionStateChanged();
        return true;
    }

    /// <summary>"Obnovit pozadí" — points RasterFilePath back at the preserved original as one more
    /// ReplaceObjectsCommand undo step. The background-removed file itself is left on disk (undo/redo
    /// of the restore, or a future re-removal, may still need it), matching the non-destructive
    /// contract: neither file this object has ever pointed at is deleted by these operations.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreSelectedBackground))]
    public void RestoreSelectedBackground()
    {
        if (Selected is not { HasBackgroundRemoved: true, OriginalRasterFilePath: { } original } item || !Objects.Contains(item))
            return;

        var replacement = WithRasterFile(item, original, null);
        var wasSelected = SelectedObjects.Contains(item);
        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        if (wasSelected)
        {
            SelectedObjects.Clear();
            SelectedObjects.Add(replacement);
        }
        NotifySelectionStateChanged();
    }

    private static SceneObject WithRasterFile(SceneObject item, string rasterFilePath, string? originalRasterFilePath) => new()
    {
        LocalShapes = item.LocalShapes,
        LocalPivot = item.LocalPivot,
        LocalBounds = item.LocalBounds,
        RasterFilePath = rasterFilePath,
        RasterOptions = item.RasterOptions,
        OriginalRasterFilePath = originalRasterFilePath,
        Text = item.Text,
        VectorPath = item.VectorPath,
        Name = item.Name,
        Transform = item.Transform,
        IsVisible = item.IsVisible,
        IsLocked = item.IsLocked,
        IncludeInOutput = item.IncludeInOutput,
    };

    private void PlaceAndAdd(SceneObject obj, IReadOnlyList<Lasero.Core.Layers.LayerSettings> candidateLayers)
    {
        var (offsetX, offsetY) = NextCascadeOffset();
        obj.Transform = obj.Transform with { X = offsetX, Y = offsetY };

        Execute(new AddObjectCommand(Scene, obj, candidateLayers));

        SelectedObjects.Clear();
        SelectedObjects.Add(obj);
        ActiveTool = DesignerTool.Select;
    }

    private void AddDrawingObject(SceneObject obj, IReadOnlyList<Lasero.Core.Layers.LayerSettings> candidateLayers)
    {
        Execute(new AddObjectCommand(Scene, obj, candidateLayers));
        SelectedObjects.Clear();
        SelectedObjects.Add(obj);
    }

    // Layer row context menu. LightBurn puts these on right-click and nowhere else, and the reason
    // holds here: they act on one named row, so a toolbar button for them would first have to say
    // which row it meant. Each one is a plain state write on LayerSettings — nothing here decides
    // what the machine does, only which layers the job generator will find enabled.

    [RelayCommand]
    private void ToggleLayerOutput(LayerSettings? layer)
    {
        if (layer is null) return;
        layer.IsEnabled = !layer.IsEnabled;
    }

    [RelayCommand]
    private void ToggleLayerVisibility(LayerSettings? layer)
    {
        if (layer is null) return;
        layer.IsVisible = !layer.IsVisible;
    }

    /// <summary>Leaves only this layer in the job. The quickest way to make one test cut.</summary>
    [RelayCommand]
    private void IsolateLayerOutput(LayerSettings? layer)
    {
        if (layer is null) return;
        foreach (var candidate in Layers)
            candidate.IsEnabled = ReferenceEquals(candidate, layer);
    }

    /// <summary>Leaves only this layer on the canvas. Visibility never changes what is produced.</summary>
    [RelayCommand]
    private void IsolateLayerVisibility(LayerSettings? layer)
    {
        if (layer is null) return;
        foreach (var candidate in Layers)
            candidate.IsVisible = ReferenceEquals(candidate, layer);
    }

    [RelayCommand]
    private void SelectObjectsInLayer(LayerSettings? layer)
    {
        if (layer is null) return;
        SelectedLayer = layer;
        SelectedObjects.Clear();
        foreach (var item in Objects.Where(item => UsesLayer(item, layer)))
            SelectedObjects.Add(item);
    }

    [RelayCommand]
    private void ActivateLayer(LayerSettings? layer)
    {
        if (layer is null) return;
        if (CanAssignSelectionToLayer)
            AssignSelectionToLayer(layer);
        else
            SelectedLayer = layer;
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedLayerUp))]
    private void MoveSelectedLayerUp()
    {
        if (SelectedLayer is null) return;
        var index = Layers.IndexOf(SelectedLayer);
        if (index <= 0) return;
        Layers.Move(index, index - 1);
        NotifyLayerStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedLayerDown))]
    private void MoveSelectedLayerDown()
    {
        if (SelectedLayer is null) return;
        var index = Layers.IndexOf(SelectedLayer);
        if (index < 0 || index >= Layers.Count - 1) return;
        Layers.Move(index, index + 1);
        NotifyLayerStateChanged();
    }

    [RelayCommand]
    private void DuplicateSelectedLayer()
    {
        if (SelectedLayer is null) return;
        var source = SelectedLayer;
        var copy = new LayerSettings
        {
            Color = FindAvailableLayerColor(),
            Name = $"{source.Name} – kopie",
            Mode = source.Mode,
            Speed = source.Speed,
            Power = source.Power,
            Passes = source.Passes,
            FillLineIntervalMm = source.FillLineIntervalMm,
            MaterialLabel = source.MaterialLabel,
            IsEnabled = source.IsEnabled,
            IsVisible = source.IsVisible,
            IsRaster = source.IsRaster,
        };
        Layers.Insert(Layers.IndexOf(source) + 1, copy);
        SelectedLayer = copy;
    }

    private RgbColor FindAvailableLayerColor()
    {
        for (var hue = 0; hue < 255; hue += 17)
        {
            var candidate = new RgbColor((byte)(55 + hue % 180), (byte)(45 + (hue * 3) % 190), (byte)(50 + (hue * 7) % 185));
            if (Layers.All(layer => !layer.Color.IsApproximately(candidate))) return candidate;
        }
        return new RgbColor(1, 1, 1);
    }

    [RelayCommand]
    private void ChangeSelectedLayerColor(RgbColor color)
    {
        var source = SelectedLayer;
        if (IsSelectedLayerRaster) return;

        // Color is an object-facing action in an editor: when vectors are selected, applying a
        // swatch must recolor them immediately. This also fixes the confusing case where a newly
        // created empty layer stayed selected while the artwork visibly remained in another layer.
        if (SelectedObjects.Count > 0 && SelectedObjects.All(item => !item.IsRaster))
        {
            ApplyColorToSelection(color, source);
            return;
        }

        if (source is null || source.Color.IsApproximately(color)) return;

        var sourceColor = source.Color;
        var destination = Layers.FirstOrDefault(layer => layer != source && layer.Color.IsApproximately(color));

        if (destination is not null)
        {
            foreach (var item in Objects)
                item.ReplaceLayer(source.Id, sourceColor, destination);
            Layers.Remove(source);
            SelectedLayer = destination;
        }
        else
        {
            source.Color = color;
            foreach (var item in Objects)
                item.ReplaceLayer(source.Id, sourceColor, source);
        }

        NotifyLayerStateChanged();
        Changed?.Invoke();
    }

    private void ApplyColorToSelection(RgbColor color, LayerSettings? template)
    {
        var previousLayers = new List<(Guid Id, RgbColor Color)>();
        foreach (var shape in SelectedObjects.SelectMany(item => item.LocalShapes))
        {
            if (previousLayers.All(existing => existing.Id != shape.LayerId || shape.LayerId == Guid.Empty && !existing.Color.IsApproximately(shape.LayerColor)))
                previousLayers.Add((shape.LayerId, shape.LayerColor));
        }

        var destination = Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color));
        if (destination is null)
        {
            // Reuse an intentionally created empty layer; otherwise create a sibling that inherits
            // the processing settings currently visible in the inspector.
            if (template is not null && CountObjectsUsingLayer(template) == 0)
            {
                template.Color = color;
                destination = template;
            }
            else
            {
                destination = new LayerSettings
                {
                    Color = color,
                    Name = template?.Name is { Length: > 0 } name ? name : $"Vrstva {Layers.Count + 1:00}",
                    Mode = template?.Mode ?? LayerMode.Cut,
                    Speed = template?.Speed ?? 350,
                    Power = template?.Power ?? 95,
                    Passes = template?.Passes ?? 1,
                    FillLineIntervalMm = template?.FillLineIntervalMm ?? 25.4 / 254,
                    MaterialLabel = template?.MaterialLabel,
                    IsEnabled = template?.IsEnabled ?? true,
                    IsVisible = template?.IsVisible ?? true,
                };
                Layers.Add(destination);
            }
        }

        foreach (var item in SelectedObjects)
            item.AssignToLayer(destination);

        // Remove only source layers made empty by this reassignment. Other user-created empty
        // layers remain untouched, and the fixed raster layer can never be removed here.
        foreach (var previous in previousLayers)
        {
            var previousLayer = Layers.FirstOrDefault(layer =>
                layer != destination && (previous.Id != Guid.Empty
                    ? layer.Id == previous.Id
                    : layer.Color.IsApproximately(previous.Color)));
            if (previousLayer is not null &&
                !previousLayer.Color.IsApproximately(SceneObjectFactory.RasterEngravingColor) &&
                CountObjectsUsingLayer(previousLayer) == 0)
                Layers.Remove(previousLayer);
        }

        SelectedLayer = destination;
        NotifyLayerStateChanged();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AssignSelectionToLayer(LayerSettings? layer)
    {
        if (layer is null || !SelectedObjects.Any() ||
            layer.Color.IsApproximately(SceneObjectFactory.RasterEngravingColor) ||
            SelectedObjects.Any(item => item.IsRaster)) return;

        // One undo step for the whole selection: the right-click "Přiřadit do vrstvy" menu made this
        // a mouse-quick action, and a mis-click there has to be as cheap to take back as any other edit.
        var command = new AssignObjectsToLayerCommand(SelectedObjects.ToList(), layer);
        if (!command.ChangesAnything) return;

        Execute(command);
        SelectedLayer = layer;
        NotifyLayerStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedLayer))]
    private void DeleteSelectedLayer()
    {
        if (SelectedLayer is null || !CanDeleteSelectedLayer) return;
        Layers.Remove(SelectedLayer);
        SelectedLayer = Layers.FirstOrDefault();
        NotifyLayerStateChanged();
    }

    private bool CanTransformSingle() => Selected is { IsLocked: false, IsRaster: false };

    [RelayCommand]
    private void ActivateTool(DesignerTool tool) => ActiveTool = tool;

    /// <summary>ActiveTool is set both through ActivateToolCommand and directly (keyboard shortcuts in
    /// MainWindow), so the shape-tool memory has to hook the property rather than the command.</summary>
    partial void OnActiveToolChanged(DesignerTool value)
    {
        if (ShapeToolCatalog.IsShapeTool(value)) CurrentShapeTool = value;
    }

    [RelayCommand(CanExecute = nameof(CanUndoExecute))]
    private void Undo()
    {
        _commandStack.Undo();
        ReconcileSelectionWithScene();
    }
    private bool CanUndoExecute() => CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoExecute))]
    private void Redo()
    {
        _commandStack.Redo();
        ReconcileSelectionWithScene();
    }

    /// <summary>A node edit, text edit or background removal swaps the object for a new instance with the
    /// same Id. Undo/redo swap it back, so the selection may name an instance that has left the scene:
    /// the overlay then draws the retired geometry. Follow the Id to the live instance or drop it.</summary>
    private void ReconcileSelectionWithScene()
    {
        var stale = SelectedObjects.Where(item => !Objects.Contains(item)).ToList();
        foreach (var item in stale)
        {
            var live = Objects.FirstOrDefault(candidate => candidate.Id == item.Id);
            var index = SelectedObjects.IndexOf(item);
            if (live is not null && !SelectedObjects.Contains(live) && index >= 0) SelectedObjects[index] = live;
            else SelectedObjects.Remove(item);
        }
    }
    private bool CanRedoExecute() => CanRedo;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var targets = SelectedObjects.ToList();
        SelectedObjects.Clear();
        Execute(new DeleteObjectsCommand(Scene, targets));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        var sources = SelectedObjects.ToList();
        var command = new DuplicateObjectsCommand(Scene, sources, new Position(CascadeOffsetMm, -CascadeOffsetMm, 0));
        Execute(command);

        SelectedObjects.Clear();
        foreach (var clone in command.Clones)
            SelectedObjects.Add(clone);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        _clipboard = SelectedObjects.ToList();
        PasteCommand.NotifyCanExecuteChanged();
    }

    private bool HasClipboard() => _clipboard.Count > 0;

    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private void Paste()
    {
        var command = new DuplicateObjectsCommand(Scene, _clipboard, new Position(CascadeOffsetMm, -CascadeOffsetMm, 0));
        Execute(command);

        SelectedObjects.Clear();
        foreach (var clone in command.Clones)
            SelectedObjects.Add(clone);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        Copy();
        Delete();
    }

    private bool CanSelectAll() => Objects.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        SelectedObjects.Clear();
        foreach (var obj in Objects)
            SelectedObjects.Add(obj);
    }

    private bool CanAlign() => SelectedObjects.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignLeft() => Align(objects =>
    {
        var target = objects.Min(o => o.WorldBounds().MinX);
        return objects.Select(o => Move(o, dx: target - o.WorldBounds().MinX, dy: 0));
    });

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignCenterHorizontal() => Align(objects =>
    {
        var target = (objects.Min(o => o.WorldBounds().MinX) + objects.Max(o => o.WorldBounds().MaxX)) / 2;
        return objects.Select(o => Move(o, dx: target - (o.WorldBounds().MinX + o.WorldBounds().MaxX) / 2, dy: 0));
    });

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignRight() => Align(objects =>
    {
        var target = objects.Max(o => o.WorldBounds().MaxX);
        return objects.Select(o => Move(o, dx: target - o.WorldBounds().MaxX, dy: 0));
    });

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignTop() => Align(objects =>
    {
        var target = objects.Max(o => o.WorldBounds().MaxY);
        return objects.Select(o => Move(o, dx: 0, dy: target - o.WorldBounds().MaxY));
    });

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignMiddle() => Align(objects =>
    {
        var target = (objects.Min(o => o.WorldBounds().MinY) + objects.Max(o => o.WorldBounds().MaxY)) / 2;
        return objects.Select(o => Move(o, dx: 0, dy: target - (o.WorldBounds().MinY + o.WorldBounds().MaxY) / 2));
    });

    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignBottom() => Align(objects =>
    {
        var target = objects.Min(o => o.WorldBounds().MinY);
        return objects.Select(o => Move(o, dx: 0, dy: target - o.WorldBounds().MinY));
    });

    /// <summary>Builds one TransformObjectCommand per selected object from the given per-object delta
    /// and executes them as a single undo step — same pattern as a multi-object drag.</summary>
    private void Align(Func<IReadOnlyList<SceneObject>, IEnumerable<TransformObjectCommand>> plan)
    {
        var objects = SelectedObjects.ToList();
        var commands = plan(objects).Cast<ISceneCommand>().ToList();
        if (commands.Count > 0) Execute(new CompositeSceneCommand(commands));
    }

    private static TransformObjectCommand Move(SceneObject obj, double dx, double dy) =>
        new(obj, obj.Transform, obj.Transform with { X = obj.Transform.X + dx, Y = obj.Transform.Y + dy });

    private bool CanReorder() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanReorder))]
    private void BringToFront()
    {
        if (Selected is null) return;
        Execute(new ReorderObjectCommand(Scene, Selected, Objects.Count - 1));
    }

    [RelayCommand(CanExecute = nameof(CanReorder))]
    private void SendToBack()
    {
        if (Selected is null) return;
        Execute(new ReorderObjectCommand(Scene, Selected, 0));
    }

    /// <summary>Objects[0] is the back of the stack and the last item the front, the same order
    /// SendToBack/BringToFront already move to. One-step moves are only meaningful for one object.</summary>
    public bool CanBringForward => Selected is not null && Objects.IndexOf(Selected) is var index && index >= 0 && index < Objects.Count - 1;
    public bool CanSendBackward => Selected is not null && Objects.IndexOf(Selected) > 0;

    [RelayCommand(CanExecute = nameof(CanBringForward))]
    private void BringForward()
    {
        if (Selected is null) return;
        Execute(new ReorderObjectCommand(Scene, Selected, Objects.IndexOf(Selected) + 1));
    }

    [RelayCommand(CanExecute = nameof(CanSendBackward))]
    private void SendBackward()
    {
        if (Selected is null) return;
        Execute(new ReorderObjectCommand(Scene, Selected, Objects.IndexOf(Selected) - 1));
    }

    [RelayCommand(CanExecute = nameof(CanTransformSingle))]
    private void RotateLeft() => RotateSelected(-90);

    [RelayCommand(CanExecute = nameof(CanTransformSingle))]
    private void RotateRight() => RotateSelected(90);

    private void RotateSelected(double delta)
    {
        if (Selected is null) return;
        var before = Selected.Transform;
        var rotation = (before.RotationDeg + delta) % 360;
        Execute(new TransformObjectCommand(Selected, before, before with { RotationDeg = rotation }));
    }

    [RelayCommand(CanExecute = nameof(CanTransformSingle))]
    private void FlipHorizontal()
    {
        if (Selected is null) return;
        var before = Selected.Transform;
        Execute(new TransformObjectCommand(Selected, before, before with { ScaleX = -before.ScaleX }));
    }

    [RelayCommand(CanExecute = nameof(CanTransformSingle))]
    private void FlipVertical()
    {
        if (Selected is null) return;
        var before = Selected.Transform;
        Execute(new TransformObjectCommand(Selected, before, before with { ScaleY = -before.ScaleY }));
    }

    [RelayCommand(CanExecute = nameof(CanGroupSelection))]
    private void GroupSelection()
    {
        var sources = SelectedObjects.ToList();
        // Preserve compound paths (outer contour + holes), but give legacy geometry from every
        // separately selected object its own identity. Without this distinction a later Union
        // could apply EvenOdd across unrelated overlapping objects and cancel them completely.
        var worldShapes = sources.SelectMany(item =>
        {
            var legacyGeometrySetId = Guid.NewGuid();
            return item.GetWorldShapes().Select(shape => shape.GeometrySetId == Guid.Empty
                ? shape with { GeometrySetId = legacyGeometrySetId }
                : shape);
        }).ToList();
        var group = CreateVectorObject(
            worldShapes,
            sources.Count == 2 ? "Skupina · 2 objekty" : $"Skupina · {sources.Count} objektů",
            sources.All(item => item.IsVisible),
            sources.All(item => item.IncludeInOutput),
            BuildCombinedWorldVectorPath(sources));

        Execute(new ReplaceObjectsCommand(Scene, sources, [group]));
        SelectedObjects.Clear();
        SelectedObjects.Add(group);
    }

    /// <summary>Every source's own editable curve geometry, transformed into one shared world-space
    /// VectorPath, subpath-per-shape in the exact same order GroupSelection's own worldShapes
    /// collection already iterates (source-by-source, GetWorldShapes()'s own per-shape order within
    /// each source) — the two lists MUST stay index-aligned, since CreateVectorObject zips this
    /// path's flattened output back onto worldShapes' per-shape metadata (LayerId, GeometrySetId,
    /// PreferredMode). A source with no VectorPath (an old flattened import, or a boolean/union
    /// result predating this fix) falls back to a straight-segment subpath per shape — the same
    /// BuildStraightVectorPath logic CreateVectorObject already uses when no curve data exists at
    /// all, just applied per-source here so ONE non-vector source in a mixed selection does not force
    /// every other source's real curves to flatten too.</summary>
    private static VectorPath BuildCombinedWorldVectorPath(IReadOnlyList<SceneObject> sources)
    {
        var subpaths = new List<VectorSubpath>();
        foreach (var source in sources)
        {
            var worldPath = source.GetWorldVectorPath();
            subpaths.AddRange(worldPath is not null
                ? worldPath.Subpaths
                : BuildStraightVectorPath(source.GetWorldShapes()).Subpaths);
        }
        return new VectorPath { Subpaths = subpaths };
    }

    [RelayCommand(CanExecute = nameof(CanUngroupSelection))]
    private void UngroupSelection()
    {
        if (Selected is null) return;
        var source = Selected;
        // All legacy contours inside one object form one compound path. Give them a stable shared
        // identity before splitting so an outer glyph contour and its counter (for example the
        // opening inside "e") can be put back together without the counter becoming a filled shape.
        var legacyGeometrySetId = Guid.NewGuid();
        // Mirrors GroupSelection's own source: the source's world VectorPath, subpath-per-shape in
        // the same order GetWorldShapes() below iterates (see GetWorldVectorPath's own guarantee that
        // Subpaths.Count/order matches LocalShapes exactly) — so Ungroup recovers the exact editable
        // curve each part had before it was grouped, rather than flattening it to straight segments
        // the way the pre-fix CreateVectorObject always did.
        var sourceWorldPath = source.GetWorldVectorPath();
        var parts = source.GetWorldShapes()
            .Select(shape => shape.GeometrySetId == Guid.Empty
                ? shape with { GeometrySetId = legacyGeometrySetId }
                : shape)
            .Select((shape, index) => CreateVectorObject(
                [shape],
                $"{source.Name} · část {index + 1}",
                source.IsVisible,
                source.IncludeInOutput,
                sourceWorldPath is not null && index < sourceWorldPath.Subpaths.Count
                    ? new VectorPath { Subpaths = [sourceWorldPath.Subpaths[index]] }
                    : null))
            .ToList();

        Execute(new ReplaceObjectsCommand(Scene, [source], parts));
        SelectedObjects.Clear();
        foreach (var part in parts)
            SelectedObjects.Add(part);
    }

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void UniteSelection()
    {
        var sources = SelectedObjects.ToList();
        if (sources.Count == 0) return;

        var sourceShapes = sources.SelectMany(item => item.GetWorldShapes()).ToList();
        var targetShape = FindUnionTargetShape(sources, sourceShapes);

        try
        {
            IReadOnlyList<IReadOnlyList<Position>>? united = null;
            foreach (var source in sources)
            {
                var sourceRings = BuildSourceRings(source);
                if (sourceRings is null) continue;
                united = united is null
                    ? sourceRings
                    : BooleanService.Union(united, sourceRings, VectorFillRule.EvenOdd);
            }

            if (united is null || TotalArea(united) <= MinimumUnitedAreaSquareMm)
            {
                RejectVectorUnion("Vybrané křivky nevytvářejí platnou uzavřenou plochu.");
                return;
            }

            var contours = ToImportedShapes(
                united,
                Guid.NewGuid(),
                targetShape.LayerId,
                targetShape.LayerColor,
                targetShape.PreferredMode);
            if (!IsSafeUnionResult(sourceShapes, contours))
            {
                RejectVectorUnion("Výsledná geometrie by byla prázdná nebo poškozená.");
                return;
            }

            var result = CreateVectorObject(
                contours,
                "Sjednocený vektor",
                sources.Any(item => item.IsVisible),
                sources.Any(item => item.IncludeInOutput));

            Execute(new ReplaceObjectsCommand(Scene, sources, [result]));
            SelectedObjects.Clear();
            SelectedObjects.Add(result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion("Křivky obsahují neplatné nebo vzájemně se křížící body.");
        }
    }

    /// <summary>Subtract, intersect and exclude (XOR) — the other three of the four standard boolean
    /// modes, sharing UniteSelection's CanExecute (CanUniteSelection: same "closed shapes only, at
    /// least two" requirement applies to any of the four) and its geometry/rejection plumbing
    /// (BuildSourceRings, ToImportedShapes, IsSafeUnionResult, NormalizeNestedCompoundPaths,
    /// FindUnionTargetShape, RejectVectorUnion). Their names say "union", but the logic underneath is
    /// identical for every mode — only which IVectorBooleanService method combine calls changes,
    /// which is exactly what this method threads through. Kept as a copy of UniteSelection's shape
    /// rather than a shared refactor of it, so this addition cannot regress the existing,
    /// already-tested union path.
    ///
    /// Source order matters for Subtract (it does not, mathematically, for Intersect/Xor, but folding
    /// left-to-right is deterministic either way): sources are processed back-to-front by scene
    /// z-order, so Subtract reads as "shapes in front cut a hole in the shape behind them" — Minus
    /// Front in Illustrator, Subtract in Figma — rather than an order the user cannot predict from
    /// selection order alone.</summary>
    private void CombineSelection(
        Func<IReadOnlyList<IReadOnlyList<Position>>, IReadOnlyList<IReadOnlyList<Position>>, VectorFillRule, IReadOnlyList<IReadOnlyList<Position>>> combine,
        string resultName)
    {
        var sources = SelectedObjects.OrderBy(item => Scene.Objects.IndexOf(item)).ToList();
        if (sources.Count == 0) return;

        var sourceShapes = sources.SelectMany(item => item.GetWorldShapes()).ToList();
        var targetShape = FindUnionTargetShape(sources, sourceShapes);

        try
        {
            IReadOnlyList<IReadOnlyList<Position>>? combined = null;
            foreach (var source in sources)
            {
                var sourceRings = BuildSourceRings(source);
                if (sourceRings is null) continue;
                combined = combined is null
                    ? sourceRings
                    : combine(combined, sourceRings, VectorFillRule.EvenOdd);
            }

            if (combined is null || TotalArea(combined) <= MinimumUnitedAreaSquareMm)
            {
                RejectVectorUnion("Výsledkem operace by byla prázdná plocha.");
                return;
            }

            var contours = ToImportedShapes(
                combined,
                Guid.NewGuid(),
                targetShape.LayerId,
                targetShape.LayerColor,
                targetShape.PreferredMode);
            if (!IsSafeBooleanResult(sourceShapes, contours))
            {
                RejectVectorUnion("Výsledná geometrie by byla prázdná nebo poškozená.");
                return;
            }

            var result = CreateVectorObject(
                contours,
                resultName,
                sources.Any(item => item.IsVisible),
                sources.Any(item => item.IncludeInOutput));

            Execute(new ReplaceObjectsCommand(Scene, sources, [result]));
            SelectedObjects.Clear();
            SelectedObjects.Add(result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion("Křivky obsahují neplatné nebo vzájemně se křížící body.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void SubtractSelection() => CombineSelection(BooleanService.Subtract, "Odečtený vektor");

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void IntersectSelection() => CombineSelection(BooleanService.Intersect, "Průnik vektorů");

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void ExcludeSelection() => CombineSelection(BooleanService.Xor, "Vyloučený vektor");

    // -------------------------------------------------------------------------------------------
    // Offset Path — vector inset/outset. Unlike the boolean ops above, offset never merges sources
    // together: every selected object gets its own independent offset result, and only sources whose
    // result survives (did not fully collapse at the chosen distance) replace their original. See
    // VectorOffsetPlanner for the ring math shared with OffsetPathViewModel's live preview.
    // -------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanOffsetSelection))]
    private void OffsetSelection()
    {
        if (SelectedObjects.Count == 0) return;
        OffsetRequested?.Invoke(SelectedObjects.ToList());
    }

    /// <summary>Commits the Offset Path dialog's result as one undo step covering every selected
    /// source that produced a surviving result. A source whose offset vanished entirely at the chosen
    /// distance (see IVectorOffsetService) is simply left out of the replacement — the same
    /// "shapes below the disappearance point are omitted, not an error" contract the geometry service
    /// itself follows.</summary>
    // -------------------------------------------------------------------------------------------
    // Cross-object endpoint join (LIGHTBURN_VECTOR_PARITY.md §23's cross-object case — the
    // same-object case, dragging one open endpoint onto the OTHER end of the SAME subpath to close
    // it, is TryCloseByEndpointJoin in SceneCanvas.VectorPathTool.cs and does not go through here).
    // Called from SceneCanvas when a dragged endpoint lands within snap tolerance of a DIFFERENT
    // node-editable object's own open endpoint.
    // -------------------------------------------------------------------------------------------

    /// <summary>Merges two SceneObjects into one by joining ONE subpath from each end-to-end
    /// (VectorPathEditor.JoinAtEndpoints — reverses either side internally as needed, so the caller
    /// never has to pre-reverse a path). Scoped to single-subpath objects only: a compound object
    /// (multiple contours, e.g. text with a counter) has no single well-defined "the other contours
    /// come along for the ride" behavior worth guessing at, so this is a documented no-op for that
    /// case rather than silently dropping or mismerging the extra contours — see
    /// docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md for the scope note.
    ///
    /// Metadata policy: the merged result adopts <paramref name="dragged"/>'s own representative
    /// shape metadata (LayerId, color, PreferredMode, GeometrySetId) and Name — "dragged" is always
    /// the object whose endpoint the user was actively moving (SceneCanvas passes it positionally),
    /// so the path being worked on keeps carrying its own identity onto whatever it touches, the same
    /// direction TryCloseByEndpointJoin's own same-object case implicitly takes (the object never
    /// becomes "some other object"). IsVisible/IncludeInOutput combine with OR (visible/included if
    /// either source was), matching BuildCombinedWorldVectorPath's sibling operations (Group).</summary>
    public bool JoinObjectEndpoints(
        SceneObject dragged, bool draggedAtStart,
        SceneObject target, bool targetAtStart)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        ArgumentNullException.ThrowIfNull(target);
        if (!Objects.Contains(dragged) || !Objects.Contains(target) || ReferenceEquals(dragged, target))
            return false;
        if (dragged.VectorPath is not { Subpaths.Count: 1 } || target.VectorPath is not { Subpaths.Count: 1 })
            return false;

        var draggedWorld = dragged.GetWorldVectorPath()!.Subpaths[0];
        var targetWorld = target.GetWorldVectorPath()!.Subpaths[0];
        if (draggedWorld.IsClosed || targetWorld.IsClosed) return false;

        VectorSubpath joined;
        try
        {
            joined = VectorPathEditor.JoinAtEndpoints(draggedWorld, draggedAtStart, targetWorld, targetAtStart);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var representative = dragged.LocalShapes.FirstOrDefault();
        var color = representative?.LayerColor ?? RgbColor.Black;
        var result = VectorPathSceneFactory.Create(new VectorPath { Subpaths = [joined] }, color, dragged.Name);
        if (representative is not null)
        {
            result.LocalShapes = result.LocalShapes.Select(shape => shape with
            {
                LayerId = representative.LayerId,
                PreferredMode = representative.PreferredMode,
                GeometrySetId = representative.GeometrySetId == Guid.Empty ? Guid.NewGuid() : representative.GeometrySetId,
            }).ToList();
        }
        result.IsVisible = dragged.IsVisible || target.IsVisible;
        result.IncludeInOutput = dragged.IncludeInOutput || target.IncludeInOutput;

        Execute(new ReplaceObjectsCommand(Scene, [dragged, target], [result]));
        SelectedObjects.Clear();
        SelectedObjects.Add(result);
        return true;
    }

    public void ApplyOffset(
        IReadOnlyList<SceneObject> sources,
        IReadOnlyDictionary<SceneObject, VectorPath> resultsBySource)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(resultsBySource);

        var removed = new List<SceneObject>();
        var added = new List<SceneObject>();
        foreach (var source in sources)
        {
            if (!Objects.Contains(source)) continue;
            if (!resultsBySource.TryGetValue(source, out var path) || path.Subpaths.Count == 0) continue;

            removed.Add(source);
            added.Add(BuildOffsetObject(source, path));
        }

        if (removed.Count == 0) return;

        Execute(new ReplaceObjectsCommand(Scene, removed, added));
        SelectedObjects.Clear();
        foreach (var item in added) SelectedObjects.Add(item);
    }

    /// <summary>Wraps one source's offset VectorPath into a new node-editable SceneObject, preserving
    /// that source's own representative shape metadata (layer, color, preferred mode) and its
    /// visibility flags — the per-object equivalent of FindUnionTargetShape/targetShape, applied here
    /// per source since offset (unlike the boolean ops) never merges multiple sources into one result.
    /// World-space geometry is baked into an Identity transform / zero pivot, the same convention
    /// ReplaceRasterWithTrace/VectorPathSceneFactory.Create already use.</summary>
    private static SceneObject BuildOffsetObject(SceneObject source, VectorPath path)
    {
        var representative = source.LocalShapes.FirstOrDefault();
        var color = representative?.LayerColor ?? RgbColor.Black;
        var result = VectorPathSceneFactory.Create(path, color, $"Offset · {source.Name}");
        if (representative is not null)
        {
            result.LocalShapes = result.LocalShapes.Select(shape => shape with
            {
                LayerId = representative.LayerId,
                PreferredMode = representative.PreferredMode,
            }).ToList();
        }
        result.IsVisible = source.IsVisible;
        result.IncludeInOutput = source.IncludeInOutput;
        return result;
    }

    /// <summary>One source's complete world geometry as ring sets, ready to fold into another source
    /// via a plain set operation (Union/Subtract/Intersect/Xor) — or null if the source contributed no
    /// closed shapes at all. Each GeometrySetId group is resolved on its own first (EvenOdd: an outer
    /// contour together with its holes collapses to one clean filled region) before the groups are
    /// unioned together, rather than treating every ring in the source as one flat EvenOdd set —
    /// otherwise two unrelated (non-nested) groups that happen to overlap on the canvas would cancel
    /// each other out by EvenOdd parity instead of merging, which is not what "select two objects and
    /// combine them" means when either object happens to already contain a hole.</summary>
    private static IReadOnlyList<IReadOnlyList<Position>>? BuildSourceRings(SceneObject source)
    {
        var normalizedShapes = NormalizeNestedCompoundPaths(source.GetWorldShapes());
        IReadOnlyList<IReadOnlyList<Position>>? sourceRings = null;
        foreach (var geometrySet in normalizedShapes.GroupBy(shape => shape.GeometrySetId))
        {
            var groupRings = geometrySet
                .Where(shape => shape.IsClosed && shape.Points.Count >= 3)
                .Select(shape => shape.Points)
                .ToList();
            if (groupRings.Count == 0) continue;

            var resolved = BooleanService.Resolve(groupRings, VectorFillRule.EvenOdd);
            sourceRings = sourceRings is null
                ? resolved
                : BooleanService.Union(sourceRings, resolved, VectorFillRule.EvenOdd);
        }

        return sourceRings;
    }

    private static double TotalArea(IReadOnlyList<IReadOnlyList<Position>> rings) =>
        rings.Sum(ring => Math.Abs(SignedArea(ring)));

    private static IReadOnlyList<ImportedShape> NormalizeNestedCompoundPaths(
        IReadOnlyList<ImportedShape> shapes)
    {
        if (shapes.Count < 2) return shapes;

        var normalized = shapes.ToList();
        for (var childIndex = 0; childIndex < normalized.Count; childIndex++)
        {
            var child = normalized[childIndex];
            var childArea = SignedArea(child.Points);
            if (!child.IsClosed || child.Points.Count < 3 || !double.IsFinite(childArea) ||
                Math.Abs(childArea) <= MinimumUnitedAreaSquareMm)
                continue;

            var childBounds = BoundsOf([child]);
            var parent = normalized
                .Where(candidate => candidate.GeometrySetId != child.GeometrySetId &&
                                    candidate.IsClosed &&
                                    candidate.Points.Count >= 3)
                .Select(candidate => new
                {
                    Shape = candidate,
                    Area = SignedArea(candidate.Points),
                    Bounds = BoundsOf([candidate]),
                })
                .Where(candidate => double.IsFinite(candidate.Area) &&
                                    Math.Sign(candidate.Area) != Math.Sign(childArea) &&
                                    Math.Abs(candidate.Area) > Math.Abs(childArea) &&
                                    Contains(candidate.Bounds, childBounds) &&
                                    IsPointInsidePolygon(child.Points[0], candidate.Shape.Points))
                .OrderBy(candidate => Math.Abs(candidate.Area))
                .FirstOrDefault();

            if (parent is null) continue;
            var previousId = child.GeometrySetId;
            var parentId = parent.Shape.GeometrySetId;
            // Repair fallback only (docs/fill-winding-contract.md): a hole is normally declared by
            // GeometrySetId + opposite winding. This path exists for legacy geometry whose contours
            // carry separate set ids (old text). It never touches shapes already in one set, and every
            // reclassification is logged so an unexpected one can be traced.
            Serilog.Log.Information(
                "Boolean pre-pass reclassified a contour into an enclosing compound path (legacy repair); child set {ChildSet} -> parent set {ParentSet}",
                previousId, parentId);
            for (var index = 0; index < normalized.Count; index++)
            {
                if (normalized[index].GeometrySetId == previousId)
                    normalized[index] = normalized[index] with { GeometrySetId = parentId };
            }
        }

        return normalized;
    }

    private static bool Contains(BoundingBox2D outer, BoundingBox2D inner)
    {
        var margin = BooleanGeometryToleranceMm * 2;
        return !outer.IsEmpty && !inner.IsEmpty &&
               inner.MinX >= outer.MinX - margin &&
               inner.MinY >= outer.MinY - margin &&
               inner.MaxX <= outer.MaxX + margin &&
               inner.MaxY <= outer.MaxY + margin;
    }

    private static bool IsPointInsidePolygon(Position point, IReadOnlyList<Position> polygon)
    {
        var inside = false;
        for (int currentIndex = 0, previousIndex = polygon.Count - 1;
             currentIndex < polygon.Count;
             previousIndex = currentIndex++)
        {
            var current = polygon[currentIndex];
            var previous = polygon[previousIndex];
            if ((current.Y > point.Y) == (previous.Y > point.Y)) continue;

            var crossingX = (previous.X - current.X) * (point.Y - current.Y) /
                            (previous.Y - current.Y) + current.X;
            if (point.X < crossingX)
                inside = !inside;
        }

        return inside;
    }

    private ImportedShape FindUnionTargetShape(
        IReadOnlyList<SceneObject> sources,
        IReadOnlyList<ImportedShape> fallbackShapes)
    {
        // Selection order depends on how the user clicked. Layer identity instead follows scene
        // z-order, using the topmost visible selected contour just like other desktop vector editors.
        return sources
            .OrderByDescending(item => Scene.Objects.IndexOf(item))
            .SelectMany(item => item.GetWorldShapes())
            .FirstOrDefault(shape => IsLayerVisible(shape.LayerId, shape.LayerColor))
            ?? fallbackShapes[0];
    }

    private void RejectVectorUnion(string reason) => VectorOperationRejected?.Invoke(
        $"Vektory nebyly změněny. {reason} Zkontrolujte překrývající se nebo velmi tenké části a zkuste výběr znovu.");

    private static List<ImportedShape> ToImportedShapes(
        IReadOnlyList<IReadOnlyList<Position>> rings,
        Guid geometrySetId,
        Guid layerId,
        RgbColor color,
        LayerMode mode)
    {
        var result = new List<ImportedShape>();
        foreach (var ring in rings)
        {
            var points = ring;
            if (points.Count > 1 && DistanceSquared(points[0], points[^1]) < 0.000001)
                points = points.Take(points.Count - 1).ToList();
            if (points.Count < 3) continue;

            result.Add(new ImportedShape
            {
                GeometrySetId = geometrySetId,
                LayerId = layerId,
                Points = points,
                IsClosed = true,
                LayerColor = color,
                PreferredMode = mode,
            });
        }

        return result;
    }

    private static bool IsSafeUnionResult(
        IReadOnlyList<ImportedShape> sourceShapes,
        IReadOnlyList<ImportedShape> resultShapes)
    {
        if (resultShapes.Count == 0 || resultShapes.SelectMany(shape => shape.Points).Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return false;

        var area = resultShapes.Sum(shape => Math.Abs(SignedArea(shape.Points)));
        if (!double.IsFinite(area) || area <= MinimumUnitedAreaSquareMm)
            return false;

        var sourceBounds = BoundsOf(sourceShapes);
        var resultBounds = BoundsOf(resultShapes);
        if (sourceBounds.IsEmpty || resultBounds.IsEmpty)
            return false;

        // Union must retain the complete extent of every input. A mismatch means WPF collapsed a
        // thin/self-intersecting contour; replacing the originals would look like data loss.
        var margin = BooleanGeometryToleranceMm * 4;
        return resultBounds.MinX <= sourceBounds.MinX + margin &&
               resultBounds.MinY <= sourceBounds.MinY + margin &&
               resultBounds.MaxX >= sourceBounds.MaxX - margin &&
               resultBounds.MaxY >= sourceBounds.MaxY - margin;
    }

    /// <summary>Subtract/Intersect/Exclude's own safety check, used instead of IsSafeUnionResult.
    /// Those three modes are *expected* to shrink the extent — removing area is the entire point —
    /// so IsSafeUnionResult's "must retain the complete extent" rule would reject every correct
    /// result from them (caught by a failing test, not just reasoned about: an early version of this
    /// reused IsSafeUnionResult verbatim and rejected a plain, valid subtract). The invariant that
    /// still catches the same real failure (a self-intersecting/degenerate contour collapsing to
    /// garbage) without assuming which direction the extent should move is the opposite one: the
    /// result must stay within the combined extent of the inputs, never producing geometry outside
    /// where the sources were.</summary>
    private static bool IsSafeBooleanResult(
        IReadOnlyList<ImportedShape> sourceShapes,
        IReadOnlyList<ImportedShape> resultShapes)
    {
        if (resultShapes.Count == 0 || resultShapes.SelectMany(shape => shape.Points).Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return false;

        var area = resultShapes.Sum(shape => Math.Abs(SignedArea(shape.Points)));
        if (!double.IsFinite(area) || area <= MinimumUnitedAreaSquareMm)
            return false;

        var sourceBounds = BoundsOf(sourceShapes);
        var resultBounds = BoundsOf(resultShapes);
        if (sourceBounds.IsEmpty || resultBounds.IsEmpty)
            return false;

        var margin = BooleanGeometryToleranceMm * 4;
        return resultBounds.MinX >= sourceBounds.MinX - margin &&
               resultBounds.MinY >= sourceBounds.MinY - margin &&
               resultBounds.MaxX <= sourceBounds.MaxX + margin &&
               resultBounds.MaxY <= sourceBounds.MaxY + margin;
    }

    private static BoundingBox2D BoundsOf(IEnumerable<ImportedShape> shapes)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in shapes.SelectMany(shape => shape.Points))
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                return BoundingBox2D.Empty;
            bounds = bounds.Include(point.X, point.Y);
        }
        return bounds;
    }

    private static double SignedArea(IReadOnlyList<Position> points)
    {
        if (points.Count < 3) return 0;
        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2;
    }

    private static double DistanceSquared(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static SceneObject CreateVectorObject(
        IReadOnlyList<ImportedShape> worldShapes,
        string name,
        bool isVisible,
        bool includeInOutput,
        VectorPath? worldVectorPath = null)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in worldShapes.SelectMany(shape => shape.Points))
            bounds = bounds.Include(point.X, point.Y);

        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var recentered = worldShapes.Select(shape => shape with
        {
            Points = shape.Points
                .Select(point => new Position(point.X - centerX, point.Y - centerY, point.Z))
                .ToList(),
        }).ToList();

        // worldVectorPath (Group's curve-preserving path, or Ungroup's recovered single subpath) is
        // already real editable geometry in world space — just recenter it the same way the shapes
        // above were. Only fall back to a fabricated straight-segment path when the caller has no
        // curve data at all (Union/Subtract/Intersect/Exclude, which fold through Clipper2's polygon
        // rings and never had curves to begin with).
        var vectorPath = worldVectorPath is not null
            ? RecenterVectorPath(worldVectorPath, centerX, centerY)
            : BuildStraightVectorPath(recentered);
        // Re-flatten LocalShapes from the VectorPath itself, the same one-directional
        // "VectorPath authoritative, LocalShapes a cached render of it" contract
        // VectorPathSceneFactory follows — rather than keep recentered's own point lists, which
        // may carry a closed shape's duplicate closing point that BuildStraightVectorPath strips
        // (VectorSubpath.Flatten() already re-adds it when re-closing). Keeping both in lockstep
        // here avoids that mismatch surfacing later as a spurious extra node on first node-edit.
        var flattened = vectorPath.FlattenAll();
        var localShapes = recentered
            .Zip(flattened, (shape, points) => shape with { Points = points })
            .ToList();

        return new SceneObject
        {
            Name = name,
            LocalShapes = localShapes,
            LocalPivot = Position.Zero,
            LocalBounds = new BoundingBox2D(
                bounds.MinX - centerX,
                bounds.MinY - centerY,
                bounds.MaxX - centerX,
                bounds.MaxY - centerY),
            Transform = ObjectTransform.Identity with { X = centerX, Y = centerY },
            IsVisible = isVisible,
            IncludeInOutput = includeInOutput,
            VectorPath = vectorPath,
        };
    }

    /// <summary>Wraps a set of already-flat, recentered ImportedShapes (Group/Ungroup/boolean
    /// results — none of which have curve data available: booleans fold through Clipper2's polygon
    /// rings, and Group merely re-centers existing polylines) into a node-editable VectorPath with
    /// one Corner-only, straight-segment VectorSubpath per shape, in the same order. Without this,
    /// every result of Group/Ungroup/Union/Subtract/Intersect/Exclude was a dead end for node
    /// editing — see docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md, "Group/Ungroup destroys
    /// VectorPath" / "Boolean ops destroy VectorPath". This does not recover the Bezier curves a
    /// source object may have had (that would require merging each source's own VectorPath
    /// subpaths, which the boolean services do not operate on at all); it guarantees the result is
    /// at least as editable as any other polyline-based node-edit path (SVG import, bitmap trace)
    /// rather than not editable at all.</summary>
    /// <summary>Translates every node's anchor and both handles by (-centerX, -centerY) — the
    /// VectorPath counterpart of the plain-point recentering already applied to worldShapes above.
    /// A pure translation (Group/Ungroup never rotate or scale geometry, only recenter it around the
    /// result object's own new pivot), so this is exact, not an approximation.</summary>
    private static VectorPath RecenterVectorPath(VectorPath path, double centerX, double centerY)
    {
        Position Shift(Position p) => new(p.X - centerX, p.Y - centerY, p.Z);
        var subpaths = path.Subpaths.Select(subpath => subpath with
        {
            Nodes = subpath.Nodes.Select(node => new VectorNode(
                Shift(node.Anchor),
                node.HandleIn is { } hi ? Shift(hi) : null,
                node.HandleOut is { } ho ? Shift(ho) : null,
                node.Type)).ToList(),
        }).ToList();
        return path with { Subpaths = subpaths };
    }

    private static VectorPath BuildStraightVectorPath(IReadOnlyList<ImportedShape> shapes)
    {
        var subpaths = shapes
            .Select(shape =>
            {
                // A closed ImportedShape conventionally repeats its first point as its last
                // (ScenePrimitiveFactory.CreateRectangle/CreateEllipse, ToImportedShapes) — but
                // VectorSubpath.Flatten already re-closes a closed subpath by wrapping its last
                // segment back to Nodes[0], so keeping that duplicate would add a coincident,
                // zero-length node the node-edit tool would show as a spurious extra corner.
                var points = shape.Points;
                if (shape.IsClosed && points.Count > 1 &&
                    DistanceSquared(points[0], points[^1]) < 0.000001)
                    points = points.Take(points.Count - 1).ToList();

                return new VectorSubpath
                {
                    Nodes = points.Select(VectorNode.CornerAt).ToList(),
                    IsClosed = shape.IsClosed,
                };
            })
            .ToList();

        return new VectorPath { Subpaths = subpaths };
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleLock()
    {
        var shouldLock = SelectedObjects.Any(item => !item.IsLocked);
        foreach (var item in SelectedObjects)
            item.IsLocked = shouldLock;
        NotifySelectionStateChanged();
        Changed?.Invoke();
    }

    public LaseroProjectFile CreateProject()
    {
        return new LaseroProjectFile
        {
            Objects = Objects.Select(obj => new ProjectObject
            {
                Name = obj.Name,
                Shapes = obj.LocalShapes.Select(shape => new ProjectShape
                {
                    GeometrySetId = shape.GeometrySetId,
                    LayerId = shape.LayerId,
                    Points = shape.Points.ToList(),
                    IsClosed = shape.IsClosed,
                    LayerColor = shape.LayerColor,
                    PreferredMode = shape.PreferredMode,
                }).ToList(),
                LocalPivot = obj.LocalPivot,
                LocalBounds = obj.LocalBounds,
                Transform = obj.Transform,
                IsVisible = obj.IsVisible,
                IsLocked = obj.IsLocked,
                IncludeInOutput = obj.IncludeInOutput,
                RasterFilePath = obj.RasterFilePath,
                RasterOptions = obj.RasterOptions,
                OriginalRasterFilePath = obj.OriginalRasterFilePath,
                Text = obj.Text,
                VectorPath = obj.VectorPath,
            }).ToList(),
            Layers = Layers.Select(layer => new ProjectLayer
            {
                Id = layer.Id,
                Color = layer.Color,
                Name = layer.Name,
                Mode = layer.Mode,
                Speed = layer.Speed,
                Power = layer.Power,
                Passes = layer.Passes,
                FillLineIntervalMm = layer.FillLineIntervalMm,
                MaterialLabel = layer.MaterialLabel,
                IsEnabled = layer.IsEnabled,
                IsVisible = layer.IsVisible,
                IsRaster = layer.IsRaster,
            }).ToList(),
        };
    }

    public void ResetDocument()
    {
        SelectedObjects.Clear();
        Objects.Clear();
        Layers.Clear();
        _commandStack.Clear();
        _importCount = 0;
        RefreshCommands();
    }

    public void LoadProject(LaseroProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // Materialize and validate the complete replacement before touching the live document.
        // A damaged project must never clear the user's current unsaved scene halfway through load.
        var loadedLayers = (project.Layers ?? throw new InvalidDataException("Projekt neobsahuje seznam vrstev."))
            .Select(layer => layer is null
                ? throw new InvalidDataException("Projekt obsahuje neplatnou vrstvu.")
                : new Lasero.Core.Layers.LayerSettings
            {
                Id = layer.Id == Guid.Empty ? Guid.NewGuid() : layer.Id,
                Color = layer.Color,
                Name = layer.Name,
                Mode = layer.Mode,
                Speed = layer.Speed,
                Power = layer.Power,
                Passes = layer.Passes,
                FillLineIntervalMm = layer.FillLineIntervalMm,
                MaterialLabel = layer.MaterialLabel,
                IsEnabled = layer.IsEnabled,
                IsVisible = layer.IsVisible,
                IsRaster = layer.IsRaster || layer.Color.IsApproximately(SceneObjectFactory.RasterEngravingColor),
            })
            .ToList();

        var loadedObjects = (project.Objects ?? throw new InvalidDataException("Projekt neobsahuje seznam objektů."))
            .Select(item =>
            {
                if (item is null) throw new InvalidDataException("Projekt obsahuje neplatný objekt.");
                var shapes = (item.Shapes ?? throw new InvalidDataException($"Objekt „{item.Name}“ nemá platnou geometrii."))
                    .Select(shape =>
                    {
                        if (shape is null || shape.Points is null)
                            throw new InvalidDataException($"Objekt „{item.Name}“ obsahuje neplatnou křivku.");
                        return new Lasero.Core.Import.ImportedShape
                        {
                            GeometrySetId = shape.GeometrySetId,
                            LayerId = ResolveLayerId(shape.LayerId, shape.LayerColor, loadedLayers),
                            Points = shape.Points,
                            IsClosed = shape.IsClosed,
                            LayerColor = shape.LayerColor,
                            PreferredMode = shape.PreferredMode,
                        };
                    })
                    .ToList();

                // Paths drawn before the pen tool learned to keep its pivot at the centre of the
                // bounds were saved with a pivot at the document origin. Moving the pivot (and
                // compensating Transform) leaves them exactly where they are and gives them working
                // selection grips, rotate and flip; objects that are already centred come back as-is.
                return new SceneObject
                {
                    LocalShapes = shapes,
                    LocalPivot = item.LocalPivot,
                    LocalBounds = item.LocalBounds,
                    RasterFilePath = item.RasterFilePath,
                    RasterOptions = item.RasterOptions,
                    OriginalRasterFilePath = item.OriginalRasterFilePath,
                    Text = item.Text,
                    VectorPath = item.VectorPath,
                    Name = item.Name,
                    Transform = item.Transform,
                    IsVisible = item.IsVisible,
                    IsLocked = item.IsLocked,
                    IncludeInOutput = item.IncludeInOutput,
                }.WithPivotAtBoundsCenter();
            })
            .ToList();

        static Guid ResolveLayerId(Guid persistedId, RgbColor color, IReadOnlyList<LayerSettings> layers)
        {
            if (persistedId != Guid.Empty && layers.Any(layer => layer.Id == persistedId))
                return persistedId;
            return layers.FirstOrDefault(layer => layer.Color.IsApproximately(color))?.Id ?? Guid.Empty;
        }

        ResetDocument();
        foreach (var layer in loadedLayers) Layers.Add(layer);
        foreach (var item in loadedObjects) Objects.Add(item);
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignCenterHorizontalCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignMiddleCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        FlipHorizontalCommand.NotifyCanExecuteChanged();
        FlipVerticalCommand.NotifyCanExecuteChanged();
        ToggleLockCommand.NotifyCanExecuteChanged();
        GroupSelectionCommand.NotifyCanExecuteChanged();
        UngroupSelectionCommand.NotifyCanExecuteChanged();
        UniteSelectionCommand.NotifyCanExecuteChanged();
        SubtractSelectionCommand.NotifyCanExecuteChanged();
        IntersectSelectionCommand.NotifyCanExecuteChanged();
        ExcludeSelectionCommand.NotifyCanExecuteChanged();
        OffsetSelectionCommand.NotifyCanExecuteChanged();
        TraceSelectedRasterCommand.NotifyCanExecuteChanged();
        RemoveSelectedBackgroundCommand.NotifyCanExecuteChanged();
        RestoreSelectedBackgroundCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(SceneObject? value)
    {
        NotifySelectionStateChanged();
        RefreshCommands();
    }

    partial void OnSelectedLayerChanged(LayerSettings? value) => NotifyLayerStateChanged();

    // --- Property-panel wrapper properties: ObjectTransform is a readonly struct, so binding a TextBox
    // straight to "Selected.Transform.X" has no settable path — these translate a set into one
    // TransformObjectCommand each, same as a completed drag gesture. Round 1 is single-selection only. ---

    public double SelectedX
    {
        get => Selected is null ? 0 : Selected.LocalPivot.X + Selected.Transform.X;
        set => SetSelectedTransform(t => t with { X = value - (Selected?.LocalPivot.X ?? 0) }, allowRaster: true);
    }

    public double SelectedY
    {
        get => Selected is null ? 0 : Selected.LocalPivot.Y + Selected.Transform.Y;
        set => SetSelectedTransform(t => t with { Y = value - (Selected?.LocalPivot.Y ?? 0) }, allowRaster: true);
    }

    public double SelectedRotation
    {
        get => Selected?.Transform.RotationDeg ?? 0;
        set => SetSelectedTransform(t => t with { RotationDeg = value }, allowRaster: false);
    }

    public double SelectedWidth
    {
        get => Selected is null ? 0 : Selected.LocalBounds.Width * Math.Abs(Selected.Transform.ScaleX);
        set => SetSelectedSize(value, isWidth: true);
    }

    public double SelectedHeight
    {
        get => Selected is null ? 0 : Selected.LocalBounds.Height * Math.Abs(Selected.Transform.ScaleY);
        set => SetSelectedSize(value, isWidth: false);
    }

    private void SetSelectedSize(double value, bool isWidth)
    {
        if (!double.IsFinite(value) || value <= 0 || Selected is not { IsLocked: false } item)
            return;

        var before = item.Transform;
        var current = isWidth ? SelectedWidth : SelectedHeight;
        if (current <= 0 || Math.Abs(current - value) < 0.0001) return;

        var factor = value / current;
        var after = isWidth
            ? before with
            {
                ScaleX = before.ScaleX * factor,
                ScaleY = LockAspectRatio ? before.ScaleY * factor : before.ScaleY,
            }
            : before with
            {
                ScaleX = LockAspectRatio ? before.ScaleX * factor : before.ScaleX,
                ScaleY = before.ScaleY * factor,
            };
        Execute(new TransformObjectCommand(item, before, after));
    }

    // ===================== Editable text =====================
    //
    // Text keeps the wording and the type settings it was built from, so changing any of them
    // re-renders the contours instead of leaving the operator with curves. Each setter goes through
    // ApplyTextSource, which is one undoable replacement of the object - the same mechanism group,
    // ungroup and unite use, so text edits sit in the same undo history as everything else.

    public bool IsTextSelected => Selected is { IsText: true };
    public bool CanEditSelectedText => Selected is { IsText: true, IsLocked: false };

    /// <summary>The families that can actually produce outlines, for the font picker. Built once:
    /// enumerating system fonts on every selection change is slow enough to be felt.</summary>
    public static IReadOnlyList<string> FontFamilies { get; } = System.Windows.Media.Fonts.SystemFontFamilies
        .Select(family => family.Source)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public string SelectedTextValue
    {
        get => Selected?.Text?.Text ?? string.Empty;
        set => ApplyTextSource(source => source with { Text = value });
    }

    public string SelectedTextFontFamily
    {
        get => Selected?.Text?.FontFamily ?? TextSource.DefaultFontFamily;
        set => ApplyTextSource(source => source with { FontFamily = value });
    }

    public double SelectedTextHeight
    {
        get => Selected?.Text?.HeightMm ?? TextSource.DefaultHeightMm;
        set => ApplyTextSource(source => source with { HeightMm = value });
    }

    public bool SelectedTextBold
    {
        get => Selected?.Text?.Bold == true;
        set => ApplyTextSource(source => source with { Bold = value });
    }

    public bool SelectedTextItalic
    {
        get => Selected?.Text?.Italic == true;
        set => ApplyTextSource(source => source with { Italic = value });
    }

    public bool SelectedTextUppercase
    {
        get => Selected?.Text?.Uppercase == true;
        set => ApplyTextSource(source => source with { Uppercase = value });
    }

    public bool SelectedTextWeld
    {
        get => Selected?.Text?.Weld == true;
        set => ApplyTextSource(source => source with { Weld = value });
    }

    /// <summary>Whether the text is currently warped. Drives the reset control, which is the only way
    /// back to square once the corners have been dragged.</summary>
    public bool IsSelectedTextDistorted => Selected?.Text?.Distortion.IsIdentity == false;

    [RelayCommand]
    private void ResetSelectedTextDistortion() =>
        ApplyTextSource(source => source with { Distortion = TextDistortion.None });

    /// <summary>Moves one corner of the distortion quad, in the text's own unit-box coordinates.
    /// Called by the canvas while a corner handle is being dragged.</summary>
    public void SetSelectedTextDistortionCorner(TextDistortionCorner corner, double u, double v) =>
        ApplyTextSource(source => source with { Distortion = source.Distortion.WithCorner(corner, u, v) });

    /// <summary>
    /// Re-renders the selected text from a changed source as one undoable edit. A source that cannot
    /// be set - an empty string, an out-of-range height, a font with no usable outlines - is reported
    /// and rejected without touching the scene, so a bad keystroke never destroys existing text.
    /// </summary>
    public void ApplyTextSource(Func<TextSource, TextSource> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (Selected is not { IsText: true, IsLocked: false } item || item.Text is null) return;

        var source = update(item.Text);
        if (source.Equals(item.Text)) return;

        SceneObject replacement;
        try
        {
            // New wording keeps the point the text starts from; a change of look (font, size, style)
            // keeps the middle, which is where the operator is looking while they pick it.
            replacement = source.Text != item.Text.Text
                ? VectorTextFactory.RebuildKeepingStart(item, source)
                : VectorTextFactory.Rebuild(item, source);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            VectorOperationRejected?.Invoke(ex.Message);
            NotifyTextStateChanged();
            return;
        }

        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        SelectedObjects.Clear();
        SelectedObjects.Add(replacement);
        NotifyTextStateChanged();
    }

    /// <summary>
    /// Commits the canvas's inline text editor as one undoable edit — the same ReplaceObjectsCommand
    /// ApplyTextSource already uses for every other change to a text object's wording or style, so
    /// typing directly on the canvas shares one undo history with the selection bar's own text field.
    /// Unlike ApplyTextSource this does not require the object to currently be Selected, since inline
    /// editing commits from a LostKeyboardFocus callback after focus may have already moved elsewhere.
    ///
    /// An empty result removes the object rather than leaving something Rebuild would reject:
    /// TextSource requires non-empty wording (see VectorTextFactory.BuildLocalGeometry), so there is
    /// no valid empty text object to fall back to — an empty inline editor becomes a delete, using the
    /// same DeleteObjectsCommand the Delete key already uses, not a bespoke removal path.
    /// </summary>
    public void CommitTextEdit(SceneObject item, string? text)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Text is null || !Objects.Contains(item)) return;

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            if (SelectedObjects.Contains(item)) SelectedObjects.Remove(item);
            Execute(new DeleteObjectsCommand(Scene, [item]));
            return;
        }

        if (trimmed == item.Text.Text) return;

        var source = item.Text with { Text = trimmed };
        SceneObject replacement;
        try
        {
            // The editor grew from where the text starts, so the committed text starts there too.
            replacement = VectorTextFactory.RebuildKeepingStart(item, source);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            VectorOperationRejected?.Invoke(ex.Message);
            return;
        }

        var wasSelected = SelectedObjects.Contains(item);
        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        if (wasSelected)
        {
            SelectedObjects.Clear();
            SelectedObjects.Add(replacement);
        }
        NotifyTextStateChanged();
    }

    private void NotifyTextStateChanged()
    {
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(CanEditSelectedText));
        OnPropertyChanged(nameof(SelectedTextValue));
        OnPropertyChanged(nameof(SelectedTextFontFamily));
        OnPropertyChanged(nameof(SelectedTextHeight));
        OnPropertyChanged(nameof(SelectedTextBold));
        OnPropertyChanged(nameof(SelectedTextItalic));
        OnPropertyChanged(nameof(SelectedTextUppercase));
        OnPropertyChanged(nameof(SelectedTextWeld));
        OnPropertyChanged(nameof(IsSelectedTextDistorted));
    }

    private void SetSelectedTransform(Func<ObjectTransform, ObjectTransform> update, bool allowRaster)
    {
        if (Selected is not { IsLocked: false } item || (!allowRaster && item.IsRaster)) return;
        var before = item.Transform;
        var after = update(before);
        if (before.Equals(after)) return;
        Execute(new TransformObjectCommand(item, before, after));
    }

    partial void OnSelectedChanging(SceneObject? oldValue, SceneObject? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedObjectPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedObjectPropertyChanged;
    }

    private void OnSelectedObjectPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneObject.Transform))
        {
            OnPropertyChanged(nameof(SelectedX));
            OnPropertyChanged(nameof(SelectedY));
            OnPropertyChanged(nameof(SelectedRotation));
            OnPropertyChanged(nameof(SelectedWidth));
            OnPropertyChanged(nameof(SelectedHeight));
        }
        else if (e.PropertyName == nameof(SceneObject.IsLocked))
        {
            NotifySelectionStateChanged();
            RefreshCommands();
        }

        if (e.PropertyName is nameof(SceneObject.Name) or nameof(SceneObject.IsLocked) or
            nameof(SceneObject.IsVisible) or nameof(SceneObject.IncludeInOutput))
        {
            Changed?.Invoke();
        }
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(AlignDisabledReason));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanEditSelectedPosition));
        OnPropertyChanged(nameof(CanTransformSelectedObject));
        OnPropertyChanged(nameof(CanRotateSelectedObject));
        OnPropertyChanged(nameof(IsSelectedLocked));
        OnPropertyChanged(nameof(CanGroupSelection));
        OnPropertyChanged(nameof(CanUngroupSelection));
        OnPropertyChanged(nameof(CanUniteSelection));
        OnPropertyChanged(nameof(UniteSelectionDisabledReason));
        OnPropertyChanged(nameof(CanOffsetSelection));
        OnPropertyChanged(nameof(OffsetSelectionDisabledReason));
        OnPropertyChanged(nameof(CanTraceSelectedRaster));
        OnPropertyChanged(nameof(CanRemoveSelectedBackground));
        OnPropertyChanged(nameof(CanRestoreSelectedBackground));
        OnPropertyChanged(nameof(IsSelectedRaster));
        OnPropertyChanged(nameof(CanAssignSelectionToLayer));
        OnPropertyChanged(nameof(SelectedX));
        OnPropertyChanged(nameof(SelectedY));
        OnPropertyChanged(nameof(SelectedRotation));
        OnPropertyChanged(nameof(SelectedWidth));
        OnPropertyChanged(nameof(SelectedHeight));
        NotifyTextStateChanged();
    }
}
