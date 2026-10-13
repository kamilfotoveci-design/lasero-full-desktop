using System.Reactive;
using System.Runtime.Versioning;
using CSharpFunctionalExtensions;
using Lasero.Avalonia.Services;
using Lasero.Core.BackgroundRemoval;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Machines;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Lasero.Core.Trace;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

public enum EditorTool
{
    Select,
    Text,
    Rectangle,
    Line,
    Shape,
}

/// <summary>
/// Matches Lasero.App's DesignerTool shape subset (Rectangle/Ellipse/Triangle/Pentagon/Hexagon/Octagon/
/// Star/DoubleStar) — the "which shape does the rail's shape button currently remember" concept. WPF
/// tracks this as one of several ActiveTool values; here it is a separate property alongside
/// EditorTool.Shape, which is functionally equivalent (one shape drawing at a time) without needing
/// eight ActiveTool cases.
/// </summary>
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    Pentagon,
    Hexagon,
    Octagon,
    Star,
    DoubleStar,
}

public sealed class EditorViewModel : ReactiveObject
{
    private const double MinimumZoom = 0.25;
    private const double MaximumZoom = 4;
    private const double CascadeOffsetMm = 10;
    private EditorTool activeTool = EditorTool.Select;
    private ShapeKind currentShapeTool = ShapeKind.Rectangle;
    private SceneObject? selectedObject;
    private LayerSettings? selectedLayer;
    private double zoom = 1;
    private double panX;
    private double panY;
    private int importCount;
    private readonly SceneCommandStack commandStack = new();

    // Lasero.App splits this differently (GCodeViewModel owns the connection/settings and builds
    // RasterImportViewModel itself; SceneViewModel never sees either). Avalonia doesn't have a
    // GCodeViewModel yet (that's the next phase per HANDOFF.md) and EditorViewModel is already the
    // one place ImportRasterFile/ImportSvgFile live, so it also owns what RasterImportViewModel needs
    // to construct itself — narrower than exposing the whole machine to the Designer surface, but the
    // same real ILaserMachine/AppSettingsStore singletons WPF passes.
    private readonly ILaserMachine machine;
    private readonly AppSettingsStore settingsStore;

    // Windows-only (see BackgroundRemovalService's own [SupportedOSPlatform]) but constructed
    // unconditionally like Lasero.App's MainWindow field of the same name — this app is itself
    // Windows-only at runtime (see App.axaml.cs), the plain net10.0 TFM is only about a future
    // cross-platform build, not today's behavior.
    [SupportedOSPlatform("windows")]
    private readonly BackgroundRemovalService backgroundRemovalService = new();

    /// <summary>Raised so the view can own the actual dialog/window (matches Lasero.App's
    /// MainWindow.OnTraceRasterRequested) instead of the view model reaching into UI concerns.</summary>
    public event Action<SceneObject>? TraceRasterRequested;

    /// <summary>Raised for "Odstranit pozadí" — the view owns the model-download confirmation and the
    /// off-thread inference call, matching MainWindow.OnBackgroundRemovalRequested.</summary>
    public event Action<SceneObject>? BackgroundRemovalRequested;

    // Only the field initializer above (BackgroundRemovalService's own constructor) is Windows-only;
    // marking just this constructor — instead of the whole class, which EditorViewModel's many
    // unrelated consumers (ShellViewModel, EditorView, DesignerCanvas) would then all need to account
    // for — keeps that to the one place it actually applies. DI already only constructs this from
    // App.axaml.cs's own [SupportedOSPlatform("windows")]-marked composition root.
    [SupportedOSPlatform("windows")]
    public EditorViewModel(ILaserMachine machine, AppSettingsStore settingsStore)
    {
        this.machine = machine;
        this.settingsStore = settingsStore;
        Document = new SceneDocument();
        SelectTool = ReactiveCommand.Create(() => { ActiveTool = EditorTool.Select; });
        TextTool = ReactiveCommand.Create(() => { ActiveTool = EditorTool.Text; });
        LineTool = ReactiveCommand.Create(() => { ActiveTool = EditorTool.Line; });
        ShapeTool = ReactiveCommand.Create(() => { ActiveTool = EditorTool.Shape; });
        SelectShape = ReactiveCommand.Create<ShapeKind>(kind =>
        {
            CurrentShapeTool = kind;
            ActiveTool = EditorTool.Shape;
        });
        ZoomIn = ReactiveCommand.Create(() => { Zoom = Math.Min(MaximumZoom, Zoom * 1.2); });
        ZoomOut = ReactiveCommand.Create(() => { Zoom = Math.Max(MinimumZoom, Zoom / 1.2); });
        ResetView = ReactiveCommand.Create(() =>
        {
            Zoom = 1;
            PanX = 0;
            PanY = 0;
        });
        CenterView = ReactiveCommand.Create(() =>
        {
            PanX = 0;
            PanY = 0;
        });
        Undo = ReactiveCommand.Create(() => commandStack.Undo());
        Redo = ReactiveCommand.Create(() => commandStack.Redo());
        commandStack.Changed += () =>
        {
            this.RaisePropertyChanged(nameof(CanUndo));
            this.RaisePropertyChanged(nameof(CanRedo));
            this.RaisePropertyChanged(nameof(HasLayers));
            NotifyLayerStateChanged();
        };

        MoveSelectedLayerUp = ReactiveCommand.Create(() =>
        {
            if (SelectedLayer is null) return;
            var index = Document.Layers.IndexOf(SelectedLayer);
            if (index > 0) Document.Layers.Move(index, index - 1);
            NotifyLayerStateChanged();
        });
        MoveSelectedLayerDown = ReactiveCommand.Create(() =>
        {
            if (SelectedLayer is null) return;
            var index = Document.Layers.IndexOf(SelectedLayer);
            if (index >= 0 && index < Document.Layers.Count - 1) Document.Layers.Move(index, index + 1);
            NotifyLayerStateChanged();
        });
        DeleteSelectedLayer = ReactiveCommand.Create(() =>
        {
            if (SelectedLayer is null) return;
            Document.Layers.Remove(SelectedLayer);
            SelectedLayer = null;
        });

        TraceSelectedRaster = ReactiveCommand.Create(
            () => { if (SelectedObject is { IsRaster: true, IsLocked: false } source) TraceRasterRequested?.Invoke(source); },
            this.WhenAnyValue(x => x.CanTraceSelectedRaster));
        RemoveSelectedBackground = ReactiveCommand.Create(
            () => { if (SelectedObject is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false } source) BackgroundRemovalRequested?.Invoke(source); },
            this.WhenAnyValue(x => x.CanRemoveSelectedBackground));
        RestoreSelectedBackground = ReactiveCommand.Create(
            RestoreOriginalBackground,
            this.WhenAnyValue(x => x.CanRestoreSelectedBackground));
    }

    public SceneDocument Document { get; }

    public EditorTool ActiveTool
    {
        get => activeTool;
        set
        {
            if (activeTool == value) return;
            this.RaiseAndSetIfChanged(ref activeTool, value);
            this.RaisePropertyChanged(nameof(IsSelectTool));
            this.RaisePropertyChanged(nameof(IsTextTool));
            this.RaisePropertyChanged(nameof(IsLineTool));
            this.RaisePropertyChanged(nameof(IsShapeTool));
        }
    }

    /// <summary>Which of the 8 shapes the rail's shape button currently remembers — matches
    /// Lasero.App's Scene.CurrentShapeTool. Selecting one from the picker also activates it
    /// immediately (see SelectShape), same as WPF's OnShapePickerItemClick.</summary>
    public ShapeKind CurrentShapeTool
    {
        get => currentShapeTool;
        set => this.RaiseAndSetIfChanged(ref currentShapeTool, value);
    }

    public SceneObject? SelectedObject
    {
        get => selectedObject;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedObject, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectedObjectName));
            BackgroundRemovalError = null;
            this.RaisePropertyChanged(nameof(IsSelectedRaster));
            this.RaisePropertyChanged(nameof(CanTraceSelectedRaster));
            this.RaisePropertyChanged(nameof(CanRemoveSelectedBackground));
            this.RaisePropertyChanged(nameof(CanRestoreSelectedBackground));
        }
    }

    /// <summary>Gates the inspector's OBRÁZEK section — true for exactly the one selected raster
    /// object, matching Lasero.App's SceneViewModel.IsSelectedRaster.</summary>
    public bool IsSelectedRaster => SelectedObject?.IsRaster == true;
    public bool CanTraceSelectedRaster => SelectedObject is { IsRaster: true, IsLocked: false };
    public bool CanRemoveSelectedBackground => SelectedObject is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: false };
    public bool CanRestoreSelectedBackground => SelectedObject is { IsRaster: true, IsLocked: false, HasBackgroundRemoved: true };

    /// <summary>Drives the OBRÁZEK section's inline spinner — set by the view around its
    /// model-download/inference calls (see BackgroundRemovalRequested), matching Lasero.App's
    /// SceneViewModel fields of the same names.</summary>
    private bool isRemovingBackground;
    public bool IsRemovingBackground { get => isRemovingBackground; set => this.RaiseAndSetIfChanged(ref isRemovingBackground, value); }

    private string? backgroundRemovalStatus;
    public string? BackgroundRemovalStatus { get => backgroundRemovalStatus; set => this.RaiseAndSetIfChanged(ref backgroundRemovalStatus, value); }

    private string? backgroundRemovalError;
    public string? BackgroundRemovalError { get => backgroundRemovalError; set => this.RaiseAndSetIfChanged(ref backgroundRemovalError, value); }

    private double backgroundRemovalProgress;
    public double BackgroundRemovalProgress { get => backgroundRemovalProgress; set => this.RaiseAndSetIfChanged(ref backgroundRemovalProgress, value); }

    public double Zoom
    {
        get => zoom;
        set
        {
            var bounded = Math.Clamp(value, MinimumZoom, MaximumZoom);
            this.RaiseAndSetIfChanged(ref zoom, bounded);
            this.RaisePropertyChanged(nameof(ZoomPercent));
        }
    }

    public double PanX
    {
        get => panX;
        set => this.RaiseAndSetIfChanged(ref panX, value);
    }

    public double PanY
    {
        get => panY;
        set => this.RaiseAndSetIfChanged(ref panY, value);
    }

    public bool IsSelectTool => ActiveTool == EditorTool.Select;
    public bool IsTextTool => ActiveTool == EditorTool.Text;
    public bool IsLineTool => ActiveTool == EditorTool.Line;
    public bool IsShapeTool => ActiveTool == EditorTool.Shape;
    public bool HasSelection => SelectedObject is not null;
    public string SelectedObjectName => SelectedObject?.Name ?? "Není vybrán žádný objekt";
    public string ZoomPercent => $"{Zoom * 100:0}%";
    public bool CanUndo => commandStack.CanUndo;
    public bool CanRedo => commandStack.CanRedo;
    public bool HasLayers => Document.Layers.Count > 0;

    /// <summary>The operation stack, in execution order — matches Lasero.App's GCode.Layers /
    /// Scene.Layers binding target (OPERACE list). Same underlying Lasero.Core.Layers.LayerSettings
    /// collection the WPF app reads, so a shape drawn here creates a real layer row, not a mock.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LayerSettings> Layers => Document.Layers;

    public LayerSettings? SelectedLayer
    {
        get => selectedLayer;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedLayer, value);
            NotifyLayerStateChanged();
        }
    }

    public bool IsSelectedLayerRaster => SelectedLayer?.IsRaster == true;
    public bool CanReorderLayers => Layers.Count > 1;
    public bool CanMoveSelectedLayerUp => SelectedLayer is not null && Layers.IndexOf(SelectedLayer) > 0;
    public bool CanMoveSelectedLayerDown => SelectedLayer is not null && Layers.IndexOf(SelectedLayer) is var index && index >= 0 && index < Layers.Count - 1;
    public bool CanDeleteSelectedLayer => SelectedLayer is not null && CountObjectsUsingLayer(SelectedLayer) == 0;

    /// <summary>Matches Lasero.App's SceneViewModel.SelectedLayerUsageLabel wording — explains, on the
    /// disabled delete button's tooltip, why a layer still in use cannot be deleted.</summary>
    public string SelectedLayerUsageLabel
    {
        get
        {
            if (SelectedLayer is null) return "Není vybraná vrstva";
            var count = CountObjectsUsingLayer(SelectedLayer);
            return count switch
            {
                0 => "Vrstvu lze odstranit",
                1 => "Vrstvu používá 1 objekt",
                2 or 3 or 4 => $"Vrstvu používají {count} objekty",
                _ => $"Vrstvu používá {count} objektů",
            };
        }
    }

    private void NotifyLayerStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsSelectedLayerRaster));
        this.RaisePropertyChanged(nameof(CanMoveSelectedLayerUp));
        this.RaisePropertyChanged(nameof(CanMoveSelectedLayerDown));
        this.RaisePropertyChanged(nameof(CanReorderLayers));
        this.RaisePropertyChanged(nameof(CanDeleteSelectedLayer));
        this.RaisePropertyChanged(nameof(SelectedLayerUsageLabel));
    }

    /// <summary>Matches Lasero.App's SceneViewModel.CountObjectsUsingLayer/UsesLayer exactly (same
    /// Lasero.Core types), so a layer used by 2 objects here is used by 2 objects there.</summary>
    private int CountObjectsUsingLayer(LayerSettings layer) => Document.Objects.Count(item => UsesLayer(item, layer));

    private static bool UsesLayer(SceneObject item, LayerSettings layer) =>
        (layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty)) ||
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color));

    public ReactiveCommand<Unit, Unit> SelectTool { get; }
    public ReactiveCommand<Unit, Unit> TextTool { get; }
    public ReactiveCommand<Unit, Unit> LineTool { get; }
    public ReactiveCommand<Unit, Unit> ShapeTool { get; }
    public ReactiveCommand<ShapeKind, Unit> SelectShape { get; }
    public ReactiveCommand<Unit, Unit> ZoomIn { get; }
    public ReactiveCommand<Unit, Unit> ZoomOut { get; }
    public ReactiveCommand<Unit, Unit> ResetView { get; }
    public ReactiveCommand<Unit, Unit> CenterView { get; }
    public ReactiveCommand<Unit, Unit> Undo { get; }
    public ReactiveCommand<Unit, Unit> Redo { get; }
    public ReactiveCommand<Unit, Unit> MoveSelectedLayerUp { get; }
    public ReactiveCommand<Unit, Unit> MoveSelectedLayerDown { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedLayer { get; }
    public ReactiveCommand<Unit, Unit> TraceSelectedRaster { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedBackground { get; }
    public ReactiveCommand<Unit, Unit> RestoreSelectedBackground { get; }

    private (double X, double Y) NextCascadeOffset()
    {
        importCount++;
        var offset = CascadeOffsetMm * importCount;
        return (offset, -offset); // down-right cascade in canvas terms = +X, -Y in work coordinates (Y grows up)
    }

    private void PlaceAndAdd(SceneObject obj, IReadOnlyList<LayerSettings> candidateLayers)
    {
        var (offsetX, offsetY) = NextCascadeOffset();
        obj.Transform = obj.Transform with { X = offsetX, Y = offsetY };

        commandStack.Execute(new AddObjectCommand(Document, obj, candidateLayers));
        SelectedObject = obj;
        ActiveTool = EditorTool.Select;
    }

    /// <summary>Matches Lasero.App's SceneViewModel.ImportSvgFile: adds an object, does not replace
    /// the document, with a cascade offset so repeated imports don't stack exactly on top of each
    /// other.</summary>
    public void ImportSvgFile(string path, double targetWidthMm)
    {
        var svgText = File.ReadAllText(path);
        var doc = SvgImporter.Import(svgText, targetWidthMm, Path.GetFileName(path));
        var obj = SceneObjectFactory.FromImportedDocument(doc, doc.SourceFileName ?? Path.GetFileName(path));
        PlaceAndAdd(obj, doc.Layers);
    }

    [SupportedOSPlatform("windows")]
    public void ImportRasterFile(string path, RasterImportOptions options)
    {
        var layer = SceneObjectFactory.CreateRasterLayer(options);
        var obj = SceneObjectFactory.FromRaster(path, options, Path.GetFileName(path), layer);
        PlaceAndAdd(obj, [layer]);
    }

    /// <summary>Constructs the view model the RasterImportWindow dialog binds to — kept here (not in
    /// the view) only because EditorViewModel is what holds the ILaserMachine/AppSettingsStore this
    /// needs; see the fields' own remarks for why.</summary>
    [SupportedOSPlatform("windows")]
    public RasterImportViewModel CreateRasterImportViewModel(string filePath, double targetWidthMm) =>
        new(machine, settingsStore, filePath, targetWidthMm, feedRatePerMinute: 3000, maxPower: 100, dpi: 254);

    [SupportedOSPlatform("windows")]
    public BackgroundRemovalService BackgroundRemovalService => backgroundRemovalService;

    public void ReplaceRasterWithTrace(SceneObject source, BitmapTraceResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        if (!source.IsRaster || !Document.Objects.Contains(source))
            throw new InvalidOperationException("The bitmap is no longer available in the scene.");
        if (result.Document.Shapes.Count == 0)
            throw new InvalidOperationException("Tracing did not produce any vector contours.");

        var traced = SceneObjectFactory.FromImportedDocument(result.Document, $"Trasování · {source.Name}");
        traced.Transform = source.Transform;
        traced.IsVisible = source.IsVisible;
        traced.IncludeInOutput = source.IncludeInOutput;

        commandStack.Execute(new ReplaceObjectsCommand(Document, [source], [traced], result.Document.Layers));
        SelectedObject = traced;
        ActiveTool = EditorTool.Select;
    }

    /// <summary>"Odstranit pozadí" commit — points RasterFilePath at the generated transparent-
    /// background file and remembers the original so "Obnovit pozadí" can point back at it. Matches
    /// Lasero.App's SceneViewModel.CommitBackgroundRemoval exactly (same field semantics).</summary>
    public void CommitBackgroundRemoval(SceneObject item, string removedBackgroundFilePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(removedBackgroundFilePath);
        if (!item.IsRaster || item.HasBackgroundRemoved || !Document.Objects.Contains(item)) return;

        var replacement = WithRasterFile(item, removedBackgroundFilePath, item.RasterFilePath);
        var wasSelected = SelectedObject == item;
        commandStack.Execute(new ReplaceObjectsCommand(Document, [item], [replacement]));
        if (wasSelected) SelectedObject = replacement;
    }

    private void RestoreOriginalBackground()
    {
        if (SelectedObject is not { IsRaster: true, IsLocked: false, HasBackgroundRemoved: true, OriginalRasterFilePath: { } original } item)
            return;

        var replacement = WithRasterFile(item, original, null);
        var wasSelected = SelectedObject == item;
        commandStack.Execute(new ReplaceObjectsCommand(Document, [item], [replacement]));
        if (wasSelected) SelectedObject = replacement;
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

    public Result<SceneObject> AddPrimitive(EditorTool tool, double startX, double startY, double endX, double endY)
    {
        if (tool is not (EditorTool.Rectangle or EditorTool.Line or EditorTool.Shape))
            return Result.Failure<SceneObject>("Aktivní nástroj nevytváří vektorový objekt.");
        if (Math.Abs(endX - startX) < 0.2 && Math.Abs(endY - startY) < 0.2)
            return Result.Failure<SceneObject>("Objekt je příliš malý.");

        var layer = Document.Layers.FirstOrDefault()
            ?? LayerSettings.CreateDefault(new RgbColor(0x17, 0x19, 0x18), LayerMode.Cut, "Čára 1");
        var start = new Position(startX, startY, 0);
        var end = new Position(endX, endY, 0);
        var item = tool switch
        {
            EditorTool.Line => ScenePrimitiveFactory.CreateLine(start, end, layer.Color, "Čára"),
            EditorTool.Rectangle => ScenePrimitiveFactory.CreateRectangle(start, end, layer.Color, "Obdélník"),
            EditorTool.Shape => CreateShape(CurrentShapeTool, start, end, layer.Color),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };

        commandStack.Execute(new AddObjectCommand(Document, item, [layer]));
        SelectedObject = item;
        ActiveTool = EditorTool.Select;
        return Result.Success(item);
    }

    /// <summary>Matches Lasero.App's DesignerPrimitiveFactory shape mapping verbatim (sides/points/
    /// innerRatio and Czech names), so the same drag produces the same geometry in both apps.</summary>
    private static SceneObject CreateShape(ShapeKind kind, Position start, Position end, RgbColor color) => kind switch
    {
        ShapeKind.Rectangle => ScenePrimitiveFactory.CreateRectangle(start, end, color, "Obdélník"),
        ShapeKind.Ellipse => ScenePrimitiveFactory.CreateEllipse(start, end, color, "Elipsa"),
        ShapeKind.Triangle => ScenePrimitiveFactory.CreatePolygon(start, end, 3, color, "Trojúhelník"),
        ShapeKind.Pentagon => ScenePrimitiveFactory.CreatePolygon(start, end, 5, color, "Pětiúhelník"),
        ShapeKind.Hexagon => ScenePrimitiveFactory.CreatePolygon(start, end, 6, color, "Šestiúhelník"),
        ShapeKind.Octagon => ScenePrimitiveFactory.CreatePolygon(start, end, 8, color, "Osmiúhelník"),
        ShapeKind.Star => ScenePrimitiveFactory.CreateStar(start, end, 5, 0.45, color, "Hvězda"),
        ShapeKind.DoubleStar => ScenePrimitiveFactory.CreateStar(start, end, 8, 0.58, color, "Dvojitá hvězda"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
