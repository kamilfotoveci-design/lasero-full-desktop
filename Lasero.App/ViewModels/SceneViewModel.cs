using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
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
        new(18, 18, 18), new(214, 42, 42), new(239, 108, 37), new(232, 180, 0),
        new(42, 157, 82), new(0, 151, 167), new(31, 95, 204), new(92, 76, 196),
        new(179, 62, 153), new(117, 72, 42), new(98, 105, 113), new(173, 181, 189),
    ];

    [ObservableProperty] private SceneObject? _selected;
    [ObservableProperty] private Lasero.Core.Layers.LayerSettings? _selectedLayer;
    [ObservableProperty] private bool _lockAspectRatio = true;
    [ObservableProperty] private DesignerTool _activeTool = DesignerTool.Select;

    /// <summary>Drives the OBRÁZEK section's inline spinner in DesignerInspectorView — set by
    /// MainWindow around its model-download/inference calls, which is where the actual work happens
    /// (see BackgroundRemovalRequested). Kept as plain observable state, not a dialog, per the "feels
    /// like Crop/Brightness-Contrast" requirement: only the one-time model download gets a dialog.</summary>
    [ObservableProperty] private bool _isRemovingBackground;
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
    public bool CanUniteSelection => SelectedObjects.Count > 0 &&
        SelectedObjects.All(item => !item.IsRaster && !item.IsLocked) &&
        SelectedObjects.SelectMany(item => item.LocalShapes).Count() >= 2 &&
        SelectedObjects.SelectMany(item => item.LocalShapes).All(shape => shape.IsClosed && shape.Points.Count >= 3);
    public bool CanTraceSelectedRaster => SelectedObjects.Count == 1 && Selected is { IsRaster: true, IsLocked: false };
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
    public event Action<string>? VectorOperationRejected;

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
    /// MouseUp, keyboard nudge) â€” kept public so SceneCanvas doesn't need its own reference to the stack.</summary>
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

    /// <summary>Kicks off "Odstranit pozadí" — the view (DesignerInspectorView/MainWindow) owns the
    /// actual model-download prompt, off-thread inference call and processing-state UI, matching how
    /// TraceSelectedRaster hands off to MainWindow.OnTraceRasterRequested rather than doing any of
    /// that work in the view model itself.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedBackground))]
    private void RemoveSelectedBackground()
    {
        if (Selected is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false } source)
            BackgroundRemovalRequested?.Invoke(source);
    }

    public void ReplaceRasterWithTrace(SceneObject source, BitmapTraceResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        if (!source.IsRaster || !Objects.Contains(source))
            throw new InvalidOperationException("The bitmap is no longer available in the scene.");
        if (result.Document.Shapes.Count == 0)
            throw new InvalidOperationException("Tracing did not produce any vector contours.");

        var traced = SceneObjectFactory.FromImportedDocument(result.Document, $"Trasování · {source.Name}");
        traced.Transform = source.Transform;
        traced.IsVisible = source.IsVisible;
        traced.IncludeInOutput = source.IncludeInOutput;

        Execute(new ReplaceObjectsCommand(Scene, [source], [traced], result.Document.Layers));
        SelectedObjects.Clear();
        SelectedObjects.Add(traced);
        ActiveTool = DesignerTool.Select;
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
    /// removal exactly like those. removedBackgroundFilePath is the new file BackgroundRemovalService
    /// already wrote (with real alpha transparency); this method only ever swaps which file
    /// RasterFilePath points at — geometry, layer, transform and every other property are untouched.</summary>
    public void CommitBackgroundRemoval(SceneObject item, string removedBackgroundFilePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(removedBackgroundFilePath);
        if (!item.IsRaster || item.HasBackgroundRemoved || !Objects.Contains(item)) return;

        var replacement = WithRasterFile(item, removedBackgroundFilePath, item.RasterFilePath);
        var wasSelected = SelectedObjects.Contains(item);
        Execute(new ReplaceObjectsCommand(Scene, [item], [replacement]));
        if (wasSelected)
        {
            SelectedObjects.Clear();
            SelectedObjects.Add(replacement);
        }
        NotifySelectionStateChanged();
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

        var changed = false;
        foreach (var item in SelectedObjects)
            changed |= item.AssignToLayer(layer);
        if (!changed) return;

        SelectedLayer = layer;
        NotifyLayerStateChanged();
        Changed?.Invoke();
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

    [RelayCommand(CanExecute = nameof(CanUndoExecute))]
    private void Undo() => _commandStack.Undo();
    private bool CanUndoExecute() => CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoExecute))]
    private void Redo() => _commandStack.Redo();
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
            sources.All(item => item.IncludeInOutput));

        Execute(new ReplaceObjectsCommand(Scene, sources, [group]));
        SelectedObjects.Clear();
        SelectedObjects.Add(group);
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
        var parts = source.GetWorldShapes()
            .Select(shape => shape.GeometrySetId == Guid.Empty
                ? shape with { GeometrySetId = legacyGeometrySetId }
                : shape)
            .Select((shape, index) => CreateVectorObject(
                [shape],
                $"{source.Name} · část {index + 1}",
                source.IsVisible,
                source.IncludeInOutput))
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
            Geometry? united = null;
            foreach (var source in sources)
            {
                var normalizedShapes = NormalizeNestedCompoundPaths(source.GetWorldShapes());
                foreach (var geometrySet in normalizedShapes.GroupBy(shape => shape.GeometrySetId))
                {
                    var sourceGeometry = BuildFilledGeometry(geometrySet);
                    united = united is null
                        ? sourceGeometry
                        : Geometry.Combine(
                            united,
                            sourceGeometry,
                            GeometryCombineMode.Union,
                            null,
                            BooleanGeometryToleranceMm,
                            ToleranceType.Absolute);
                }
            }

            if (united is null || united.GetArea(BooleanGeometryToleranceMm, ToleranceType.Absolute) <= MinimumUnitedAreaSquareMm)
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
    /// (BuildFilledGeometry, ToImportedShapes, IsSafeUnionResult, NormalizeNestedCompoundPaths,
    /// FindUnionTargetShape, RejectVectorUnion). Their names say "union", but the logic underneath is
    /// identical for every mode — only Geometry.Combine's own mode argument changes, which is exactly
    /// what this method threads through. Kept as a copy of UniteSelection's shape rather than a shared
    /// refactor of it, so this addition cannot regress the existing, already-tested union path.
    ///
    /// Source order matters for Subtract (it does not, mathematically, for Intersect/Xor, but folding
    /// left-to-right is deterministic either way): sources are processed back-to-front by scene
    /// z-order, so Subtract reads as "shapes in front cut a hole in the shape behind them" — Minus
    /// Front in Illustrator, Subtract in Figma — rather than an order the user cannot predict from
    /// selection order alone.</summary>
    private void CombineSelection(GeometryCombineMode mode, string resultName)
    {
        var sources = SelectedObjects.OrderBy(item => Scene.Objects.IndexOf(item)).ToList();
        if (sources.Count == 0) return;

        var sourceShapes = sources.SelectMany(item => item.GetWorldShapes()).ToList();
        var targetShape = FindUnionTargetShape(sources, sourceShapes);

        try
        {
            Geometry? combined = null;
            foreach (var source in sources)
            {
                var normalizedShapes = NormalizeNestedCompoundPaths(source.GetWorldShapes());
                Geometry? sourceGeometry = null;
                foreach (var geometrySet in normalizedShapes.GroupBy(shape => shape.GeometrySetId))
                {
                    var figureGeometry = BuildFilledGeometry(geometrySet);
                    sourceGeometry = sourceGeometry is null
                        ? figureGeometry
                        : Geometry.Combine(
                            sourceGeometry,
                            figureGeometry,
                            GeometryCombineMode.Union,
                            null,
                            BooleanGeometryToleranceMm,
                            ToleranceType.Absolute);
                }

                if (sourceGeometry is null) continue;
                combined = combined is null
                    ? sourceGeometry
                    : Geometry.Combine(
                        combined,
                        sourceGeometry,
                        mode,
                        null,
                        BooleanGeometryToleranceMm,
                        ToleranceType.Absolute);
            }

            if (combined is null || combined.GetArea(BooleanGeometryToleranceMm, ToleranceType.Absolute) <= MinimumUnitedAreaSquareMm)
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
    private void SubtractSelection() => CombineSelection(GeometryCombineMode.Exclude, "Odečtený vektor");

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void IntersectSelection() => CombineSelection(GeometryCombineMode.Intersect, "Průnik vektorů");

    [RelayCommand(CanExecute = nameof(CanUniteSelection))]
    private void ExcludeSelection() => CombineSelection(GeometryCombineMode.Xor, "Vyloučený vektor");

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

    private static Geometry BuildFilledGeometry(IEnumerable<ImportedShape> shapes)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            foreach (var shape in shapes.Where(item => item.IsClosed && item.Points.Count >= 3))
            {
                var points = shape.Points;
                context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true, isClosed: true);
                context.PolyLineTo(points.Skip(1).Select(point => new Point(point.X, point.Y)).ToList(), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static List<ImportedShape> ToImportedShapes(
        Geometry geometry,
        Guid geometrySetId,
        Guid layerId,
        RgbColor color,
        LayerMode mode)
    {
        var flattened = geometry.GetFlattenedPathGeometry(BooleanGeometryToleranceMm, ToleranceType.Absolute);
        var result = new List<ImportedShape>();

        foreach (var figure in flattened.Figures.Where(figure => figure.IsClosed))
        {
            var points = new List<Position> { new(figure.StartPoint.X, figure.StartPoint.Y, 0) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        points.Add(new Position(line.Point.X, line.Point.Y, 0));
                        break;
                    case PolyLineSegment polyLine:
                        points.AddRange(polyLine.Points.Select(point => new Position(point.X, point.Y, 0)));
                        break;
                }
            }

            if (points.Count > 1 && DistanceSquared(points[0], points[^1]) < 0.000001)
                points.RemoveAt(points.Count - 1);
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
        bool includeInOutput)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in worldShapes.SelectMany(shape => shape.Points))
            bounds = bounds.Include(point.X, point.Y);

        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var localShapes = worldShapes.Select(shape => shape with
        {
            Points = shape.Points
                .Select(point => new Position(point.X - centerX, point.Y - centerY, point.Z))
                .ToList(),
        }).ToList();

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
        };
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
                };
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
    // straight to "Selected.Transform.X" has no settable path â€” these translate a set into one
    // TransformObjectCommand each, same as a completed drag gesture. Round 1 is single-selection only. ---

    public double SelectedX
    {
        get => Selected?.Transform.X ?? 0;
        set => SetSelectedTransform(t => t with { X = value }, allowRaster: true);
    }

    public double SelectedY
    {
        get => Selected?.Transform.Y ?? 0;
        set => SetSelectedTransform(t => t with { Y = value }, allowRaster: true);
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
            replacement = VectorTextFactory.Rebuild(item, source);
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
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanEditSelectedPosition));
        OnPropertyChanged(nameof(CanTransformSelectedObject));
        OnPropertyChanged(nameof(CanRotateSelectedObject));
        OnPropertyChanged(nameof(IsSelectedLocked));
        OnPropertyChanged(nameof(CanGroupSelection));
        OnPropertyChanged(nameof(CanUngroupSelection));
        OnPropertyChanged(nameof(CanUniteSelection));
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
