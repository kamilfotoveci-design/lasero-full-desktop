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
    private List<LayerSettings> _clipboardLayers = [];
    private bool _reconcilingSelectionEligibility;
    private bool _refreshingLinkedOffsets;
    private System.Windows.Threading.DispatcherTimer? _linkedOffsetPreviewTimer;

    public SceneDocument Scene { get; } = new();

    public ObservableCollection<SceneObject> Objects => Scene.Objects;
    public ObservableCollection<Lasero.Core.Layers.LayerSettings> Layers => Scene.Layers;

    /// <summary>Multi-select set the canvas maintains (rubber-band / shift-click). The property panel
    /// (round 1) only edits a single object, so Selected mirrors this when exactly one is selected.</summary>
    public SelectionCollection SelectedObjects { get; } = new();

    /// <summary>True right after a bitmap trace: the canvas marks the traced contours but draws no
    /// selection rectangle or handles until the operator next clicks the canvas.</summary>
    public bool HideSelectionFrame { get; set; }

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
    [ObservableProperty] private DesignerTool _currentShapeTool = DesignerTool.Square;

    /// <summary>Drives the OBRÁZEK section's inline progress UI while MainWindow sends the image to
    /// the authenticated Gemini service and awaits its result.</summary>
    [ObservableProperty] private bool _isRemovingBackground;
    [ObservableProperty] private bool _backgroundRemovalIsIndeterminate;
    [ObservableProperty] private string? _backgroundRemovalStatus;
    [ObservableProperty] private string? _backgroundRemovalError;
    private readonly HashSet<SceneObject> _observedSelection = [];
    private readonly HashSet<LayerSettings> _hookedLayers = [];
    private readonly Dictionary<LayerSettings, LayerSettingsState> _pendingLayerEdits = [];
    private bool _layerHistoryScheduled;

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
    public bool CanEditSelectedPosition => HasSelection && SelectedObjects.All(item => !item.IsLocked);
    public bool CanTransformSelectedObject => CanEditSelectedPosition;
    public bool CanRotateSelectedObject => Selected is { IsLocked: false, IsRaster: false };
    public bool IsSelectedLocked => Selected?.IsLocked == true;
    public bool CanGroupSelection => ExpandedGroupSelection() is { Count: >= 2 } members &&
        members.All(item => !item.IsLocked);
    public bool CanUngroupSelection => SelectedObjects.Select(item => item.GroupId)
        .OfType<Guid>().Any(id => Objects.Where(item => item.GroupId == id).All(item => !item.IsLocked));
    public bool CanBreakApartSelection => SelectedObjects.Count == 1 && Selected is
        { IsRaster: false, IsLocked: false, IsText: false, LocalShapes.Count: > 1 } item &&
        (item.VectorPath is null || item.VectorPath.Subpaths.Count == item.LocalShapes.Count);
    /// <summary>Null when the selection contains valid closed vector geometry; otherwise a specific Czech
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
            if (SelectedObjects.Any(item => item.IsRaster)) return "Bitmapu nelze sjednotit ani kombinovat. Lze vybrat pouze vektory.";
            if (SelectedObjects.Any(item => item.IsLocked)) return "Zamknuté objekty nelze sjednotit ani kombinovat. Nejprve je potřeba je odemknout.";
            if (ExpandedGroupSelection().Any(item => !SelectedObjects.Contains(item)))
                return "Vybraná skupina obsahuje skryté nebo zamknuté členy. Zpřístupni všechny členy před booleovskou operací.";
            if (SelectedObjects.SelectMany(item => item.LocalShapes).Count() < 2) return "Je potřeba vybrat alespoň dva tvary.";
            if (SelectedObjects.SelectMany(item => item.LocalShapes).Any(shape => !shape.IsClosed || shape.Points.Count < 3))
                return "Výběr obsahuje otevřenou dráhu — booleovské operace vyžadují uzavřené tvary.";
            return null;
        }
    }
    /// <summary>Why Align is unavailable, or null when it works. Alignment is relative to the other
    /// selected objects, so a single object has nothing to align to.</summary>
    public string? AlignDisabledReason => SelectedObjects.Count switch
    {
        0 => "K zarovnání je potřeba vybrat alespoň dva objekty",
        1 => "K zarovnání je potřeba vybrat alespoň dva objekty. Jeden objekt nemá k čemu zarovnat",
        _ => null,
    };

    public string? BooleanPairSelectionDisabledReason => UniteSelectionDisabledReason ??
        (BooleanOperands().Count == 2 ? null : "Tato operace pracuje přesně se dvěma vybranými objekty nebo skupinami.");
    public bool CanUniteSelection => BooleanPairSelectionDisabledReason is null;
    public bool CanWeldSelection => (BooleanOperands().Count >= 2 && UniteSelectionDisabledReason is null) || CanWeldOpenPaths;
    public bool CanBooleanPairSelection => BooleanPairSelectionDisabledReason is null;
    public bool CanDivideSelection => BooleanOperands().Count == 2 &&
        ExpandedGroupSelection().All(SelectedObjects.Contains) &&
        SelectedObjects.All(item => !item.IsRaster && !item.IsLocked &&
            item.GetWorldShapes().Any() && item.GetWorldShapes().All(shape => shape.IsClosed && shape.Points.Count >= 3));
    public bool CanFractureSelection => BooleanOperands().Count >= 2 &&
        ExpandedGroupSelection().All(SelectedObjects.Contains) &&
        SelectedObjects.All(item => !item.IsRaster && !item.IsLocked &&
            item.GetWorldShapes().Any() && item.GetWorldShapes().All(shape => shape.IsClosed && shape.Points.Count >= 3));
    public bool CanFillBetweenPaths => SelectedObjects.Count == 2 &&
        SelectedObjects.All(item => !item.IsRaster && !item.IsLocked && item.VectorPath is { Subpaths.Count: 1 } path &&
            !path.Subpaths[0].IsClosed && path.Subpaths[0].Nodes.Count >= 2);
    public bool CanEditSelectedVectorPaths => SelectedObjects.Count > 0 &&
        SelectedObjects.All(item => !item.IsRaster && !item.IsLocked && item.VectorPath is { Subpaths.Count: > 0 });
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
            if (SelectedObjects.Any(item => item.IsRaster)) return "Bitmapu nelze posunout offsetem. Lze vybrat vektor.";
            if (SelectedObjects.Any(item => item.IsLocked)) return "Zamknuté objekty nelze upravit offsetem. Nejprve je potřeba je odemknout.";
            if (!SelectedObjects.SelectMany(item => item.GetWorldShapes()).Any(shape => shape.Points.Count >= 2))
                return "Vybraný tvar neobsahuje žádnou dráhu k posunutí.";
            return null;
        }
    }
    public bool CanOffsetSelection => OffsetSelectionDisabledReason is null;
    public bool CanRemoveSelectedBackground => !IsRemovingBackground && SelectedObjects.Count == 1 && Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false };
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
    public string? DeleteSelectedLayerDisabledReason
    {
        get
        {
            if (SelectedLayer is null) return "Nejprve vyber vrstvu.";
            if (Layers.Count <= 1) return "Poslední vrstvu nelze odstranit.";
            if (Objects.Any(item => !DeleteLayerCommand.CanKeepOtherContours(item, SelectedLayer)))
                return "Tato vrstva sdílí editovatelný text nebo cestu, kterou nelze bezpečně rozdělit po obrysech.";
            return null;
        }
    }
    public bool CanDeleteSelectedLayer => DeleteSelectedLayerDisabledReason is null;
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
    public event Action<SceneObject>? EditRasterRequested;
    public event Action<string>? RasterOperationRejected;
    public event Action<SceneObject>? BackgroundRemovalRequested;
    public event Action? BackgroundRemovalCancelRequested;
    public event Action<string>? VectorOperationRejected;
    public event Action<VectorOperationPreview>? BooleanPreviewRequested;

    /// <summary>Report a vector edit rejection from a canvas interaction without exposing the event
    /// invocation outside this view model.</summary>
    public void ReportVectorOperationRejected(string message) => VectorOperationRejected?.Invoke(message);
    /// <summary>Raised by OffsetSelectionCommand — MainWindow owns opening OffsetPathWindow and
    /// calling ApplyOffset back with its result, the same hand-off shape TraceRasterRequested already
    /// uses for BitmapTraceWindow.</summary>
    public event Action<IReadOnlyList<SceneObject>, VectorOffsetDirection?>? OffsetRequested;

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
        // A font popup can still be open while selection moves to another object. Its temporary
        // geometry belongs only to the original selection and must not remain in the document.
        if (_fontPreviewShown is not null && !SelectedObjects.Contains(_fontPreviewShown))
            EndTextFontPreview();
        if (!_reconcilingSelectionEligibility)
        {
            var ineligible = SelectedObjects.Where(item => !CanSelectObject(item)).ToList();
            if (ineligible.Count > 0)
            {
                _reconcilingSelectionEligibility = true;
                try
                {
                    foreach (var item in ineligible) SelectedObjects.Remove(item);
                }
                finally { _reconcilingSelectionEligibility = false; }
            }
        }

        foreach (var item in _observedSelection.Except(SelectedObjects).ToList())
        {
            item.PropertyChanged -= OnSelectedObjectPropertyChanged;
            _observedSelection.Remove(item);
        }
        foreach (var item in SelectedObjects.Where(item => _observedSelection.Add(item)))
            item.PropertyChanged += OnSelectedObjectPropertyChanged;
        Selected = SelectedObjects.Count == 1 ? SelectedObjects[0] : null;
        var selectedColors = SelectedObjects
            .SelectMany(item => item.LocalShapes)
            .Select(shape => shape.LayerColor)
            .ToList();
        if (selectedColors.Count > 0 && selectedColors.All(color => color.IsApproximately(selectedColors[0])))
            SelectedLayer = Layers.FirstOrDefault(layer => layer.Color.IsApproximately(selectedColors[0]));
        if (!IsRemovingBackground)
        {
            BackgroundRemovalError = null;
            BackgroundRemovalStatus = null;
        }
        NotifySelectionStateChanged();
        NotifyLayerStateChanged();
        RefreshCommands();
    }

    /// <summary>Bumped by every execute, undo, redo and clear of the undo stack. A gesture that wants to
    /// extend its own newest step (keyboard nudge) trusts that step only while this is unchanged.</summary>
    public int CommandRevision { get; private set; }

    /// <summary>Swaps the newest undo step for <paramref name="merged"/> (the same step carried further),
    /// so a held arrow key stays one Ctrl+Z. Refuses, and changes nothing, when anything else touched the
    /// stack since <paramref name="expectedRevision"/>, including a layer edit flushed right now.</summary>
    public bool TryReplaceLastCommand(int expectedRevision, ISceneCommand merged)
    {
        FlushPendingLayerEdits();
        if (expectedRevision != CommandRevision || !CanUndo) return false;
        ApplyCommand(() =>
        {
            _commandStack.Undo();
            _commandStack.Execute(merged);
        });
        return true;
    }

    private void OnCommandStackChanged()
    {
        CommandRevision++;
        _linkedOffsetPreviewTimer?.Stop();
        RefreshLinkedOffsets();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        NotifyLayerStateChanged();
        RefreshCommands();
        _historyCompleting = true;
        try { Changed?.Invoke(); }
        finally { _historyCompleting = false; }
    }

    private bool _historyCompleting;
    private int _gesturePreviewDepth;

    /// <summary>Classifies the <see cref="Changed"/> being raised right now (valid only inside a handler).</summary>
    public SceneChangeOrigin ChangeOrigin =>
        _historyCompleting ? SceneChangeOrigin.HistoryCompleted
        : IsApplyingCommand && _gesturePreviewDepth == 0 ? SceneChangeOrigin.HistoryInProgress
        : SceneChangeOrigin.Other;

    /// <summary>True when undo/redo has returned to the state recorded by <see cref="MarkSaved"/>. Says nothing
    /// about edits made outside the history; the owner tracks those via <see cref="ChangeOrigin"/>.</summary>
    public bool IsAtSavePoint => _commandStack.IsAtSavePoint;

    /// <summary>Records the current history position as the saved document. Pending layer edits are committed
    /// first so the saved snapshot and the recorded position describe the same state.</summary>
    public void MarkSaved()
    {
        FlushPendingLayerEdits();
        _commandStack.MarkSaved();
    }

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var layer in _hookedLayers)
            {
                layer.PropertyChanged -= OnLayerPropertyChanged;
                layer.PropertyChanging -= OnLayerPropertyChanging;
            }
            _hookedLayers.Clear();
            _pendingLayerEdits.Clear();
            foreach (var layer in Layers) HookLayer(layer);
        }
        if (e.OldItems is not null) foreach (Lasero.Core.Layers.LayerSettings layer in e.OldItems)
        {
            layer.PropertyChanged -= OnLayerPropertyChanged;
            layer.PropertyChanging -= OnLayerPropertyChanging;
            _hookedLayers.Remove(layer);
            _pendingLayerEdits.Remove(layer);
        }
        if (e.NewItems is not null) foreach (Lasero.Core.Layers.LayerSettings layer in e.NewItems) HookLayer(layer);
        if (SelectedLayer is null || !Layers.Contains(SelectedLayer))
            SelectedLayer = Layers.FirstOrDefault();
        if (e.Action != NotifyCollectionChangedAction.Reset) Changed?.Invoke();
    }

    private void HookLayer(Lasero.Core.Layers.LayerSettings layer)
    {
        if (!_hookedLayers.Add(layer)) return;
        layer.PropertyChanged += OnLayerPropertyChanged;
        layer.PropertyChanging += OnLayerPropertyChanging;
    }

    private static bool IsStoredLayerProperty(string? name) => name is
        nameof(LayerSettings.Color) or nameof(LayerSettings.Name) or nameof(LayerSettings.Mode) or
        nameof(LayerSettings.Speed) or nameof(LayerSettings.Power) or nameof(LayerSettings.Passes) or
        nameof(LayerSettings.FillLineIntervalMm) or nameof(LayerSettings.MaterialLabel) or
        nameof(LayerSettings.IsEnabled) or nameof(LayerSettings.IsVisible) or
        nameof(LayerSettings.IsLocked) or nameof(LayerSettings.IsRaster);

    private void OnLayerPropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (IsApplyingCommand || sender is not LayerSettings layer || !IsStoredLayerProperty(e.PropertyName)) return;
        _pendingLayerEdits.TryAdd(layer, LayerSettingsState.Capture(layer));
    }

    private void ScheduleLayerHistory()
    {
        if (_layerHistoryScheduled || _pendingLayerEdits.Count == 0) return;
        _layerHistoryScheduled = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                _layerHistoryScheduled = false;
                FlushPendingLayerEdits();
            }));
    }

    private void FlushPendingLayerEdits()
    {
        if (_pendingLayerEdits.Count == 0) return;
        var pending = _pendingLayerEdits.ToList();
        _pendingLayerEdits.Clear();
        foreach (var (layer, before) in pending)
        {
            if (!Layers.Contains(layer)) continue;
            var after = LayerSettingsState.Capture(layer);
            if (before == after) continue;
            ApplyCommand(() => _commandStack.Execute(new LayerSettingsHistoryCommand(layer, before, after)));
        }
    }
    /// <summary>
    /// False while <see cref="Changed"/> is being raised for an edit that cannot change what the canvas
    /// draws (a layer's power, speed, passes, line interval, name or output switch). Job generation
    /// still hears about every change; the canvas uses this to skip a full refresh of every object.
    /// </summary>
    public bool ChangedAffectsCanvas { get; private set; } = true;

    private static bool LayerPropertyAffectsCanvas(string? propertyName) =>
        propertyName is null or "" or nameof(Lasero.Core.Layers.LayerSettings.Color)
            or nameof(Lasero.Core.Layers.LayerSettings.Mode)
            or nameof(Lasero.Core.Layers.LayerSettings.IsVisible)
            or nameof(Lasero.Core.Layers.LayerSettings.IsRaster)
            or nameof(Lasero.Core.Layers.LayerSettings.Id);

    private void OnLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsApplyingCommand && IsStoredLayerProperty(e.PropertyName)) ScheduleLayerHistory();
        var affectsCanvas = LayerPropertyAffectsCanvas(e.PropertyName);
        if (e.PropertyName is nameof(LayerSettings.IsVisible) or nameof(LayerSettings.IsLocked))
            ReconcileSelectionEligibility();
        if (affectsCanvas)
            foreach (var item in Objects)
                item.InvalidateLayerAppearance();
        NotifyLayerStateChanged();
        ChangedAffectsCanvas = affectsCanvas;
        try { Changed?.Invoke(); }
        finally { ChangedAffectsCanvas = true; }
    }

    private int CountObjectsUsingLayer(LayerSettings layer) => Objects.Count(item => UsesLayer(item, layer));

    private static bool ShapeUsesLayer(ImportedShape shape, LayerSettings layer) => shape.LayerId != Guid.Empty
        ? shape.LayerId == layer.Id
        : shape.LayerColor.IsApproximately(layer.Color);

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
        OnPropertyChanged(nameof(DeleteSelectedLayerDisabledReason));
        OnPropertyChanged(nameof(CanMoveSelectedLayerUp));
        OnPropertyChanged(nameof(CanMoveSelectedLayerDown));
        OnPropertyChanged(nameof(CanReorderLayers));
        OnPropertyChanged(nameof(SelectedLayerUsageLabel));
        DeleteSelectedLayerCommand.NotifyCanExecuteChanged();
        MoveSelectedLayerUpCommand.NotifyCanExecuteChanged();
        MoveSelectedLayerDownCommand.NotifyCanExecuteChanged();
        SelectAllInActiveLayerCommand.NotifyCanExecuteChanged();
        MergeSelectedLayerIntoCommand.NotifyCanExecuteChanged();
        MergeSelectedLayerIntoWithSourceSettingsCommand.NotifyCanExecuteChanged();
    }

    public bool IsLayerVisible(Lasero.Core.Layers.RgbColor color) =>
        Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color))?.IsVisible ?? true;

    public bool IsLayerVisible(Guid layerId, Lasero.Core.Layers.RgbColor legacyColor) =>
        (layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor)))?.IsVisible ?? true;

    public bool IsLayerLocked(Guid layerId, Lasero.Core.Layers.RgbColor legacyColor) =>
        (layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor)))?.IsLocked ?? false;

    /// <summary>Central gate for ordinary canvas selection. If one scene object spans multiple
    /// layers, every contour must be editable before the whole object can be transformed.</summary>
    public bool CanSelectObject(SceneObject item) =>
        Objects.Contains(item) && item.IsVisible && !item.IsLocked &&
        (item.LocalShapes.Count == 0 || item.LocalShapes.All(shape =>
            IsLayerVisible(shape.LayerId, shape.LayerColor) && !IsLayerLocked(shape.LayerId, shape.LayerColor)));

    private void ReconcileSelectionEligibility()
    {
        var ineligible = SelectedObjects.Where(item => !CanSelectObject(item)).ToList();
        foreach (var item in ineligible) SelectedObjects.Remove(item);
    }

    public LayerMode LayerModeFor(RgbColor color) =>
        Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color))?.Mode ?? LayerMode.Cut;

    public LayerMode LayerModeFor(Guid layerId, RgbColor legacyColor) =>
        (layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor)))?.Mode ?? LayerMode.Cut;

    /// <summary>Executed by every mutating gesture the canvas performs (drag-move/rotate/resize on
    /// MouseUp, keyboard nudge) — kept public so SceneCanvas doesn't need its own reference to the stack.</summary>
    public void Execute(ISceneCommand command)
    {
        FlushPendingLayerEdits();
        ApplyCommand(() => _commandStack.Execute(command));
    }

    /// <summary>
    /// Runs a live, uncommitted assignment of several objects' transforms during a group drag. It uses
    /// the same bracket as a command so the per-object notifications collapse: the inspector's
    /// X/Y/size fields are told once afterwards instead of once per object (quadratic in the selection),
    /// and the canvas, which skips per-object repaints while this is open, refreshes what it needs
    /// itself. It does not touch the undo stack; the gesture commits one command on mouse-up.
    /// </summary>
    public void ApplyGesturePreview(Action assign)
    {
        _gesturePreviewDepth++;
        try { ApplyCommand(assign); }
        finally { _gesturePreviewDepth--; }
        ScheduleLinkedOffsetPreview();
    }

    private void ScheduleLinkedOffsetPreview()
    {
        if (!Objects.Any(item => item.OffsetLink is not null)) return;
        _linkedOffsetPreviewTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(120), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _linkedOffsetPreviewTimer!.Stop();
                RefreshLinkedOffsets();
                Changed?.Invoke();
            }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _linkedOffsetPreviewTimer.Stop();
        _linkedOffsetPreviewTimer.Start();
    }

    private int _commandDepth;
    private bool _selectedTransformNotificationPending;
    private bool _selectedGroupNotificationPending;

    /// <summary>
    /// True while a command is being executed, undone or redone. Every such operation ends in
    /// <see cref="Changed"/>, which makes the canvas refresh once; per-object notifications in between
    /// would repaint the scene and the whole selection overlay once for each object of a multi-object
    /// command (quadratic in the selection size).
    /// </summary>
    public bool IsApplyingCommand => _commandDepth > 0;

    private void ApplyCommand(Action operation)
    {
        _commandDepth++;
        try { operation(); }
        finally { _commandDepth--; }

        // The inspector's X/Y/size fields describe the whole selection; tell them once, afterwards.
        if (_commandDepth == 0 && _selectedTransformNotificationPending)
        {
            _selectedTransformNotificationPending = false;
            NotifySelectedTransformChanged();
        }
        if (_commandDepth == 0 && _selectedGroupNotificationPending)
        {
            _selectedGroupNotificationPending = false;
            NotifySelectionStateChanged();
            RefreshCommands();
        }
    }

    private void NotifySelectedTransformChanged()
    {
        OnPropertyChanged(nameof(SelectedX));
        OnPropertyChanged(nameof(SelectedY));
        OnPropertyChanged(nameof(SelectedRotation));
        OnPropertyChanged(nameof(SelectedWidth));
        OnPropertyChanged(nameof(SelectedHeight));
        OnPropertyChanged(nameof(CanEditSelectedPosition));
        OnPropertyChanged(nameof(CanTransformSelectedObject));
    }

    private (double X, double Y) NextCascadeOffset()
    {
        _importCount++;
        var offset = CascadeOffsetMm * _importCount;
        return (offset, -offset); // down-right cascade in canvas terms = +X, -Y in work coordinates (Y grows up)
    }

    /// <summary>Imports an SVG as a new object. Returns what the importer could not carry over (empty when
    /// nothing was lost) so the caller can show it as non-blocking text. An SVG that yields no geometry at
    /// all is rejected rather than adding an empty object to the canvas.</summary>
    public IReadOnlyList<string> ImportSvgFile(string path, double targetWidthMm, bool useDocumentSize = false)
    {
        var svgText = File.ReadAllText(path);
        var doc = SvgImporter.Import(svgText, targetWidthMm, Path.GetFileName(path), useDocumentSize);
        if (doc.Shapes.Count == 0)
        {
            var reason = doc.ImportWarnings.Count > 0 ? " " + string.Join(" ", doc.ImportWarnings) : string.Empty;
            throw new InvalidDataException("Nic nebylo naimportováno." + reason);
        }
        var obj = SceneObjectFactory.FromImportedDocument(doc, doc.SourceFileName ?? Path.GetFileName(path));
        PlaceAndAdd(obj, doc.Layers);
        FileImported?.Invoke();
        return doc.ImportWarnings;
    }

    /// <summary>Raised after a graphics file (SVG or bitmap) has been placed on the canvas. Only the
    /// first-run tips listen; it carries no data and changes nothing.</summary>
    public event Action? FileImported;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ImportRasterFile(string path, RasterImportOptions options)
    {
        if (SelectedLayer is { IsVisible: false } or { IsLocked: true })
        {
            RasterOperationRejected?.Invoke("Aktivní vrstva je skrytá nebo zamčená. Před vložením obrázku je potřeba ji zobrazit a odemknout.");
            return;
        }

        path = RasterImportSource.NormalizeWebp(path);
        var layer = Layers.FirstOrDefault(candidate => candidate.IsRaster && candidate.IsVisible && !candidate.IsLocked)
            ?? SceneObjectFactory.CreateRasterLayer(options);
        var obj = SceneObjectFactory.FromRaster(path, options, Path.GetFileName(path), layer);
        PlaceAndAdd(obj, [layer]);
        FileImported?.Invoke();
    }

    public void ImportRasterFiles(IEnumerable<string> paths, RasterImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var inputPaths = paths.Where(File.Exists).ToList();
        if (inputPaths.Count == 0) return;
        if (SelectedLayer is { IsVisible: false } or { IsLocked: true })
        {
            RasterOperationRejected?.Invoke("Aktivní vrstva je skrytá nebo zamčená. Před vložením obrázku je potřeba ji zobrazit a odemknout.");
            return;
        }

        var layer = Layers.FirstOrDefault(candidate => candidate.IsRaster && candidate.IsVisible && !candidate.IsLocked)
            ?? SceneObjectFactory.CreateRasterLayer(options);
        var objects = new List<SceneObject>();
        var commands = new List<ISceneCommand>();
        foreach (var sourcePath in inputPaths)
        {
            var path = RasterImportSource.NormalizeWebp(sourcePath);
            var obj = SceneObjectFactory.FromRaster(path, options, Path.GetFileName(sourcePath), layer);
            var (offsetX, offsetY) = NextCascadeOffset();
            obj.Transform = obj.Transform with { X = offsetX, Y = offsetY };
            objects.Add(obj);
            commands.Add(new AddObjectCommand(Scene, obj, [layer]));
        }

        Execute(commands.Count == 1 ? commands[0] : new CompositeSceneCommand(commands));
        SelectedObjects.ReplaceWith(objects);
        ActiveTool = DesignerTool.Select;
        FileImported?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanTraceSelectedRaster))]
    private void TraceSelectedRaster()
    {
        if (Selected is { IsRaster: true, IsLocked: false } source)
            TraceRasterRequested?.Invoke(source);
    }

    public bool CanEditSelectedRaster => SelectedObjects.Count == 1 && Selected is { IsRaster: true } item && CanSelectObject(item);

    [RelayCommand(CanExecute = nameof(CanEditSelectedRaster))]
    private void EditSelectedRaster()
    {
        if (CanEditSelectedRaster && Selected is { } source) EditRasterRequested?.Invoke(source);
    }

    public void ApplyRasterEdit(SceneObject source, RasterImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!Objects.Contains(source) || !CanSelectObject(source) || source.RasterFilePath is not { } path) return;
        var layerId = source.LocalShapes.FirstOrDefault()?.LayerId ?? Guid.Empty;
        var layer = Layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? Layers.FirstOrDefault(candidate => candidate.IsRaster);
        if (layer is null) return;

        var rebuilt = SceneObjectFactory.FromRaster(path, options, source.Name, layer);
        var replacement = new SceneObject
        {
            Id = source.Id,
            GroupId = source.GroupId,
            NestedGroupIds = source.NestedGroupIds.ToArray(),
            Name = source.Name,
            LocalShapes = rebuilt.LocalShapes,
            LocalPivot = rebuilt.LocalPivot,
            LocalBounds = rebuilt.LocalBounds,
            // The dialog edits the bitmap's size in mm, so the rebuilt local bounds already ARE the new
            // size: scale goes back to unit (flip signs kept), and X/Y absorb the pivot move so the
            // bitmap keeps its centre on the canvas.
            Transform = source.Transform with
            {
                ScaleX = source.Transform.ScaleX < 0 ? -1 : 1,
                ScaleY = source.Transform.ScaleY < 0 ? -1 : 1,
                X = source.LocalPivot.X + source.Transform.X - rebuilt.LocalPivot.X,
                Y = source.LocalPivot.Y + source.Transform.Y - rebuilt.LocalPivot.Y,
            },
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            IncludeInOutput = source.IncludeInOutput,
            RasterFilePath = path,
            RasterOptions = options,
            OriginalRasterFilePath = source.OriginalRasterFilePath,
        };

        // The bitmap layer owns speed, power, passes and line interval for every bitmap on it (that is
        // what reaches G-code), so the dialog's values are written into the layer as part of the same
        // undo step instead of being silently overridden at output time.
        var commands = new List<ISceneCommand>();
        var before = LayerSettingsState.Capture(layer);
        var after = before with
        {
            Speed = options.FeedRatePerMinute,
            Power = options.MaxPower,
            Passes = options.Passes,
            FillLineIntervalMm = options.LineIntervalMm,
        };
        if (after != before) commands.Add(new LayerSettingsHistoryCommand(layer, before, after));
        commands.Add(new ReplaceObjectsCommand(Scene, [source], [replacement]));
        Execute(commands.Count == 1 ? commands[0] : new CompositeSceneCommand(commands));
        SelectedObjects.ReplaceWith([replacement]);
    }

    /// <summary>Requests cloud background removal — MainWindow owns consent, HTTP work and the
    /// processing-state UI, while this view model remains responsible for document undo/redo.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedBackground))]
    private void RemoveSelectedBackground()
    {
        if (CanRemoveSelectedBackground && Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false } source)
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
    public void ReplaceRasterWithTrace(SceneObject source, BitmapTraceResult result, bool deleteSource = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        if (!source.IsRaster || !Objects.Contains(source))
            throw new InvalidOperationException("The bitmap is no longer available in the scene.");
        if (result.VectorPaths.Count == 0)
            throw new InvalidOperationException("Tracing did not produce any vector contours.");

        // The whole trace is one object per colour (the logo is one object), so it selects and scales as a
        // single piece with the normal frame and handles instead of as hundreds of loose contours.
        var mergedPaths = result.VectorPaths
            .GroupBy(item => item.Color)
            .Select(group => (Color: group.Key, Path: new VectorPath { Subpaths = group.SelectMany(item => item.Path.Subpaths).ToList() }))
            .ToList();
        var multipleObjects = mergedPaths.Count > 1;
        var tracedObjects = new List<SceneObject>(mergedPaths.Count);
        for (var index = 0; index < mergedPaths.Count; index++)
        {
            var traced = mergedPaths[index];
            var worldPath = ToWorldSpace(traced.Path, source.Transform, source.LocalPivot);
            var name = multipleObjects ? $"Trasování · {source.Name} ({index + 1})" : $"Trasování · {source.Name}";
            var tracedObject = VectorPathSceneFactory.Create(worldPath, traced.Color, name);
            var layer = result.Document.Layers.FirstOrDefault(candidate => candidate.Color == traced.Color);
            if (layer is not null) tracedObject.AssignToLayer(layer);
            tracedObject.IsVisible = source.IsVisible;
            tracedObject.IncludeInOutput = source.IncludeInOutput;
            tracedObjects.Add(tracedObject);
        }

        // Deleting the bitmap must not leave its "Bitmapa" operation behind: when it was the last
        // bitmap in the scene, its raster operation goes with it (undo brings both back).
        var orphanedLayers = deleteSource && !Objects.Any(item => item.IsRaster && !ReferenceEquals(item, source))
            ? Layers.Where(layer => layer.IsRaster).ToList()
            : [];
        Execute(new ReplaceObjectsCommand(
            Scene, deleteSource ? new[] { source } : Array.Empty<SceneObject>(), tracedObjects, result.Document.Layers, orphanedLayers));
        SelectedObjects.ReplaceWith(tracedObjects);
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
            [Lasero.Core.Layers.LayerSettings.CreateDefault(
                color, Lasero.Core.Layers.LayerMode.Fill, "Text", removeWhenEmpty: true)]);
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

    /// <summary>One unlocked vector object that is not yet an editable path (a primitive, an imported
    /// outline, converted text) and has at least one contour long enough to become one.</summary>
    public bool CanConvertSelectionToCurves =>
        SelectedObjects.Count == 1
        && Selected is { IsLocked: false, IsRaster: false, IsText: false, IsVectorPath: false } source
        && source.LocalShapes.Any(shape =>
        {
            var points = shape.Points;
            var uniqueCount = shape.IsClosed && points.Count > 1 && points[0] == points[^1] ? points.Count - 1 : points.Count;
            return uniqueCount >= (shape.IsClosed ? 3 : 2);
        });

    /// <summary>Converts one simple vector outline into an editable path without changing its
    /// rendered geometry, placement, layer, or output settings. The conversion is one undoable
    /// replacement; the caller may then enter Node Edit mode on the returned object.</summary>
    public SceneObject? ConvertSelectedObjectToCurves()
    {
        if (!CanConvertSelectionToCurves) return null;

        if (Selected is not { IsLocked: false, IsRaster: false, IsText: false, IsVectorPath: false } source
            || source.LocalShapes.Count == 0)
            return null;

        var replacement = BuildCurveConversion(source);
        if (replacement is null) return null;

        Execute(new ReplaceObjectsCommand(Scene, [source], [replacement]));
        SelectedObjects.Clear();
        SelectedObjects.Add(replacement);
        return replacement;
    }

    /// <summary>Scissors/Trim on a plain outline as ONE undo step: converts <paramref name="source"/>
    /// to an editable path in memory, lets <paramref name="edit"/> change that path (return null to
    /// cancel, or a path with no subpaths to ask for the object to be deleted instead) and commits
    /// conversion plus edit as a single replacement. Returns the committed object, or null.</summary>
    public SceneObject? ConvertAndEditVectorPath(
        SceneObject source, Func<SceneObject, VectorPath, VectorPath?> edit, out bool deleteRequested)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edit);
        deleteRequested = false;
        if (!Objects.Contains(source) || source is not { IsLocked: false, IsRaster: false, IsText: false, IsVectorPath: false })
            return null;

        var converted = BuildCurveConversion(source);
        if (converted?.VectorPath is not { } convertedPath) return null;
        var edited = edit(converted, convertedPath);
        if (edited is null) return null;
        if (edited.Subpaths.Count == 0)
        {
            deleteRequested = true;
            return null;
        }

        SceneObject final;
        try
        {
            final = VectorPathSceneFactory.Rebuild(converted, edited);
        }
        catch (InvalidOperationException ex)
        {
            VectorOperationRejected?.Invoke(ex.Message);
            return null;
        }

        Execute(new ReplaceObjectsCommand(Scene, [source], [final]));
        SelectedObjects.Clear();
        SelectedObjects.Add(final);
        return final;
    }

    /// <summary>Builds (without executing anything) the editable-path version of a plain outline:
    /// one subpath per contour, ellipses as true Bezier nodes, LocalShapes always in step with the
    /// subpaths. Null when no contour is long enough to become a path.</summary>
    internal static SceneObject? BuildCurveConversion(SceneObject source)
    {
        if (source is not { IsRaster: false, IsText: false, IsVectorPath: false } || source.LocalShapes.Count == 0)
            return null;

        // One subpath per contour, so converted text (letters and their counters) or a multi-part
        // import keeps every contour. Contours too short to be a path are skipped.
        var subpaths = new List<VectorSubpath>(source.LocalShapes.Count);
        // Shapes that became subpaths, in the same order: LocalShapes[i] must stay the flattening of
        // Subpaths[i], so skipped contours are dropped from the shape list as well.
        var keptShapes = new List<ImportedShape>(source.LocalShapes.Count);
        var hasTrueCurves = false;
        foreach (var shape in source.LocalShapes)
        {
            var points = shape.Points;
            if (points.Count < (shape.IsClosed ? 3 : 2)) continue;
            var uniqueCount = points.Count;
            if (shape.IsClosed && uniqueCount > 1 && points[0] == points[^1]) uniqueCount--;
            if (uniqueCount < (shape.IsClosed ? 3 : 2)) continue;
            if (shape.IsClosed && VectorPathEditor.TryFitEllipseSubpath(points, uniqueCount, out var ellipse))
            {
                subpaths.Add(ellipse);
                hasTrueCurves = true;
            }
            else if (shape.IsClosed && RectangleCurveFit.TryFit(points, uniqueCount, out var rectangle))
            {
                subpaths.Add(rectangle);
                hasTrueCurves |= rectangle.Nodes.Any(node => node.HasAnyHandle);
            }
            else
            {
                subpaths.Add(new VectorSubpath
                {
                    Nodes = points.Take(uniqueCount).Select(VectorNode.CornerAt).ToArray(),
                    IsClosed = shape.IsClosed,
                });
            }
            keptShapes.Add(shape);
        }

        if (subpaths.Count == 0) return null;
        var path = new VectorPath { Subpaths = subpaths };
        SceneObject replacement;
        if (!hasTrueCurves && keptShapes.Count == source.LocalShapes.Count)
        {
            // Corner-only nodes flatten to the very same ring (a plain rectangle's four corners
            // included), so the existing geometry is kept untouched.
            replacement = new SceneObject
            {
                Id = source.Id,
                GroupId = source.GroupId,
                NestedGroupIds = source.NestedGroupIds.ToArray(),
                Name = source.Name,
                LocalShapes = source.LocalShapes,
                LocalPivot = source.LocalPivot,
                LocalBounds = source.LocalBounds,
                Transform = source.Transform,
                IsVisible = source.IsVisible,
                IsLocked = source.IsLocked,
                IncludeInOutput = source.IncludeInOutput,
                VectorPath = path,
            };
        }
        else
        {
            // Bezier nodes (ellipse) or dropped contours: derive LocalShapes from the path itself.
            replacement = VectorPathSceneFactory.FromConversion(source, path, keptShapes);
        }

        return replacement;
    }

    /// <summary>Every selected object is unlocked text, so "Převést na dráhu" has something to act on.</summary>
    public bool CanConvertSelectedTextToPath =>
        SelectedObjects.Count > 0 && SelectedObjects.All(item => item is { IsText: true, IsLocked: false });

    /// <summary>
    /// "Převést na dráhu" for text: drops the wording and type settings and keeps the contours exactly
    /// as they are drawn and burned, so the letters become ordinary curves. Position, rotation, scale,
    /// layer, visibility and stacking order carry over unchanged; only the ability to retype the text
    /// is given up, which is the point of the conversion.
    ///
    /// One gesture is one undo step: each text becomes one ReplaceObjectsCommand in place (so every
    /// object keeps its own stacking position) and all of them are wrapped in one composite.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertSelectedTextToPath))]
    private void ConvertSelectedTextToPath()
    {
        if (!CanConvertSelectedTextToPath) return;
        var sources = SelectedObjects.ToList();
        var replacements = sources.Select(source => new SceneObject
        {
            Id = source.Id,
            GroupId = source.GroupId,
            NestedGroupIds = source.NestedGroupIds.ToArray(),
            Name = source.Name,
            LocalShapes = source.LocalShapes,
            LocalPivot = source.LocalPivot,
            LocalBounds = source.LocalBounds,
            Transform = source.Transform,
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            IncludeInOutput = source.IncludeInOutput,
        }).ToList();

        var commands = sources
            .Select((source, index) => (ISceneCommand)new ReplaceObjectsCommand(Scene, [source], [replacements[index]]))
            .ToList();
        Execute(commands.Count == 1 ? commands[0] : new CompositeSceneCommand(commands));

        SelectedObjects.Clear();
        foreach (var replacement in replacements)
            SelectedObjects.Add(replacement);
        NotifySelectionStateChanged();
    }

    /// <summary>Null when "Převést na bitmapu" can run; otherwise the sentence saying why not. Vectors and
    /// text qualify, in any mix; a bitmap is already a bitmap, and locked objects are left alone.</summary>
    public string? ConvertSelectionToBitmapDisabledReason
    {
        get
        {
            if (SelectedObjects.Count == 0) return "Nic není vybráno.";
            if (SelectedObjects.Any(item => item.IsRaster)) return "Výběr už obsahuje bitmapu. Na bitmapu lze převést jen vektory a text.";
            if (SelectedObjects.Any(item => item.IsLocked)) return "Zamčené objekty nelze převést na bitmapu. Nejprve je potřeba je odemknout.";
            if (!SelectedObjects.SelectMany(item => item.LocalShapes).Any(shape => shape.Points.Count >= 2))
                return "Vybraný objekt neobsahuje žádnou dráhu, kterou by šlo převést.";
            return null;
        }
    }

    public bool CanConvertSelectionToBitmap => ConvertSelectionToBitmapDisabledReason is null;

    /// <summary>Folder that receives the PNG made by "Převést na bitmapu". It is the same app-local area
    /// background removal writes to, outside the project; saving the project packs the file into the
    /// project archive like any imported bitmap. Tests point it at a temporary folder.</summary>
    public string BitmapConversionDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "converted-bitmaps");

    [RelayCommand(CanExecute = nameof(CanConvertSelectionToBitmap))]
    private void ConvertSelectionToBitmap() => TryConvertSelectionToBitmap();

    /// <summary>
    /// "Převést na bitmapu": renders the selected vectors and text, as they are placed on the canvas, into
    /// one black-on-white PNG at the finest scan-line resolution of the layers they use (254 DPI when a
    /// layer gives none), and swaps them for one bitmap object covering the same rectangle on the bitmap
    /// layer. Fill layers become solid, line layers stay outlines (what would be burned). It is one
    /// ReplaceObjectsCommand, so undo brings the originals back (and removes a bitmap layer the conversion
    /// created) in one step; the PNG itself stays on disk, because redo needs it.
    /// Returns the new object, or null when nothing was converted.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public SceneObject? TryConvertSelectionToBitmap()
    {
        if (!CanConvertSelectionToBitmap) return null;
        var sources = SelectedObjects.ToList();

        var frame = BoundingBox2D.Empty;
        foreach (var source in sources)
        {
            var bounds = source.WorldBounds();
            frame = frame.Include(bounds.MinX, bounds.MinY).Include(bounds.MaxX, bounds.MaxY);
        }

        // Contours of one object are one compound path (outer shape plus its holes); give legacy geometry
        // a shared identity per object, exactly like grouping does, so separate objects never cancel out.
        var worldShapes = sources.SelectMany(item =>
        {
            var legacyGeometrySetId = Guid.NewGuid();
            return item.GetWorldShapes().Select(shape => shape.GeometrySetId == Guid.Empty
                ? shape with { GeometrySetId = legacyGeometrySetId }
                : shape);
        }).ToList();

        var dpi = worldShapes
            .Select(shape => FindLayer(shape.LayerId, shape.LayerColor))
            .Where(layer => layer is not null)
            .Select(layer => Lasero.Core.Raster.VectorRasterizer.DpiFromLineInterval(layer!.FillLineIntervalMm))
            .DefaultIfEmpty(Lasero.Core.Raster.VectorRasterizer.DefaultDpi)
            .Max();

        SceneObject raster;
        LayerSettings rasterLayer;
        try
        {
            var rendered = Lasero.Core.Raster.VectorRasterizer.Render(
                worldShapes, frame, dpi,
                shape => LayerModeFor(shape.LayerId, shape.LayerColor) is LayerMode.Fill or LayerMode.FillAndCut,
                Path.Combine(BitmapConversionDirectory, $"bitmap-{Guid.NewGuid():N}.png"));

            var options = new RasterImportOptions
            {
                TargetWidthMm = rendered.Frame.Width,
                TargetHeightMm = rendered.Frame.Height,
                Dpi = rendered.Dpi,
                UseThreshold = true,
                UseDithering = false,
                ThresholdValue = 128,
            };
            rasterLayer = Layers.FirstOrDefault(layer => layer.IsRaster) ?? SceneObjectFactory.CreateRasterLayer(options);
            raster = SceneObjectFactory.FromRaster(
                rendered.FilePath, options, sources.Count == 1 ? $"Bitmapa · {sources[0].Name}" : "Bitmapa · výběr", rasterLayer);

            // The picture's pixel grid rounds its height, so a hair of vertical scale (well under one scan
            // line) keeps the bitmap exactly as tall as the selection it replaces.
            var scaleY = raster.LocalBounds.Height > 0 ? rendered.Frame.Height / raster.LocalBounds.Height : 1;
            raster.Transform = new ObjectTransform(
                (rendered.Frame.MinX + rendered.Frame.MaxX) / 2 - raster.LocalPivot.X,
                (rendered.Frame.MinY + rendered.Frame.MaxY) / 2 - raster.LocalPivot.Y,
                0, 1, Math.Abs(scaleY - 1) < 1e-6 ? 1 : scaleY);
            raster.IsVisible = sources.All(item => item.IsVisible);
            raster.IncludeInOutput = sources.All(item => item.IncludeInOutput);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            VectorOperationRejected?.Invoke($"Převod na bitmapu se nepovedl. {ex.Message}");
            return null;
        }

        Execute(new ReplaceObjectsCommand(Scene, sources, [raster], [rasterLayer]));
        SelectedObjects.ReplaceWith([raster]);
        NotifySelectionStateChanged();
        return raster;
    }

    private LayerSettings? FindLayer(Guid layerId, RgbColor legacyColor) =>
        layerId != Guid.Empty
            ? Layers.FirstOrDefault(layer => layer.Id == layerId)
            : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(legacyColor));

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
        GroupId = item.GroupId,
        NestedGroupIds = item.NestedGroupIds.ToArray(),
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

    // Layer row actions share the document command stack, including the machine output order.

    private void ExecuteLayerChange(Action mutation) => Execute(new SceneLayerMutationCommand(
        Scene, mutation, () => SelectedLayer, layer => SelectedLayer = layer));

    [RelayCommand]
    private void ToggleLayerOutput(LayerSettings? layer)
    {
        if (layer is null) return;
        ExecuteLayerChange(() => layer.IsEnabled = !layer.IsEnabled);
    }

    [RelayCommand]
    private void ToggleLayerVisibility(LayerSettings? layer)
    {
        if (layer is null) return;
        ExecuteLayerChange(() => layer.IsVisible = !layer.IsVisible);
    }

    [RelayCommand]
    private void ToggleLayerLock(LayerSettings? layer)
    {
        if (layer is null) return;
        ExecuteLayerChange(() => layer.IsLocked = !layer.IsLocked);
    }

    /// <summary>Leaves only this layer in the job. The quickest way to make one test cut.</summary>
    [RelayCommand]
    private void IsolateLayerOutput(LayerSettings? layer)
    {
        if (layer is null) return;
        ExecuteLayerChange(() =>
        {
            foreach (var candidate in Layers)
                candidate.IsEnabled = ReferenceEquals(candidate, layer);
        });
    }

    /// <summary>Leaves only this layer on the canvas. Visibility never changes what is produced.</summary>
    [RelayCommand]
    private void IsolateLayerVisibility(LayerSettings? layer)
    {
        if (layer is null) return;
        ExecuteLayerChange(() =>
        {
            foreach (var candidate in Layers)
                candidate.IsVisible = ReferenceEquals(candidate, layer);
        });
    }

    [RelayCommand]
    private void SelectObjectsInLayer(LayerSettings? layer)
    {
        if (layer is null) return;
        SelectedLayer = layer;
        SelectedObjects.ReplaceWith(Objects.Where(item => UsesLayer(item, layer) && CanSelectObject(item)).ToList());
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

    /// <summary>Drag and drop in the layer list: the layer takes the given place. The list order is the
    /// order the machine runs the layers, top first.</summary>
    public void MoveLayerTo(LayerSettings layer, int newIndex)
    {
        var index = Layers.IndexOf(layer);
        newIndex = Math.Clamp(newIndex, 0, Layers.Count - 1);
        if (index < 0 || index == newIndex) return;
        ExecuteLayerChange(() => Layers.Move(index, newIndex));
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedLayerUp))]
    private void MoveSelectedLayerUp()
    {
        if (SelectedLayer is null) return;
        var index = Layers.IndexOf(SelectedLayer);
        if (index <= 0) return;
        ExecuteLayerChange(() => Layers.Move(index, index - 1));
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedLayerDown))]
    private void MoveSelectedLayerDown()
    {
        if (SelectedLayer is null) return;
        var index = Layers.IndexOf(SelectedLayer);
        if (index < 0 || index >= Layers.Count - 1) return;
        ExecuteLayerChange(() => Layers.Move(index, index + 1));
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
            IsLocked = source.IsLocked,
            IsRaster = source.IsRaster,
        };
        ExecuteLayerChange(() =>
        {
            Layers.Insert(Layers.IndexOf(source) + 1, copy);
            SelectedLayer = copy;
        });
    }

    private bool CanMergeSelectedLayerInto(LayerSettings? target) =>
        SelectedLayer is { IsRaster: false } source && target is { IsRaster: false } &&
        !ReferenceEquals(source, target) && Layers.Contains(source) && Layers.Contains(target);

    [RelayCommand(CanExecute = nameof(CanMergeSelectedLayerInto))]
    private void MergeSelectedLayerInto(LayerSettings? target) => MergeSelectedLayer(target, useSourceSettings: false);

    [RelayCommand(CanExecute = nameof(CanMergeSelectedLayerInto))]
    private void MergeSelectedLayerIntoWithSourceSettings(LayerSettings? target) =>
        MergeSelectedLayer(target, useSourceSettings: true);

    private void MergeSelectedLayer(LayerSettings? target, bool useSourceSettings)
    {
        if (!CanMergeSelectedLayerInto(target) || SelectedLayer is not { } source || target is null) return;
        ExecuteLayerChange(() =>
        {
            if (useSourceSettings)
            {
                target.Mode = source.Mode;
                target.Speed = source.Speed;
                target.Power = source.Power;
                target.Passes = source.Passes;
                target.FillLineIntervalMm = source.FillLineIntervalMm;
                target.MaterialLabel = source.MaterialLabel;
                target.IsEnabled = source.IsEnabled;
                target.IsVisible = source.IsVisible;
            }
            foreach (var item in Objects)
                item.ReplaceLayer(source.Id, source.Color, target);
            Layers.Remove(source);
            SelectedLayer = target;
        });
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
            var destination = Layers.FirstOrDefault(layer => layer.Color.IsApproximately(color));
            if (destination is not null && !destination.Color.Equals(color))
            {
                // A merely similar colour (within the shared layer-match tolerance) would silently move
                // the selection onto another layer's settings. Ask for the exact colour instead.
                VectorOperationRejected?.Invoke($"Tato barva je příliš blízká vrstvě „{destination.Name}“. Pro přesun do ní použij přesně její barvu nebo „Přiřadit do vrstvy“.");
                return;
            }
            if (destination is not null && SelectedObjects.SelectMany(item => item.LocalShapes).All(shape =>
                    shape.LayerId == destination.Id && shape.LayerColor.Equals(destination.Color)))
                return;

            Execute(new SceneLayerMutationCommand(Scene, () => ApplyColorToSelection(color, source),
                () => SelectedLayer, layer => SelectedLayer = layer));
            return;
        }

        if (source is null || source.Color.IsApproximately(color)) return;

        var occupied = Layers.FirstOrDefault(layer => layer != source && layer.Color.IsApproximately(color));
        if (occupied is not null)
        {
            VectorOperationRejected?.Invoke("Barvu už používá jiná vrstva. Použij „Sloučit do vrstvy“ a zvol, která nastavení zachovat.");
            return;
        }

        Execute(new SceneLayerMutationCommand(Scene, () =>
        {
            var sourceColor = source.Color;
            source.Color = color;
            foreach (var item in Objects)
                item.ReplaceLayer(source.Id, sourceColor, source);

            NotifyLayerStateChanged();
        }, () => SelectedLayer, layer => SelectedLayer = layer));
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
            if (previousLayer is { RemoveWhenEmpty: true } &&
                !previousLayer.Color.IsApproximately(SceneObjectFactory.RasterEngravingColor) &&
                CountObjectsUsingLayer(previousLayer) == 0)
                Layers.Remove(previousLayer);
        }

        SelectedLayer = destination;
        NotifyLayerStateChanged();
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
        Execute(new DeleteLayerCommand(Scene, SelectedLayer));
        ReconcileSelectionWithScene();
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
        EndTextFontPreview();
        FlushPendingLayerEdits();
        ApplyCommand(_commandStack.Undo);
        ReconcileSelectionWithScene();
    }
    private bool CanUndoExecute() => CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoExecute))]
    private void Redo()
    {
        EndTextFontPreview();
        FlushPendingLayerEdits();
        ApplyCommand(_commandStack.Redo);
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
        EndTextFontPreview();
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

        SelectedObjects.ReplaceWith(command.Clones);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        // The clipboard owns copy-time geometry. Keeping live scene instances here made Ctrl+V
        // reproduce later edits to the originals instead of what the operator copied.
        _clipboard = SelectedObjects.Select(SnapshotClipboardObject).ToList();
        _clipboardLayers = [];
        foreach (var shape in _clipboard.SelectMany(item => item.LocalShapes))
        {
            if (_clipboardLayers.Any(layer => shape.LayerId != Guid.Empty
                    ? layer.Id == shape.LayerId
                    : layer.Color.IsApproximately(shape.LayerColor))) continue;
            var original = shape.LayerId != Guid.Empty
                ? Layers.FirstOrDefault(layer => layer.Id == shape.LayerId)
                : Layers.FirstOrDefault(layer => layer.Color.IsApproximately(shape.LayerColor));
            _clipboardLayers.Add(original is null
                ? new LayerSettings
                {
                    Id = shape.LayerId == Guid.Empty ? Guid.NewGuid() : shape.LayerId,
                    Name = shape.LayerColor.ToHex(),
                    Color = shape.LayerColor,
                    Mode = shape.PreferredMode,
                }
                : SnapshotClipboardLayer(original));
        }
        PasteCommand.NotifyCanExecuteChanged();
    }

    private static LayerSettings SnapshotClipboardLayer(LayerSettings layer) => new()
    {
        Id = layer.Id,
        RemoveWhenEmpty = layer.RemoveWhenEmpty,
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
        IsLocked = layer.IsLocked,
        IsRaster = layer.IsRaster,
    };

    private static SceneObject SnapshotClipboardObject(SceneObject source) => new()
    {
        // Keep source identities inside the detached snapshot so DuplicateObjectsCommand can
        // remap a copied linked offset to its copied sources when both are selected.
        Id = source.Id,
        Name = source.Name,
        LocalShapes = source.LocalShapes.Select(shape => shape with { Points = shape.Points.ToArray() }).ToArray(),
        LocalPivot = source.LocalPivot,
        LocalBounds = source.LocalBounds,
        RasterFilePath = source.RasterFilePath,
        RasterOptions = source.RasterOptions,
        OriginalRasterFilePath = source.OriginalRasterFilePath,
        Text = source.Text,
        VectorPath = source.VectorPath is { } path
            ? path with
            {
                Subpaths = path.Subpaths.Select(subpath => subpath with
                {
                    Nodes = subpath.Nodes.ToArray(),
                }).ToArray(),
            }
            : null,
        OffsetLink = source.OffsetLink is { } link
            ? link with { SourceIds = link.SourceIds.ToArray() }
            : null,
        Transform = source.Transform,
        IsVisible = source.IsVisible,
        IsLocked = source.IsLocked,
        IncludeInOutput = source.IncludeInOutput,
        GroupId = source.GroupId,
        NestedGroupIds = source.NestedGroupIds.ToArray(),
    };

    private bool HasClipboard() => _clipboard.Count > 0;

    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private void Paste()
    {
        // Resolve copied layer identities against this document before inserting objects. This
        // also makes cross-project paste safe when the original project has since been replaced.
        var sources = _clipboard.Select(SnapshotClipboardObject).ToList();
        var copiedLayers = _clipboardLayers.Select(SnapshotClipboardLayer).ToList();
        var reconcileLayers = new SceneLayerMutationCommand(Scene,
            () => Scene.EnsureLayers(copiedLayers, sources));
        var command = new DuplicateObjectsCommand(Scene, sources, new Position(CascadeOffsetMm, -CascadeOffsetMm, 0));
        Execute(new CompositeSceneCommand([reconcileLayers, command]));

        SelectedObjects.ReplaceWith(command.Clones);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        Copy();
        Delete();
    }

    private bool CanSelectAll() => Objects.Any(CanSelectObject);

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        SelectedObjects.ReplaceWith(Objects.Where(CanSelectObject).ToList());
    }

    [RelayCommand(CanExecute = nameof(CanSelectActiveLayer))]
    private void SelectAllInActiveLayer()
    {
        if (SelectedLayer is null) return;
        SelectedObjects.ReplaceWith(Objects.Where(item => UsesLayer(item, SelectedLayer) && CanSelectObject(item)).ToList());
    }

    private bool CanSelectActiveLayer() => SelectedLayer is { IsVisible: true, IsLocked: false };

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

    /// <summary>Raised by CenterOnWorkAreaCommand. The work-area size lives in the machine settings, so the
    /// shell answers with CenterSelectionAt(width / 2, height / 2).</summary>
    public event Action? CenterOnWorkAreaRequested;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterOnWorkArea() => CenterOnWorkAreaRequested?.Invoke();

    /// <summary>Moves the whole selection, as one group, so its common centre lands on the given point.
    /// One undo step; locked objects stay where they are.</summary>
    public void CenterSelectionAt(double centerX, double centerY) => Align(objects =>
    {
        var movable = objects.Where(o => !o.IsLocked).ToList();
        if (movable.Count == 0) return [];
        var cx = (movable.Min(o => o.WorldBounds().MinX) + movable.Max(o => o.WorldBounds().MaxX)) / 2;
        var cy = (movable.Min(o => o.WorldBounds().MinY) + movable.Max(o => o.WorldBounds().MaxY)) / 2;
        return movable.Select(o => Move(o, dx: centerX - cx, dy: centerY - cy));
    });

    /// <summary>Center both: the common centre of the selection on both axes in one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanAlign))]
    private void AlignCenterBoth() => Align(objects =>
    {
        var targetX = (objects.Min(o => o.WorldBounds().MinX) + objects.Max(o => o.WorldBounds().MaxX)) / 2;
        var targetY = (objects.Min(o => o.WorldBounds().MinY) + objects.Max(o => o.WorldBounds().MaxY)) / 2;
        return objects.Select(o => Move(o,
            dx: targetX - (o.WorldBounds().MinX + o.WorldBounds().MaxX) / 2,
            dy: targetY - (o.WorldBounds().MinY + o.WorldBounds().MaxY) / 2));
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
        var objects = IndependentTransformTargets(SelectedObjects).ToList();
        if (objects.Count == 0) return;
        var commands = plan(objects).Cast<ISceneCommand>().ToList();
        if (commands.Count > 0) Execute(new CompositeSceneCommand(commands));
    }

    /// <summary>The part of a selection that may be transformed directly: a linked-offset output whose
    /// source is selected too is left out, because it is regenerated from that source after the
    /// command (transforming both would move it twice). Shared by align, nudge, flip and rotate callers.</summary>
    public static IReadOnlyList<SceneObject> IndependentTransformTargets(IEnumerable<SceneObject> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var items = selection.ToList();
        var ids = items.Select(item => item.Id).ToHashSet();
        return items.Where(item => item.OffsetLink is not { } link ||
            !link.SourceIds.Any(ids.Contains)).ToList();
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
        if (!CanGroupSelection) return;
        // A group is membership, not a geometry operation. Keep the children and their source paths.
        var members = ExpandedGroupSelection();
        Execute(new SetObjectGroupCommand(members, Guid.NewGuid()));
        SelectedObjects.ReplaceWith(members.Where(CanSelectObject));
    }

    private IReadOnlyList<SceneObject> ExpandedGroupSelection()
    {
        var selected = SelectedObjects.ToHashSet();
        var groupIds = SelectedObjects.Select(item => item.GroupId).OfType<Guid>().ToHashSet();
        return Objects.Where(item => selected.Contains(item) ||
            item.GroupId is { } id && groupIds.Contains(id)).ToList();
    }

    [RelayCommand(CanExecute = nameof(CanUngroupSelection))]
    private void UngroupSelection()
    {
        var groupIds = SelectedObjects.Select(item => item.GroupId).OfType<Guid>()
            .Distinct().Where(id => Objects.Where(item => item.GroupId == id)
                .All(item => !item.IsLocked)).ToHashSet();
        if (groupIds.Count == 0) return;
        Execute(new SetObjectGroupCommand(Objects.Where(item => item.GroupId is { } id && groupIds.Contains(id)), null));
    }

    /// <summary>How a boolean result picks its layer when the operands sit on different ones.</summary>
    private enum BooleanLayerRule
    {
        /// <summary>Single shared layer: the topmost operand's (Unite, Fill between paths).</summary>
        Topmost,
        /// <summary>Every region takes the layer of the operand it comes from.</summary>
        PerRegion,
    }

    private void PreviewBooleanChange(string title, IReadOnlyList<SceneObject> sources,
        IReadOnlyList<SceneObject> results, bool approximatesCurves = false,
        BooleanLayerRule layerRule = BooleanLayerRule.Topmost)
    {
        // Sources that all belong to one group produce results that stay in that group.
        if (sources.Count > 0 && sources[0].GroupId is { } sharedGroup
            && sources.All(item => item.GroupId == sharedGroup))
        {
            foreach (var result in results)
            {
                result.GroupId = sharedGroup;
                result.NestedGroupIds = sources[0].NestedGroupIds.ToArray();
            }
        }

        var resultLayers = results.SelectMany(item => item.LocalShapes)
            .Select(shape => Layers.FirstOrDefault(layer => layer.Id == shape.LayerId)?.Name ?? "Původní vrstva")
            .Distinct().ToList();
        var resultLayer = resultLayers.Count == 0 ? "Bez vrstvy" : string.Join(", ", resultLayers);
        void Commit()
        {
            if (sources.Any(item => !Objects.Contains(item))) return;
            Execute(new ReplaceObjectsCommand(Scene, sources, results));
            SelectedObjects.ReplaceWith(results);
        }
        var orderedSources = sources.OrderBy(item => Objects.IndexOf(item)).ToList();
        var geometryNotice = sources.Any(item => item.VectorPath is null &&
            item.LocalShapes.Any(shape => shape.IsClosed && shape.Points.Count > 8))
            ? "Část vstupů už nemá Bézierovy uzly. Pro tyto obrysy se použijí jejich uložené úsečky."
            : null;
        var sourceShapes = sources.SelectMany(item => item.LocalShapes).Where(shape => shape.IsClosed).ToList();
        var mixedLayers = sourceShapes.Count > 0 && sourceShapes.Any(shape => !SameLayer(shape, sourceShapes[0]));
        var layerNotice = !mixedLayers ? null : layerRule == BooleanLayerRule.PerRegion
            ? $"Vstupy jsou na různých vrstvách. Každá část převezme vrstvu tvaru, ze kterého vznikla (průnik vrstvu horního tvaru): {resultLayer}."
            : $"Vstupy jsou na různých vrstvách. Výsledek převezme vrstvu horního tvaru: {resultLayer}.";
        var preview = new VectorOperationPreview(title, orderedSources, results, resultLayer,
            approximatesCurves, Commit, geometryNotice, layerNotice);
        if (BooleanPreviewRequested is { } showPreview) showPreview(preview);
        else Commit();
    }

    private IReadOnlyList<IReadOnlyList<SceneObject>> BooleanOperands() => SelectedObjects
        .GroupBy(item => item.GroupId ?? item.Id)
        .Select(group => (IReadOnlyList<SceneObject>)group.OrderBy(item => Objects.IndexOf(item)).ToList())
        .OrderBy(group => group.Max(item => Objects.IndexOf(item)))
        .ToList();

    private static bool HasCurveArea(VectorPath path) => path.Subpaths.Any(subpath =>
        subpath.IsClosed && subpath.Nodes.Count >= 2 &&
        Math.Abs(SignedArea(subpath.Flatten())) > MinimumUnitedAreaSquareMm);

    /// <summary>One boolean operand (an ungrouped object or a whole group), back-to-front in z-order,
    /// with the layer metadata its result regions inherit.</summary>
    private sealed record BooleanOperandInfo(VectorPath Path, ImportedShape Shape);

    private static List<BooleanOperandInfo> BuildOperandInfos(IReadOnlyList<IReadOnlyList<SceneObject>> operands)
    {
        var infos = new List<BooleanOperandInfo>(operands.Count);
        foreach (var operand in operands)
        {
            var shape = operand[^1].GetWorldShapes().FirstOrDefault(item => item.IsClosed)
                ?? operand.SelectMany(item => item.GetWorldShapes()).FirstOrDefault();
            if (shape is null) continue;
            infos.Add(new BooleanOperandInfo(CurveBooleanService.FromObjects(operand), shape));
        }
        return infos;
    }

    private static bool SameLayer(ImportedShape first, ImportedShape second) =>
        first.LayerId == second.LayerId && first.LayerColor == second.LayerColor;

    /// <summary>Which operand a result region belongs to: the one it overlaps most (A\B lies wholly in
    /// A, B\A in B), and on a tie, so for A∩B, the topmost operand. Null when every operand is on the
    /// same layer, so the caller keeps the single shared target and pays for no extra boolean work.</summary>
    private static Func<VectorPath, ImportedShape>? RegionTargetResolver(IReadOnlyList<BooleanOperandInfo> infos)
    {
        if (infos.Count < 2 || infos.All(info => SameLayer(info.Shape, infos[0].Shape))) return null;
        return region =>
        {
            var best = infos[^1];
            var bestArea = -1.0;
            // Topmost first, so an equal overlap keeps the topmost operand.
            for (var index = infos.Count - 1; index >= 0; index--)
            {
                var area = OverlapArea(region, infos[index].Path);
                if (area > bestArea * (1 + 1e-6) + 1e-9)
                {
                    best = infos[index];
                    bestArea = area;
                }
            }
            return best.Shape;
        };
    }

    private static double OverlapArea(VectorPath region, VectorPath operand)
    {
        try
        {
            var common = CurveBooleanService.Apply(region, operand, CurveBooleanService.Operation.Intersection);
            double total = 0;
            foreach (var piece in CurveBooleanService.SplitRegions(common))
            {
                // SplitRegions lists the outer contour first, then its holes.
                var areas = piece.Subpaths.Select(subpath => Math.Abs(SignedArea(subpath.Flatten()))).ToList();
                total += areas[0] - areas.Skip(1).Sum();
            }
            return total;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<SceneObject> CreateCurveResults(VectorPath path,
        ImportedShape fallbackTarget, string name, bool visible, bool includeInOutput,
        Func<VectorPath, ImportedShape>? targetFor = null)
    {
        var results = new List<SceneObject>();
        foreach (var component in CurveBooleanService.SplitRegions(path))
        {
            var target = targetFor?.Invoke(component) ?? fallbackTarget;
            var item = VectorPathSceneFactory.Create(component, target.LayerColor, name);
            item.LocalShapes = item.LocalShapes.Select(shape => shape with
            {
                LayerId = target.LayerId,
                PreferredMode = target.PreferredMode,
            }).ToList();
            item.IsVisible = visible;
            item.IncludeInOutput = includeInOutput;
            results.Add(item);
        }
        return results;
    }

    private static IReadOnlyList<IReadOnlyList<Position>>? BuildOperandRings(
        IReadOnlyList<SceneObject> members)
    {
        IReadOnlyList<IReadOnlyList<Position>>? combined = null;
        foreach (var member in members)
        {
            var rings = BuildSourceRings(member);
            if (rings is null) return null;
            combined = combined is null ? rings :
                BooleanService.Union(combined, rings, VectorFillRule.EvenOdd);
        }
        return combined;
    }

    /// <summary>Turns one compound object into independent contour objects. Unlike Ungroup this
    /// intentionally changes the contour structure; editable subpaths and layer metadata are kept.</summary>
    [RelayCommand(CanExecute = nameof(CanBreakApartSelection))]
    private void BreakApartSelection()
    {
        if (!CanBreakApartSelection || Selected is not { } source) return;
        var worldPath = source.GetWorldVectorPath();
        if (worldPath is not null && worldPath.Subpaths.Count != source.LocalShapes.Count)
        {
            RejectVectorUnion("Obrysy a uzly cesty si neodpovídají. Rozdělení by poškodilo křivku.");
            return;
        }
        var parts = source.GetWorldShapes().Select((shape, index) =>
        {
            var part = CreateVectorObject(
                [shape with { GeometrySetId = Guid.NewGuid() }],
                $"{source.Name} · obrys {index + 1}",
                source.IsVisible, source.IncludeInOutput,
                worldPath is null ? null : new VectorPath { Subpaths = [worldPath.Subpaths[index]] });
            part.GroupId = source.GroupId;
            part.NestedGroupIds = source.NestedGroupIds.ToArray();
            return part;
        }).ToList();
        Execute(new ReplaceObjectsCommand(Scene, [source], parts));
        SelectedObjects.ReplaceWith(parts);
    }

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void UniteSelection()
    {
        var operands = BooleanOperands();
        var sources = operands.SelectMany(group => group).ToList();
        if (sources.Count == 0) return;

        var sourceShapes = sources.SelectMany(item => item.GetWorldShapes()).ToList();
        var targetShape = FindUnionTargetShape(sources, sourceShapes);

        try
        {
            VectorPath? united = null;
            foreach (var operand in operands)
            {
                var path = CurveBooleanService.FromObjects(operand);
                if (!HasCurveArea(path)) continue;
                united = united is null ? path :
                    CurveBooleanService.Apply(united, path, CurveBooleanService.Operation.Union);
            }

            if (united is null || !HasCurveArea(united))
            {
                RejectVectorUnion("Vybrané křivky nevytvářejí platnou uzavřenou plochu.");
                return;
            }

            var results = CreateCurveResults(united, targetShape, "Sjednocený vektor",
                sources.Any(item => item.IsVisible), sources.Any(item => item.IncludeInOutput));
            if (results.Count == 0 || !IsSafeUnionResult(sourceShapes,
                    results.SelectMany(item => item.GetWorldShapes()).ToList()))
            {
                RejectVectorUnion("Výsledná geometrie by byla prázdná nebo poškozená.");
                return;
            }
            PreviewBooleanChange("Sjednotit", sources, results, layerRule: BooleanLayerRule.Topmost);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion("Křivky obsahují neplatné nebo vzájemně se křížící body.");
        }
    }

    /// <summary>Subtract, intersect and exclude (XOR) share the pairwise gate and the curve-aware
    /// result path. Every disconnected filled island becomes its own editable scene object.
    ///
    /// Source order matters for Subtract (it does not, mathematically, for Intersect/Xor, but folding
    /// left-to-right is deterministic either way): sources are processed back-to-front by scene
    /// z-order, so Subtract reads as "shapes in front cut a hole in the shape behind them" — Minus
    /// Front in Illustrator, Subtract in Figma — rather than an order the user cannot predict from
    /// selection order alone.</summary>
    private void CombineSelection(CurveBooleanService.Operation operation, string resultName)
    {
        var operands = BooleanOperands();
        var sources = operands.SelectMany(group => group).ToList();
        if (sources.Count == 0) return;

        var sourceShapes = sources.SelectMany(item => item.GetWorldShapes()).ToList();
        var targetShape = FindUnionTargetShape(sources, sourceShapes);

        try
        {
            VectorPath? combined = null;
            foreach (var operand in operands)
            {
                var sourcePath = CurveBooleanService.FromObjects(operand);
                if (!HasCurveArea(sourcePath)) continue;
                combined = combined is null
                    ? sourcePath
                    : CurveBooleanService.Apply(combined, sourcePath, operation);
            }

            if (combined is null || !HasCurveArea(combined))
            {
                RejectVectorUnion("Výsledkem operace by byla prázdná plocha.");
                return;
            }

            // Each region takes the layer of the operand it comes from (A\B -> A, B\A -> B,
            // A∩B -> topmost) instead of the whole result landing on the topmost layer.
            var results = CreateCurveResults(combined, targetShape, resultName,
                sources.Any(item => item.IsVisible), sources.Any(item => item.IncludeInOutput),
                RegionTargetResolver(BuildOperandInfos(operands)));
            if (results.Count == 0 || results.Any(item => !IsSafeBooleanResult(sourceShapes, item.GetWorldShapes())))
            {
                RejectVectorUnion("Výsledná geometrie by byla prázdná nebo poškozená.");
                return;
            }

            PreviewBooleanChange(resultName, sources, results, layerRule: BooleanLayerRule.PerRegion);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion("Křivky obsahují neplatné nebo vzájemně se křížící body.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanBooleanPairSelection))]
    private void SubtractSelection() => CombineSelection(CurveBooleanService.Operation.Difference, "Odečtený vektor");

    [RelayCommand(CanExecute = nameof(CanBooleanPairSelection))]
    private void IntersectSelection() => CombineSelection(CurveBooleanService.Operation.Intersection, "Průnik vektorů");

    [RelayCommand(CanExecute = nameof(CanBooleanPairSelection))]
    private void ExcludeSelection() => CombineSelection(CurveBooleanService.Operation.Xor, "Vyloučený vektor");

    /// <summary>Partitions exactly two closed operands into A\B, A∩B, and B\A. Each non-empty
    /// region becomes its own editable object. Z-order defines A (back) and B (front), matching
    /// subtraction. All pieces are committed as one undoable replacement.</summary>
    [RelayCommand(CanExecute = nameof(CanDivideSelection))]
    private void DivideSelection() => PartitionSelectedShapes("Rozdělený vektor");

    /// <summary>Splits every selected closed shape at all other selected boundaries. Each resulting
    /// non-overlapping region is emitted as its own object; unlike Divide this accepts any number of
    /// operands. The topmost shape owns newly uncovered regions, while intersections keep the
    /// metadata of the region that existed first in the z-order. Each independent filled island
    /// becomes an object, with its holes kept in the same compound path.</summary>
    [RelayCommand(CanExecute = nameof(CanFractureSelection))]
    private void FractureSelection()
    {
        var operands = BooleanOperands();
        var sources = operands.SelectMany(group => group).ToList();
        if (operands.Count < 2) return;
        try
        {
            var partitions = new List<(VectorPath Path, SceneObject Owner)>();
            VectorPath? covered = null;
            foreach (var operand in operands)
            {
                var source = operand[^1];
                var sourcePath = CurveBooleanService.FromObjects(operand);
                if (!HasCurveArea(sourcePath)) return;

                var next = new List<(VectorPath Path, SceneObject Owner)>();
                foreach (var partition in partitions)
                {
                    var outside = CurveBooleanService.Apply(partition.Path, sourcePath,
                        CurveBooleanService.Operation.Difference);
                    var overlap = CurveBooleanService.Apply(partition.Path, sourcePath,
                        CurveBooleanService.Operation.Intersection);
                    if (HasCurveArea(outside)) next.Add((outside, partition.Owner));
                    if (HasCurveArea(overlap)) next.Add((overlap, partition.Owner));
                }

                var uncovered = covered is null ? sourcePath : CurveBooleanService.Apply(sourcePath,
                    covered, CurveBooleanService.Operation.Difference);
                if (HasCurveArea(uncovered)) next.Add((uncovered, source));
                covered = covered is null ? sourcePath : CurveBooleanService.Apply(covered,
                    sourcePath, CurveBooleanService.Operation.Union);
                partitions = next;
            }

            var pieces = new List<SceneObject>();
            foreach (var partition in partitions)
            {
                var representative = partition.Owner.GetWorldShapes().FirstOrDefault(shape => shape.IsClosed);
                if (representative is null) continue;
                var components = CreateCurveResults(partition.Path, representative, "Fracture díl",
                    partition.Owner.IsVisible, partition.Owner.IncludeInOutput);
                if (components.Any(item => !IsSafeBooleanResult(
                    sources.SelectMany(source => source.GetWorldShapes()).ToList(), item.GetWorldShapes())))
                    throw new InvalidOperationException("Výsledná oblast není platná uzavřená plocha.");
                pieces.AddRange(components);
            }

            if (pieces.Count == 0)
            {
                RejectVectorUnion("Vybrané tvary nevytvářejí žádné oblasti pro Fracture.");
                return;
            }
            PreviewBooleanChange("Fracture", sources, pieces, layerRule: BooleanLayerRule.PerRegion);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion($"Fracture se nepodařilo bezpečně vytvořit. {exception.Message}");
        }
    }

    private void PartitionSelectedShapes(string resultName)
    {
        var operands = BooleanOperands();
        if (operands.Count != 2) return;
        var sources = operands.SelectMany(group => group).ToList();
        try
        {
            var a = CurveBooleanService.FromObjects(operands[0]);
            var b = CurveBooleanService.FromObjects(operands[1]);
            if (!HasCurveArea(a) || !HasCurveArea(b)) return;

            var regions = new[]
            {
                (Path: CurveBooleanService.Apply(a, b, CurveBooleanService.Operation.Difference), Source: operands[0][^1]),
                (Path: CurveBooleanService.Apply(a, b, CurveBooleanService.Operation.Intersection), Source: operands[1][^1]),
                (Path: CurveBooleanService.Apply(b, a, CurveBooleanService.Operation.Difference), Source: operands[1][^1]),
            };
            var pieces = new List<SceneObject>();
            foreach (var (path, source) in regions)
            {
                if (!HasCurveArea(path)) continue;
                var representative = source.GetWorldShapes().FirstOrDefault(shape => shape.IsClosed);
                if (representative is null) continue;
                var components = CreateCurveResults(path, representative, resultName,
                    source.IsVisible, source.IncludeInOutput);
                if (components.Any(item => !IsSafeBooleanResult(
                    sources.SelectMany(item => item.GetWorldShapes()).ToList(), item.GetWorldShapes())))
                    throw new InvalidOperationException("Výsledná část není platná uzavřená plocha.");
                pieces.AddRange(components);
            }

            if (pieces.Count == 0)
            {
                RejectVectorUnion("Vybrané tvary nevytvářejí žádné rozdělené plochy.");
                return;
            }
            PreviewBooleanChange("Rozdělit překryv", sources, pieces, layerRule: BooleanLayerRule.PerRegion);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion($"Tvary nešlo bezpečně rozdělit. {exception.Message}");
        }
    }

    /// <summary>Joins two open editable curves into one filled path without sampling away handles.</summary>
    [RelayCommand(CanExecute = nameof(CanFillBetweenPaths))]
    private void FillBetweenPaths()
    {
        var sources = SelectedObjects.OrderBy(item => Scene.Objects.IndexOf(item)).ToList();
        if (sources.Count != 2 || sources.Any(item => item.GetWorldVectorPath() is not { Subpaths.Count: 1 })) return;

        try
        {
            var first = sources[0].GetWorldVectorPath()!.Subpaths[0];
            var second = sources[1].GetWorldVectorPath()!.Subpaths[0];
            if (first.IsClosed || second.IsClosed || first.Nodes.Count < 2 || second.Nodes.Count < 2)
            {
                RejectVectorUnion("Cesty jsou příliš krátké nebo se překrývají. Výplň nelze vytvořit.");
                return;
            }
            var reversed = second.Nodes.Reverse().Select(node => node with
            {
                HandleIn = node.HandleOut,
                HandleOut = node.HandleIn,
            }).ToList();
            var nodes = first.Nodes.ToList();
            nodes[0] = nodes[0] with { HandleIn = null };
            nodes[^1] = nodes[^1] with { HandleOut = null };
            reversed[0] = reversed[0] with { HandleIn = null };
            reversed[^1] = reversed[^1] with { HandleOut = null };
            nodes.AddRange(reversed);
            var joined = new VectorPath { Subpaths = [new VectorSubpath { Nodes = nodes, IsClosed = true }] };
            var regions = CurveBooleanService.Resolve(joined);
            if (!HasCurveArea(regions))
            {
                RejectVectorUnion("Mezi vybranými cestami nevznikla vyplnitelná plocha.");
                return;
            }

            var target = FindUnionTargetShape(sources, sources.SelectMany(item => item.GetWorldShapes()).ToList());
            var results = CreateCurveResults(regions, target, "Výplň mezi cestami",
                sources.Any(item => item.IsVisible), sources.Any(item => item.IncludeInOutput));
            if (results.Count == 0) return;
            PreviewBooleanChange("Výplň mezi cestami", sources, results);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            RejectVectorUnion($"Výplň mezi cestami se nepodařilo vytvořit. {exception.Message}");
        }
    }

    private static bool SameVectorPoint(Position first, Position second) =>
        Math.Abs(first.X - second.X) < 1e-9 && Math.Abs(first.Y - second.Y) < 1e-9;

    // Weld accepts any number of closed operands; Boolean Union stays pairwise like LightBurn.
    // Both use the same geometry implementation and one undo transaction.
    [RelayCommand(CanExecute = nameof(CanWeldSelection))]
    private void WeldSelection()
    {
        if (UniteSelectionDisabledReason is null) UniteSelection();
        else WeldOpenEndpoints();
    }

    /// <summary>Largest gap between two open endpoints that Weld still treats as touching.</summary>
    public const double WeldEndpointToleranceMm = 0.05;

    /// <summary>Two or more unlocked editable open paths, each a single contour, so Weld can join
    /// the ends that meet.</summary>
    public bool CanWeldOpenPaths => SelectedObjects.Count >= 2 && SelectedObjects.All(item =>
        CanSelectObject(item) && !item.IsRaster && item.OffsetLink is null &&
        item.VectorPath is { Subpaths.Count: 1 } path && !path.Subpaths[0].IsClosed &&
        path.Subpaths[0].Nodes.Count >= 2);

    /// <summary>Weld for open paths: joins endpoints that coincide within
    /// <see cref="WeldEndpointToleranceMm"/> (across objects, and an end meeting its own start closes
    /// the contour) using the same <see cref="VectorPathEditor.JoinAtEndpoints"/> as the endpoint-drag
    /// join. Objects that touch nothing are left alone; everything is one undo step.</summary>
    private void WeldOpenEndpoints()
    {
        if (!CanWeldOpenPaths) return;
        var chains = SelectedObjects.OrderBy(item => Objects.IndexOf(item)).Select(item =>
            (Path: item.GetWorldVectorPath()!.Subpaths[0], Members: new List<SceneObject> { item })).ToList();

        static Position EndOf(VectorSubpath path, bool atStart) => atStart ? path.Nodes[0].Anchor : path.Nodes[^1].Anchor;
        static bool Near(Position a, Position b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) <= WeldEndpointToleranceMm;

        var changed = false;
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < chains.Count && !merged; i++)
            {
                if (chains[i].Path.IsClosed) continue;
                for (var j = i + 1; j < chains.Count && !merged; j++)
                {
                    if (chains[j].Path.IsClosed) continue;
                    foreach (var iStart in new[] { false, true })
                    {
                        foreach (var jStart in new[] { true, false })
                        {
                            if (!Near(EndOf(chains[i].Path, iStart), EndOf(chains[j].Path, jStart))) continue;
                            var joined = VectorPathEditor.JoinAtEndpoints(chains[i].Path, iStart, chains[j].Path, jStart);
                            chains[i].Members.AddRange(chains[j].Members);
                            chains[i] = (joined, chains[i].Members);
                            chains.RemoveAt(j);
                            merged = changed = true;
                            break;
                        }
                        if (merged) break;
                    }
                }
                if (merged) break;
                var own = chains[i].Path;
                if (own.Nodes.Count >= 3 && Near(own.Nodes[0].Anchor, own.Nodes[^1].Anchor))
                {
                    var nodes = own.Nodes.ToList();
                    nodes[0] = nodes[0] with { HandleIn = nodes[^1].HandleIn };
                    nodes.RemoveAt(nodes.Count - 1);
                    chains[i] = (new VectorSubpath { Nodes = nodes, IsClosed = true }, chains[i].Members);
                    merged = changed = true;
                }
            }
        }

        if (!changed)
        {
            VectorOperationRejected?.Invoke(
                $"Žádné koncové body nejsou blíž než {WeldEndpointToleranceMm:0.##} mm. Cesty nebylo možné svařit.");
            return;
        }

        var replaced = new List<SceneObject>();
        var results = new List<SceneObject>();
        foreach (var (path, members) in chains)
        {
            var closedBySelf = path.IsClosed;
            if (members.Count == 1 && !closedBySelf) continue;
            var owner = members[0];
            var representative = owner.LocalShapes.FirstOrDefault();
            var result = VectorPathSceneFactory.Create(new VectorPath { Subpaths = [path] },
                representative?.LayerColor ?? RgbColor.Black, owner.Name);
            if (representative is not null)
            {
                result.LocalShapes = result.LocalShapes.Select(shape => shape with
                {
                    LayerId = representative.LayerId,
                    PreferredMode = representative.PreferredMode,
                    GeometrySetId = representative.GeometrySetId == Guid.Empty ? Guid.NewGuid() : representative.GeometrySetId,
                }).ToList();
            }
            result.IsVisible = members.Any(item => item.IsVisible);
            result.IncludeInOutput = members.Any(item => item.IncludeInOutput);
            result.GroupId = members.Select(item => item.GroupId).FirstOrDefault(id => id is not null);
            result.NestedGroupIds = members.First(item => item.GroupId == result.GroupId).NestedGroupIds.ToArray();
            replaced.AddRange(members);
            results.Add(result);
        }

        Execute(new ReplaceObjectsCommand(Scene, replaced, results));
        SelectedObjects.ReplaceWith(results);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedVectorPaths))]
    private void FlattenSelectedVectorPaths() => RewriteSelectedVectorPaths(simplify: false);

    [RelayCommand(CanExecute = nameof(CanEditSelectedVectorPaths))]
    private void SimplifySelectedVectorPaths() => RewriteSelectedVectorPaths(simplify: true);

    /// <summary>Converts curves into editable straight segments, optionally removing points within
    /// 0.05 mm of a straight chord. Existing transforms, layer/output metadata, and object identity
    /// are retained through one undoable replacement per selected path.</summary>
    private void RewriteSelectedVectorPaths(bool simplify)
    {
        const double toleranceMm = VectorPath.DefaultFlattenToleranceMm;
        var replacements = new List<ISceneCommand>();
        var updated = new List<SceneObject>();
        try
        {
            foreach (var source in SelectedObjects.ToList())
            {
                if (source.VectorPath is not { } path) return;
                var subpaths = new List<VectorSubpath>(path.Subpaths.Count);
                foreach (var subpath in path.Subpaths)
                {
                    var minimumNodes = subpath.IsClosed ? 3 : 2;
                    if (subpath.Nodes.Count < minimumNodes)
                        throw new InvalidOperationException("Cesta nemá dostatek uzlů.");
                    if (simplify && subpath.Nodes.Any(node => node.HasAnyHandle))
                    {
                        // Curves stay curves: refit the finely flattened contour with fewer Bezier
                        // nodes. If the refit would not remove anything, the original is kept as is.
                        subpaths.Add(RefitCurvedSubpath(subpath, toleranceMm));
                        continue;
                    }
                    var points = subpath.Flatten(toleranceMm).ToList();
                    if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                        throw new InvalidOperationException("Cesta obsahuje neplatné souřadnice.");
                    if (subpath.IsClosed && points.Count > 1 && SamePoint(points[0], points[^1]))
                        points.RemoveAt(points.Count - 1);
                    var reduced = subpath.IsClosed
                        ? RingSimplifier.SimplifyClosed(points, simplify ? toleranceMm : 0)
                        : RingSimplifier.SimplifyOpen(points, simplify ? toleranceMm : 0);
                    var minimum = subpath.IsClosed ? 3 : 2;
                    if (reduced.Count < minimum)
                        throw new InvalidOperationException("Zjednodušení by odstranilo celou cestu.");
                    subpaths.Add(new VectorSubpath
                    {
                        Nodes = reduced.Select(VectorNode.CornerAt).ToList(),
                        IsClosed = subpath.IsClosed,
                    });
                }

                var replacement = VectorPathSceneFactory.Rebuild(source, new VectorPath { Subpaths = subpaths });
                replacements.Add(new ReplaceObjectsCommand(Scene, [source], [replacement]));
                updated.Add(replacement);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            VectorOperationRejected?.Invoke($"Cestu se nepodařilo {(simplify ? "zjednodušit" : "zploštit")}. {exception.Message}");
            return;
        }

        if (replacements.Count == 0) return;
        Execute(new CompositeSceneCommand(replacements));
        SelectedObjects.ReplaceWith(updated);
    }

    /// <summary>Curve-preserving simplify: flattens at a quarter of the tolerance, refits with
    /// BezierFitter and keeps the result only when it has fewer nodes than the source.</summary>
    internal static VectorSubpath RefitCurvedSubpath(VectorSubpath subpath, double toleranceMm)
    {
        var points = subpath.Flatten(toleranceMm / 4).ToList();
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new InvalidOperationException("Cesta obsahuje neplatné souřadnice.");
        if (subpath.IsClosed && points.Count > 1 && SamePoint(points[0], points[^1]))
            points.RemoveAt(points.Count - 1);

        var options = new Lasero.Core.Trace.BezierFitOptions
        {
            ToleranceMm = toleranceMm,
            CornerAngleDegrees = 35,
            DedupeDistanceMm = toleranceMm / 10,
        };
        var fitted = subpath.IsClosed
            ? Lasero.Core.Trace.BezierFitter.FitClosed(points, options)
            : Lasero.Core.Trace.BezierFitter.FitOpen(points, options);
        var minimum = subpath.IsClosed ? 3 : 2;
        return fitted.Nodes.Count >= minimum && fitted.Nodes.Count < subpath.Nodes.Count
            ? fitted
            : subpath;
    }

    private static bool SamePoint(Position first, Position second) =>
        Math.Abs(first.X - second.X) < 1e-9 && Math.Abs(first.Y - second.Y) < 1e-9;

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
        OffsetRequested?.Invoke(SelectedObjects.ToList(), null);
    }

    [RelayCommand(CanExecute = nameof(CanOffsetSelection))]
    private void ShrinkSelection() => OffsetRequested?.Invoke(SelectedObjects.ToList(), VectorOffsetDirection.Inward);

    [RelayCommand(CanExecute = nameof(CanOffsetSelection))]
    private void ExpandSelection() => OffsetRequested?.Invoke(SelectedObjects.ToList(), VectorOffsetDirection.Outward);

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
        // Locked, hidden, layer-locked/hidden objects and derived offset outlines are not editable
        // join sources or targets: joining would silently rewrite geometry the operator cannot touch.
        if (!CanSelectObject(dragged) || !CanSelectObject(target)
            || dragged.OffsetLink is not null || target.OffsetLink is not null)
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
        // Keep editor-group membership: the dragged object's own group, else the one it joined into.
        var membership = dragged.GroupId is not null ? dragged : target;
        result.GroupId = membership.GroupId;
        result.NestedGroupIds = membership.NestedGroupIds.ToArray();

        Execute(new ReplaceObjectsCommand(Scene, [dragged, target], [result]));
        SelectedObjects.Clear();
        SelectedObjects.Add(result);
        return true;
    }

    /// <param name="keepOriginal">True (the default): every source stays exactly as it was and its offset
    /// is inserted as a new object directly above it. False: each source is replaced by its offset. Both
    /// are one undo step. A locked source is only ever kept — replacing would modify it.</param>
    public void ApplyOffset(
        IReadOnlyList<SceneObject> sources,
        IReadOnlyDictionary<SceneObject, VectorPath> resultsBySource,
        bool keepOriginal = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(resultsBySource);

        var removed = new List<SceneObject>();
        var added = new List<SceneObject>();
        var anchored = new List<(SceneObject Anchor, SceneObject Added)>();
        foreach (var source in sources)
        {
            if (!Objects.Contains(source)) continue;
            if (!resultsBySource.TryGetValue(source, out var path) || path.Subpaths.Count == 0) continue;

            var offset = BuildOffsetObject(source, path);
            if (keepOriginal || source.IsLocked)
            {
                anchored.Add((source, offset));
            }
            else
            {
                removed.Add(source);
            }
            added.Add(offset);
        }

        if (added.Count == 0) return;

        if (anchored.Count > 0 && removed.Count > 0)
        {
            // A locked source next to unlocked ones in replace mode: keep the locked, replace the rest.
            var replacement = added.Where(item => !anchored.Any(pair => ReferenceEquals(pair.Added, item))).ToList();
            Execute(new CompositeSceneCommand(
            [
                new ReplaceObjectsCommand(Scene, removed, replacement),
                new InsertObjectsAboveCommand(Scene, anchored),
            ]));
        }
        else if (anchored.Count > 0)
        {
            Execute(new InsertObjectsAboveCommand(Scene, anchored));
        }
        else
        {
            Execute(new ReplaceObjectsCommand(Scene, removed, added));
        }

        SelectedObjects.ReplaceWith(added);
    }

    /// <summary>Commits the Offset dialog's merged result (VectorOffsetPlanner.ComputeMergedOffset) as
    /// ONE undo step. The results belong to the selection as a whole, so they take the layer, colour and
    /// mode of the topmost visible source (the same rule the boolean operations use) and are inserted
    /// directly above the topmost source. Originals stay unless <paramref name="deleteOriginals"/>; a
    /// locked source is never removed. The selection changes only when <paramref name="selectResults"/>
    /// (or when removed originals would otherwise stay selected).</summary>
    public void ApplyOffsetResults(
        IReadOnlyList<SceneObject> sources,
        IReadOnlyList<VectorPath> results,
        bool deleteOriginals = false,
        bool selectResults = false,
        VectorOffsetOptions? options = null,
        bool linked = false,
        Guid? replaceLinkId = null,
        IReadOnlyList<VectorOffsetResultKind>? resultKinds = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(results);

        var present = sources
            .Where(Objects.Contains)
            .OrderBy(item => Scene.Objects.IndexOf(item))
            .ToList();
        var paths = results.Where(path => path.Subpaths.Count > 0).ToList();
        if (present.Count == 0 || paths.Count == 0) return;
        if (linked && (options is null || deleteOriginals || present.Any(item => item.OffsetLink is not null)))
        {
            VectorOperationRejected?.Invoke("Prepojený offset potřebuje zachované původní objekty a nemůže záviset na jiném offsetu.");
            return;
        }
        if (linked && (resultKinds is null || resultKinds.Count != paths.Count))
        {
            VectorOperationRejected?.Invoke("Prepojený offset nemá úplný popis výsledných obrysov.");
            return;
        }
        var initialResultCount = paths.Count;
        var kinds = resultKinds?.ToList() ?? [];
        if (linked && present.Any(item => item.GetWorldShapes().Any(shape => shape.IsClosed)))
        {
            if (options!.Direction != VectorOffsetDirection.Inward && !kinds.Contains(VectorOffsetResultKind.Outward))
            {
                paths.Add(paths[0]);
                kinds.Add(VectorOffsetResultKind.Outward);
            }
            if (options.Direction != VectorOffsetDirection.Outward && !kinds.Contains(VectorOffsetResultKind.Inward))
            {
                paths.Add(paths[0]);
                kinds.Add(VectorOffsetResultKind.Inward);
            }
        }

        var sourceShapes = present.SelectMany(item => item.GetWorldShapes()).ToList();
        if (sourceShapes.Count == 0) return;
        var target = FindUnionTargetShape(present, sourceShapes);
        var name = present.Count == 1 ? $"Offset · {present[0].Name}" : "Offset";

        var added = new List<SceneObject>(paths.Count);
        var linkId = Guid.NewGuid();
        for (var resultIndex = 0; resultIndex < paths.Count; resultIndex++)
        {
            var path = paths[resultIndex];
            var created = VectorPathSceneFactory.Create(path, target.LayerColor, name);
            created.LocalShapes = created.LocalShapes.Select(shape => shape with
            {
                LayerId = target.LayerId,
                PreferredMode = target.PreferredMode,
            }).ToList();
            created.IsVisible = present.Any(item => item.IsVisible);
            created.IncludeInOutput = present.Any(item => item.IncludeInOutput);
            if (linked)
            {
                var suspended = resultIndex >= initialResultCount;
                var requestedVisible = created.IsVisible;
                var requestedOutput = created.IncludeInOutput;
                created = new SceneObject
                {
                    Id = created.Id,
                    Name = created.Name,
                    LocalShapes = created.LocalShapes,
                    LocalBounds = created.LocalBounds,
                    LocalPivot = created.LocalPivot,
                    VectorPath = created.VectorPath,
                    Transform = created.Transform,
                    IsVisible = !suspended && requestedVisible,
                    IncludeInOutput = !suspended && requestedOutput,
                    OffsetLink = new OffsetLink
                    {
                        LinkId = linkId,
                        SourceIds = present.Select(item => item.Id).ToArray(),
                        Options = options!,
                        Kind = kinds[resultIndex],
                        IsVisible = requestedVisible,
                        IncludeInOutput = requestedOutput,
                        IsSuspended = suspended,
                    },
                };
            }
            added.Add(created);
        }

        List<SceneObject> removed = deleteOriginals ? present.Where(item => !item.IsLocked).ToList() : [];
        if (replaceLinkId is { } oldLink)
            removed.AddRange(Objects.Where(item => item.OffsetLink?.LinkId == oldLink));
        if (removed.Count > 0)
        {
            Execute(new ReplaceObjectsCommand(Scene, removed, added));
        }
        else
        {
            var anchor = present[^1];
            var items = new List<(SceneObject Anchor, SceneObject Added)>(added.Count);
            foreach (var item in added)
            {
                items.Add((anchor, item));
                anchor = item;
            }
            Execute(new InsertObjectsAboveCommand(Scene, items));
        }

        if (selectResults)
            SelectedObjects.ReplaceWith(added.Select(item => Objects.FirstOrDefault(live => live.Id == item.Id))
                .OfType<SceneObject>().ToList());
        else if (removed.Count > 0)
            SelectedObjects.ReplaceWith(SelectedObjects.Where(item => !removed.Contains(item)).ToList());
    }

    /// <summary>Regenerates cached offset outlines after a source command or project load. This is
    /// derived state only: the source command remains the single Undo entry.</summary>
    private void RefreshLinkedOffsets()
    {
        if (_refreshingLinkedOffsets) return;
        var outputs = Objects.Where(item => item.OffsetLink is not null).ToList();
        if (outputs.Count == 0) return;
        _refreshingLinkedOffsets = true;
        try
        {
            foreach (var group in outputs.GroupBy(item => item.OffsetLink!.LinkId))
            {
                var recipe = group.First().OffsetLink!;
                var sources = recipe.SourceIds
                    .Select(id => Objects.FirstOrDefault(item => item.Id == id && item.OffsetLink is null))
                    .ToList();
                IReadOnlyList<VectorOffsetPart> parts = [];
                if (sources.All(item => item is not null))
                {
                    try
                    {
                        parts = VectorOffsetPlanner.ComputeMergedOffsetParts(sources.Select(item => item!).ToList(),
                            BooleanService, OffsetService, recipe.Options);
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
                    {
                        VectorOperationRejected?.Invoke($"Prepojený offset nelze přepočítat: {exception.Message}");
                    }
                }
                foreach (var previous in group)
                {
                    if (!Objects.Contains(previous) || previous.VectorPath is null) continue;
                    var link = previous.OffsetLink!;
                    var part = parts.FirstOrDefault(candidate => candidate.Kind == link.Kind);
                    var available = part is not null;
                    var requestedVisible = link.IsSuspended ? link.IsVisible : previous.IsVisible;
                    var requestedOutput = link.IsSuspended ? link.IncludeInOutput : previous.IncludeInOutput;
                    if (part is not null) previous.RefreshDerivedVectorPath(part.Path);
                    previous.IsVisible = available && requestedVisible;
                    previous.IncludeInOutput = available && requestedOutput;
                    previous.OffsetLink = available
                        ? link with { IsSuspended = false }
                        : link.IsSuspended ? link : link with
                        {
                            IsSuspended = true,
                            IsVisible = requestedVisible,
                            IncludeInOutput = requestedOutput,
                        };
                }
            }
        }
        finally { _refreshingLinkedOffsets = false; }
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
        $"Vektory nebyly změněny. {reason} Je potřeba zkontrolovat překrývající se nebo velmi tenké části, potom lze výběr zkusit znovu.");

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
        // curve data at all (legacy polygon operations and imported shapes without a VectorPath).
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

    /// <summary>Constructs editable straight segments for legacy artwork that has no curve source.</summary>
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
                Id = obj.Id,
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
                GroupId = obj.GroupId,
                NestedGroupIds = obj.NestedGroupIds.ToList(),
                RasterFilePath = obj.RasterFilePath,
                RasterOptions = obj.RasterOptions,
                OriginalRasterFilePath = obj.OriginalRasterFilePath,
                Text = obj.Text,
                VectorPath = obj.VectorPath,
                OffsetLink = obj.OffsetLink,
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
                IsLocked = layer.IsLocked,
                IsRaster = layer.IsRaster,
                RemoveWhenEmpty = layer.RemoveWhenEmpty,
            }).ToList(),
        };
    }

    public void ResetDocument()
    {
        EndTextFontPreview();
        _linkedOffsetPreviewTimer?.Stop();
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
        EndTextFontPreview();
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
                IsLocked = layer.IsLocked,
                IsRaster = layer.IsRaster || layer.Color.IsApproximately(SceneObjectFactory.RasterEngravingColor),
                RemoveWhenEmpty = layer.RemoveWhenEmpty,
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
                            LayerId = ResolveLayerId(shape.LayerId, shape.LayerColor, shape.PreferredMode, loadedLayers,
                                createWhenOrphaned: item.RasterFilePath is null),
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
                var loaded = new SceneObject
                {
                    Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                    LocalShapes = shapes,
                    LocalPivot = item.LocalPivot,
                    LocalBounds = item.LocalBounds,
                    RasterFilePath = item.RasterFilePath,
                    RasterOptions = item.RasterOptions,
                    OriginalRasterFilePath = item.OriginalRasterFilePath,
                    Text = item.Text,
                    VectorPath = item.VectorPath,
                    OffsetLink = item.OffsetLink,
                    Name = item.Name,
                    Transform = item.Transform,
                    IsVisible = item.IsVisible,
                    IsLocked = item.IsLocked,
                    IncludeInOutput = item.IncludeInOutput,
                    GroupId = item.GroupId is { } groupId && groupId != Guid.Empty ? groupId : null,
                    NestedGroupIds = item.NestedGroupIds?.Where(id => id != Guid.Empty).ToArray() ?? [],
                };
                // Re-derive the cached outline when it no longer matches the saved nodes.
                loaded.ReconcileVectorGeometry();
                return loaded.WithPivotAtBoundsCenter();
            })
            .ToList();

        static Guid ResolveLayerId(Guid persistedId, RgbColor color, LayerMode mode, List<LayerSettings> layers, bool createWhenOrphaned)
        {
            if (persistedId != Guid.Empty && layers.Any(layer => layer.Id == persistedId))
                return persistedId;
            var match = layers.FirstOrDefault(layer => layer.Mode == mode && layer.Color.IsApproximately(color))
                ?? layers.FirstOrDefault(layer => layer.Color.IsApproximately(color));
            if (match is not null) return match.Id;
            if (!createWhenOrphaned) return Guid.Empty;

            // Geometry that points at a layer the project no longer contains would still be drawn but
            // never cut or engraved. Give it a real layer so what is on the canvas is what the job does.
            var created = LayerSettings.CreateDefault(color, mode, color.ToHex(), removeWhenEmpty: true);
            layers.Add(created);
            return created.Id;
        }

        // The canvas listens to Objects. Telling it about every object of a large project one at a
        // time made it rebuild the whole scene per object; it is told once, after the document is whole.
        IsLoadingProject = true;
        try
        {
            ResetDocument();
            foreach (var layer in loadedLayers) Layers.Add(layer);
            foreach (var item in loadedObjects) Objects.Add(item);
        }
        finally
        {
            IsLoadingProject = false;
        }

        RefreshLinkedOffsets();
        ProjectLoaded?.Invoke();
        RefreshCommands();
    }

    /// <summary>True while <see cref="LoadProject"/> is replacing the document. Views should ignore
    /// per-object collection changes in that window and rebuild once on <see cref="ProjectLoaded"/>.</summary>
    public bool IsLoadingProject { get; private set; }

    /// <summary>Raised once after <see cref="LoadProject"/> has replaced the document.</summary>
    public event Action? ProjectLoaded;
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
        AlignCenterBothCommand.NotifyCanExecuteChanged();
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
        BreakApartSelectionCommand.NotifyCanExecuteChanged();
        UniteSelectionCommand.NotifyCanExecuteChanged();
        SubtractSelectionCommand.NotifyCanExecuteChanged();
        IntersectSelectionCommand.NotifyCanExecuteChanged();
        ExcludeSelectionCommand.NotifyCanExecuteChanged();
        DivideSelectionCommand.NotifyCanExecuteChanged();
        FillBetweenPathsCommand.NotifyCanExecuteChanged();
        FractureSelectionCommand.NotifyCanExecuteChanged();
        WeldSelectionCommand.NotifyCanExecuteChanged();
        FlattenSelectedVectorPathsCommand.NotifyCanExecuteChanged();
        SimplifySelectedVectorPathsCommand.NotifyCanExecuteChanged();
        OffsetSelectionCommand.NotifyCanExecuteChanged();
        ShrinkSelectionCommand.NotifyCanExecuteChanged();
        ExpandSelectionCommand.NotifyCanExecuteChanged();
        TraceSelectedRasterCommand.NotifyCanExecuteChanged();
        RemoveSelectedBackgroundCommand.NotifyCanExecuteChanged();
        RestoreSelectedBackgroundCommand.NotifyCanExecuteChanged();
        ConvertSelectedTextToPathCommand.NotifyCanExecuteChanged();
        ConvertSelectionToBitmapCommand.NotifyCanExecuteChanged();
        EditSelectedRasterCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(SceneObject? value)
    {
        NotifySelectionStateChanged();
        RefreshCommands();
    }

    partial void OnIsRemovingBackgroundChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveSelectedBackground));
        RemoveSelectedBackgroundCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLayerChanged(LayerSettings? value) => NotifyLayerStateChanged();

    // --- Property-panel wrapper properties: ObjectTransform is a readonly struct, so binding a TextBox
    // straight to "Selected.Transform.X" has no settable path. Position and size describe the whole
    // selection; rotation remains a single-object property because a multi-selection has no single angle. ---

    public double SelectedX
    {
        get => Selected is { } item
            ? item.LocalPivot.X + item.Transform.X
            : SelectionBounds() is { } bounds && !bounds.IsEmpty ? (bounds.MinX + bounds.MaxX) / 2 : 0;
        set => MoveSelectionTo(value, SelectedY);
    }

    public double SelectedY
    {
        get => Selected is { } item
            ? item.LocalPivot.Y + item.Transform.Y
            : SelectionBounds() is { } bounds && !bounds.IsEmpty ? (bounds.MinY + bounds.MaxY) / 2 : 0;
        set => MoveSelectionTo(SelectedX, value);
    }

    public double SelectedRotation
    {
        get => Selected?.Transform.RotationDeg ?? 0;
        set => SetSelectedTransform(t => t with { RotationDeg = value }, allowRaster: false);
    }

    public double SelectedWidth
    {
        get => SelectionBounds() is { } bounds && !bounds.IsEmpty ? bounds.Width : 0;
        set => SetSelectedSize(value, isWidth: true);
    }

    public double SelectedHeight
    {
        get => SelectionBounds() is { } bounds && !bounds.IsEmpty ? bounds.Height : 0;
        set => SetSelectedSize(value, isWidth: false);
    }

    private BoundingBox2D SelectionBounds()
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var item in SelectedObjects)
        {
            var itemBounds = item.WorldBounds();
            if (itemBounds.IsEmpty) continue;
            bounds = bounds.Include(itemBounds.MinX, itemBounds.MinY).Include(itemBounds.MaxX, itemBounds.MaxY);
        }
        return bounds;
    }

    private void MoveSelectionTo(double x, double y)
    {
        if (!CanEditSelectedPosition || !double.IsFinite(x) || !double.IsFinite(y)) return;
        var dx = x - SelectedX;
        var dy = y - SelectedY;
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001) return;
        var commands = SelectedObjects.Select(item => (ISceneCommand)new TransformObjectCommand(
            item, item.Transform, item.Transform with { X = item.Transform.X + dx, Y = item.Transform.Y + dy })).ToList();
        if (commands.Count > 0) Execute(new CompositeSceneCommand(commands));
    }

    private void SetSelectedSize(double value, bool isWidth)
    {
        if (!double.IsFinite(value) || value <= 0 || !CanTransformSelectedObject)
            return;

        var current = isWidth ? SelectedWidth : SelectedHeight;
        if (current <= 0 || Math.Abs(current - value) < 0.0001) return;

        var factor = value / current;
        var bounds = SelectionBounds();
        if (bounds.IsEmpty) return;
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var commands = new List<ISceneCommand>();
        foreach (var item in SelectedObjects)
        {
            var before = item.Transform;
            var pivotWorldX = item.LocalPivot.X + before.X;
            var pivotWorldY = item.LocalPivot.Y + before.Y;
            var afterPivotX = centerX + (pivotWorldX - centerX) * (isWidth ? factor : LockAspectRatio ? factor : 1);
            var afterPivotY = centerY + (pivotWorldY - centerY) * (!isWidth && !LockAspectRatio ? factor : LockAspectRatio ? factor : 1);
            var scaleX = before.ScaleX;
            var scaleY = before.ScaleY;

            if (LockAspectRatio)
            {
                scaleX *= factor;
                scaleY *= factor;
            }
            else
            {
                // World-axis nonuniform scaling of a rotated object would require shear. For axis-aligned
                // rotations, map the requested world axis to the corresponding local scale axis.
                var quarterTurns = (int)Math.Round(before.RotationDeg / 90d);
                var axisAligned = Math.Abs(before.RotationDeg - quarterTurns * 90d) < 1e-8;
                if (!axisAligned) return;
                var localX = (Math.Abs(quarterTurns) % 2 == 0) == isWidth;
                if (localX) scaleX *= factor; else scaleY *= factor;
                if (isWidth) afterPivotX = centerX + (pivotWorldX - centerX) * factor;
                else afterPivotY = centerY + (pivotWorldY - centerY) * factor;
            }

            var after = before with
            {
                X = afterPivotX - item.LocalPivot.X,
                Y = afterPivotY - item.LocalPivot.Y,
                ScaleX = scaleX,
                ScaleY = scaleY,
            };
            if (!before.Equals(after)) commands.Add(new TransformObjectCommand(item, before, after));
        }
        if (commands.Count > 0) Execute(new CompositeSceneCommand(commands));
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
        set
        {
            EndTextFontPreview();
            ApplyTextSource(source => source with { FontFamily = value });
        }
    }

    private SceneObject? _fontPreviewOriginal;
    private SceneObject? _fontPreviewShown;

    /// <summary>Shows the selected text in another font while the operator points at it in the list. It swaps
    /// the object in place with no undo step; EndTextFontPreview puts the original back, and the normal
    /// property setter then commits the chosen font as one undoable edit.</summary>
    public void PreviewTextFontFamily(string family)
    {
        var baseItem = _fontPreviewOriginal ?? (Selected is { IsText: true, IsLocked: false } selected ? selected : null);
        if (baseItem?.Text is null || string.IsNullOrWhiteSpace(family)) return;
        if (string.Equals((_fontPreviewShown ?? baseItem).Text?.FontFamily, family, StringComparison.OrdinalIgnoreCase)) return;
        var index = Objects.IndexOf(_fontPreviewShown ?? baseItem);
        if (index < 0) return;

        // Moving back to the committed family restores the original object without rebuilding.
        if (string.Equals(baseItem.Text.FontFamily, family, StringComparison.OrdinalIgnoreCase))
        {
            EndTextFontPreview();
            return;
        }

        SceneObject replacement;
        try { replacement = VectorTextFactory.Rebuild(baseItem, baseItem.Text with { FontFamily = family }); }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException) { return; }

        _fontPreviewOriginal = baseItem;
        _fontPreviewShown = replacement;
        Objects[index] = replacement;
        SelectedObjects.ReplaceWith([replacement]);
    }

    /// <summary>Only the explicit choice enters history. Keyboard movement and hover replace the
    /// temporary preview; the committed edit starts from the original object exactly once.</summary>
    public void CommitTextFontPreview(string family)
    {
        if (_fontPreviewShown is not null && !ReferenceEquals(Selected, _fontPreviewShown))
        {
            EndTextFontPreview();
            return;
        }

        EndTextFontPreview();
        ApplyTextSource(source => source with { FontFamily = family });
    }

    public void EndTextFontPreview()
    {
        if (_fontPreviewOriginal is null || _fontPreviewShown is null) return;
        var original = _fontPreviewOriginal;
        var shown = _fontPreviewShown;
        _fontPreviewOriginal = null;
        _fontPreviewShown = null;
        var index = Objects.IndexOf(shown);
        if (index < 0) return;
        Objects[index] = original;
        if (SelectedObjects.Contains(shown))
        {
            var selectionIndex = SelectedObjects.IndexOf(shown);
            SelectedObjects[selectionIndex] = original;
        }
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

    private void OnSelectedObjectPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneObject.Transform))
        {
            if (IsApplyingCommand) _selectedTransformNotificationPending = true; // raised once when the command ends
            else
            {
                NotifySelectedTransformChanged();
                if (!_refreshingLinkedOffsets) ScheduleLinkedOffsetPreview();
            }
        }
        else if (e.PropertyName == nameof(SceneObject.IsLocked))
        {
            OnPropertyChanged(nameof(CanEditSelectedPosition));
            OnPropertyChanged(nameof(CanTransformSelectedObject));
            NotifySelectionStateChanged();
            RefreshCommands();
        }
        else if (e.PropertyName == nameof(SceneObject.GroupId))
        {
            if (IsApplyingCommand) _selectedGroupNotificationPending = true;
            else
            {
                NotifySelectionStateChanged();
                RefreshCommands();
            }
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
        OnPropertyChanged(nameof(CanBreakApartSelection));
        OnPropertyChanged(nameof(CanUniteSelection));
        OnPropertyChanged(nameof(CanWeldSelection));
        OnPropertyChanged(nameof(CanBooleanPairSelection));
        OnPropertyChanged(nameof(BooleanPairSelectionDisabledReason));
        OnPropertyChanged(nameof(CanDivideSelection));
        OnPropertyChanged(nameof(CanFractureSelection));
        OnPropertyChanged(nameof(CanFillBetweenPaths));
        OnPropertyChanged(nameof(CanEditSelectedVectorPaths));
        OnPropertyChanged(nameof(UniteSelectionDisabledReason));
        OnPropertyChanged(nameof(CanOffsetSelection));
        OnPropertyChanged(nameof(OffsetSelectionDisabledReason));
        OnPropertyChanged(nameof(CanTraceSelectedRaster));
        OnPropertyChanged(nameof(CanRemoveSelectedBackground));
        OnPropertyChanged(nameof(CanRestoreSelectedBackground));
        OnPropertyChanged(nameof(CanConvertSelectedTextToPath));
        OnPropertyChanged(nameof(CanConvertSelectionToBitmap));
        OnPropertyChanged(nameof(ConvertSelectionToBitmapDisabledReason));
        OnPropertyChanged(nameof(IsSelectedRaster));
        OnPropertyChanged(nameof(CanAssignSelectionToLayer));
        OnPropertyChanged(nameof(CanEditSelectedRaster));
        OnPropertyChanged(nameof(SelectedX));
        OnPropertyChanged(nameof(SelectedY));
        OnPropertyChanged(nameof(SelectedRotation));
        OnPropertyChanged(nameof(SelectedWidth));
        OnPropertyChanged(nameof(SelectedHeight));
        NotifyTextStateChanged();
    }
}
