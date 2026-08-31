using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Machines;
using Lasero.Core.Scene;
using Microsoft.Win32;
using Serilog;

namespace Lasero.App.ViewModels;

public enum ImportKind { None, GCode, Svg, Raster }

/// <summary>Raised only for a real, fully-completed engraving/cutting run — never for a framing pass
/// (RunFraming never raises this). Consumed by MainViewModel to append a JobHistoryEntry.</summary>
public sealed class JobCompletedEventArgs : EventArgs
{
    public required string Name { get; init; }
    public required double DurationSeconds { get; init; }
    public required DateTime CompletedUtc { get; init; }
}

/// <summary>
/// Streaming/job concerns only — loading a raw .gcode file (bypasses the scene entirely, exactly like
/// before) and running/pausing/aborting/framing whatever's in Document. SVG/raster imports now go
/// through SceneViewModel (they become movable/rotatable/scalable SceneObjects, not a one-shot replace);
/// "Prepočítať G-code" combines everything currently in the scene into one Document via RegenerateFromScene.
/// </summary>
public partial class GCodeViewModel : ObservableObject
{
    private readonly ILaserMachine _connection;
    private readonly SceneViewModel _scene;
    private readonly AppSettingsStore _settingsStore;
    private GCodeJobRunner? _activeRunner;
    private bool _sceneDocumentDirty;
    private JobPlacement? _placement;
    private JobTimeEstimate? _timeEstimate;
    private readonly DispatcherTimer _simulationTimer;
    private readonly DispatcherTimer _runTimer;
    private readonly System.Diagnostics.Stopwatch _runStopwatch = new();
    private DateTime _lastSimulationTickUtc;
    private bool _isFramingOperation;

    [ObservableProperty] private GCodeDocument? _document;
    [ObservableProperty] private string _fileLabel = "Žádný soubor";

    partial void OnFileLabelChanged(string value)
    {
        OnPropertyChanged(nameof(JobSourceLabel));
        OnPropertyChanged(nameof(HasNamedJobFile));
    }
    [ObservableProperty] private JobRunState _jobState = JobRunState.Idle;
    [ObservableProperty] private int _currentLine;
    [ObservableProperty] private int _totalLines;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private string? _preflightMessage;
    [ObservableProperty] private bool _isCurrentDocumentFramed;
    [ObservableProperty] private ImportKind _importKind = ImportKind.None;

    /// <summary>The scene's shared per-color layers — proxied here so the existing "VRSTVY" panel binding
    /// in MainWindow.xaml (Binding GCode.Layers) keeps working unchanged now that layers are scene-global.</summary>
    public ObservableCollection<LayerSettings> Layers => _scene.Layers;

    // Shared import sizing (SVG has no physical units — this is the target
    // output width; raster images reuse the same field plus their own DPI/power knobs).
    [ObservableProperty] private double _importWidthMm = 100;
    [ObservableProperty] private double _rasterDpi = 254;
    [ObservableProperty] private double _rasterMaxPower = 100;
    [ObservableProperty] private bool _rasterUseThreshold;
    [ObservableProperty] private double _rasterThreshold = 128;

    [ObservableProperty] private FramingMode _framingMode = FramingMode.FullOutline;
    [ObservableProperty] private double _framingFeedRate = 3000;
    [ObservableProperty] private double _framingPower = 0;
    [ObservableProperty] private JobPlacementMode _placementMode = JobPlacementMode.AbsoluteCoordinates;
    [ObservableProperty] private bool _hasPlacementOrigin;
    [ObservableProperty] private JobOriginAnchor _originAnchor = JobOriginAnchor.TopLeft;
    [ObservableProperty] private double _placementReferenceX;
    [ObservableProperty] private double _placementReferenceY;
    [ObservableProperty] private string _placementLabel = "0,0 odpovídá pracovní nule stroje";

    [ObservableProperty] private TimeSpan _estimatedDuration;
    [ObservableProperty] private TimeSpan _elapsedDuration;
    [ObservableProperty] private TimeSpan _remainingDuration;
    [ObservableProperty] private string _estimatedTimeLabel = "—";
    [ObservableProperty] private string _elapsedTimeLabel = "00:00";
    [ObservableProperty] private string _remainingTimeLabel = "—";

    [ObservableProperty] private bool _isSimulationActive;
    [ObservableProperty] private bool _isSimulationPlaying;
    [ObservableProperty] private double _simulationProgressPercent;
    [ObservableProperty] private double _simulationElapsedSeconds;
    [ObservableProperty] private double _simulationSpeedMultiplier = 10;
    [ObservableProperty] private Position _simulationPosition;
    [ObservableProperty] private int _simulationCompletedSegments;
    [ObservableProperty] private string _simulationTimeLabel = "00:00 / 00:00";

    /// <summary>
    /// What the estimate is actually made of, in the operator's terms: how far the head travels
    /// cutting versus repositioning, and how long each accounts for. Shown with the preview so the
    /// total is inspectable rather than a bare number to be taken on faith.
    /// </summary>
    public string CutBreakdownLabel => _timeEstimate is null
        ? "—"
        : $"{_timeEstimate.CutDistanceMm:N0} mm ({FormatDuration(_timeEstimate.CutDuration)})";

    public string RapidBreakdownLabel => _timeEstimate is null
        ? "—"
        : $"{_timeEstimate.RapidDistanceMm:N0} mm ({FormatDuration(_timeEstimate.RapidDuration)})";

    /// <summary>
    /// Whether there is anything to make. Artwork on the canvas counts, not just a generated
    /// document — the G-code is produced on demand, so panels that keyed off Document alone told an
    /// operator with a rectangle on screen to go and open a design first.
    /// </summary>
    public bool HasJobContent => _scene.Objects.Count > 0 || Document is { Segments.Count: > 0 };

    /// <summary>
    /// What the job is, named for what the operator can see. FileLabel describes the generated
    /// G-code, so before regeneration it read "Žádný soubor" next to a canvas full of artwork.
    /// </summary>
    public const string SceneJobLabel = "Návrh na plátně";

    public string JobSourceLabel => Document is not null
        ? FileLabel
        : _scene.Objects.Count > 0
            ? SceneJobLabel
            : "Žádný návrh";

    /// <summary>
    /// The job came from the canvas rather than an imported file, so naming it adds nothing the
    /// operator cannot already see. The status strip hides its file chip in that case.
    /// </summary>
    public bool HasNamedJobFile => Document is not null && FileLabel != SceneJobLabel;

    public IReadOnlyList<JobOriginAnchor> OriginAnchors { get; } = Enum.GetValues<JobOriginAnchor>();
    public double[] SimulationSpeedPresets { get; } = [1, 2, 5, 10, 20, 50];
    public bool SupportsPlacementMode => ImportKind != ImportKind.GCode;
    public double SimulationDurationSeconds => _timeEstimate?.Duration.TotalSeconds ?? 0;
    public double VisualProgressPercent => IsSimulationActive ? SimulationProgressPercent : ProgressPercent;
    public event Action? SimulationStarted;
    public event Action? PlacementChanged;

    public event EventHandler<JobCompletedEventArgs>? JobCompleted;

    public GCodeViewModel(ILaserMachine connection, SceneViewModel scene, AppSettingsStore settingsStore)
    {
        _connection = connection;
        _scene = scene;
        _settingsStore = settingsStore;
        _connection.Connected += _ => Application.Current.Dispatcher.Invoke(RefreshCommands);
        _connection.StatusUpdated += status => Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshCommands();
            UpdateLivePlacement(status);
        });
        _connection.Disconnected += reason => Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshCommands();
            if (JobState is JobRunState.Running or JobRunState.Paused or JobRunState.Framing)
                LastMessage = reason is null
                    ? "Spojení se zařízením bylo přerušeno. Úloha nebude automaticky obnovena."
                    : $"Spojení se zařízením bylo přerušeno: {reason.Message}. Úloha nebude automaticky obnovena.";
        });
        _scene.Objects.CollectionChanged += (_, _) =>
        {
            RegenerateFromSceneCommand.NotifyCanExecuteChanged();
            PreviewSimulationCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasJobContent));
            OnPropertyChanged(nameof(JobSourceLabel));
        };
        _scene.Changed += OnSceneChanged;

        _simulationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _simulationTimer.Tick += OnSimulationTick;
        _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _runTimer.Tick += (_, _) => UpdateRunTiming();
    }

    [RelayCommand]
    private void LoadFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Podporované soubory (*.gcode;*.nc;*.tap;*.svg;*.png;*.jpg;*.jpeg;*.bmp)|*.gcode;*.nc;*.tap;*.svg;*.png;*.jpg;*.jpeg;*.bmp|" +
                     "G-code (*.gcode;*.nc;*.tap)|*.gcode;*.nc;*.tap|" +
                     "Vektor SVG (*.svg)|*.svg|" +
                     "Obrázek (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|" +
                     "Všechny soubory (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            switch (ext)
            {
                case ".svg":
                    _scene.ImportSvgFile(dialog.FileName, ImportWidthMm);
                    ImportKind = ImportKind.Svg;
                    FileLabel = Path.GetFileName(dialog.FileName);
                    LastMessage = "SVG bylo přidáno do návrhu. Objekt můžete přesunout, otočit nebo změnit jeho velikost.";
                    RegenerateFromScene();
                    break;
                case ".png" or ".jpg" or ".jpeg" or ".bmp":
                    var importViewModel = new RasterImportViewModel(_connection, _settingsStore, dialog.FileName,
                        ImportWidthMm, feedRatePerMinute: 3000, RasterMaxPower, RasterDpi, RasterUseThreshold, RasterThreshold);
                    var importWindow = new RasterImportWindow(importViewModel) { Owner = Application.Current.MainWindow };
                    if (importWindow.ShowDialog() != true) break;

                    _scene.ImportRasterFile(dialog.FileName, importViewModel.BuildOptions());
                    ImportKind = ImportKind.Raster;
                    FileLabel = Path.GetFileName(dialog.FileName);
                    LastMessage = "Obrázek byl převeden do odstínů šedi. Velikost upravíte na plátně, výkon a rychlost v kartě Vrstvy.";
                    RegenerateFromScene();
                    break;
                default:
                    ImportGCodeFile(dialog.FileName);
                    break;
            }
        }
        catch (Exception ex)
        {
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Soubor se nepodařilo načíst",
                ex.Message,
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    private void ImportGCodeFile(string path)
    {
        var lines = File.ReadAllLines(path);
        ImportKind = ImportKind.GCode;
        SetDocument(GCodeParser.Parse(lines, Path.GetFileName(path)), path);
        _sceneDocumentDirty = false;
        LastMessage = $"Načteno: {Document!.Segments.Count} pohybů, rozměr {Document.BoundingBox.Width:0.#} × {Document.BoundingBox.Height:0.#} mm";
    }

    partial void OnImportKindChanged(ImportKind value)
    {
        OnPropertyChanged(nameof(SupportsPlacementMode));
        if (value == ImportKind.GCode && PlacementMode == JobPlacementMode.CurrentPosition)
            PlacementMode = JobPlacementMode.AbsoluteCoordinates;
    }

    private bool CanRegenerate() => _scene.Objects.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private void RegenerateFromScene()
    {
        RefreshEffectivePlacement();
        var baseLines = BuildSceneGCode(0, 0);
        var baseDocument = GCodeParser.Parse(baseLines, "Scéna");
        var offset = _placement?.CalculateOffset(baseDocument.BoundingBox) ?? (0d, 0d);
        var lines = offset == (0d, 0d) ? baseLines : BuildSceneGCode(offset.Item1, offset.Item2);

        // "Scéna" is what this code calls the document internally; it is not a name the operator has
        // ever seen or chosen, and showing it in the status strip and the job panel just put an
        // implementation word where a project name belongs.
        SetDocument(GCodeParser.Parse(lines, SceneJobLabel), SceneJobLabel);
        _sceneDocumentDirty = false;
        UpdatePlacementLabel();
    }

    private List<string> BuildSceneGCode(double offsetX, double offsetY)
    {
        var lines = new List<string>();
        var document = _scene.Scene.ToImportedDocument(offsetX, offsetY);

        // The layer list is the manufacturing order for both vectors and bitmaps. Raster jobs used
        // to be appended after every vector regardless of the order shown in the UI.
        foreach (var layer in Layers.Where(item => item.IsEnabled))
        {
            if (layer.IsRaster)
            {
                foreach (var obj in _scene.Objects.Where(item =>
                             item.IsVisible && item.IncludeInOutput && item.IsRaster && UsesLayer(item, layer)))
                {
                    var outputOptions = obj.BuildRasterOutputOptions(layer, offsetX, offsetY);
                    if (outputOptions is not null)
                        lines.AddRange(RasterImporter.BuildGCode(obj.RasterFilePath!, outputOptions));
                }
                continue;
            }

            var shapes = document.Shapes.Where(shape => shape.LayerId != Guid.Empty
                ? shape.LayerId == layer.Id
                : shape.LayerColor.IsApproximately(layer.Color)).ToList();
            if (shapes.Count == 0) continue;

            lines.AddRange(ToolpathBuilder.BuildGCode(new ImportedDocument
            {
                Shapes = shapes,
                Layers = [layer],
                BoundingBox = document.BoundingBox,
                SourceFileName = document.SourceFileName,
            }));
        }

        return lines;
    }

    private static bool UsesLayer(SceneObject item, LayerSettings layer) =>
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color)) ||
        layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty);

    private void OnSceneChanged()
    {
        if (ImportKind == ImportKind.GCode) return;
        _sceneDocumentDirty = true;
        IsCurrentDocumentFramed = false;
    }

    private void EnsureSceneDocumentCurrent(bool refreshCurrentPosition = false)
    {
        if (_scene.Objects.Count == 0)
            return;

        if (_sceneDocumentDirty ||
            (refreshCurrentPosition && PlacementMode == JobPlacementMode.CurrentPosition))
            RegenerateFromScene();
    }

    private void SetDocument(GCodeDocument document, string pathOrLabel)
    {
        Document = document;
        FileLabel = Path.GetFileName(pathOrLabel);
        TotalLines = document.RawLines.Count;
        CurrentLine = 0;
        ProgressPercent = 0;
        JobState = document.Segments.Count > 0 ? JobRunState.Ready : JobRunState.Idle;
        _timeEstimate = JobTimeEstimator.Estimate(document);
        OnPropertyChanged(nameof(CutBreakdownLabel));
        OnPropertyChanged(nameof(RapidBreakdownLabel));
        EstimatedDuration = _timeEstimate.Duration;
        EstimatedTimeLabel = FormatDuration(EstimatedDuration);
        RemainingDuration = EstimatedDuration;
        RemainingTimeLabel = EstimatedTimeLabel;
        OnPropertyChanged(nameof(SimulationDurationSeconds));
        StopSimulation(reset: true);
    }

    public void ClearDocument()
    {
        Document = null;
        FileLabel = "Žádný soubor";
        CurrentLine = 0;
        TotalLines = 0;
        ProgressPercent = 0;
        JobState = JobRunState.Idle;
        LastMessage = null;
        _timeEstimate = null;
        EstimatedDuration = TimeSpan.Zero;
        EstimatedTimeLabel = "—";
        ClearPlacementOrigin();
        StopSimulation(reset: true);
        RefreshCommands();
    }

    private bool IsMachineReadyForPhysicalAction() =>
        _connection.State == GrblConnectionState.Connected
        && _connection.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2)
        && _connection.LastStatus?.Mode == GrblMachineMode.Idle
        && _connection.ActiveAlert is null;

    private bool CanRun() => Document is not null
        && IsMachineReadyForPhysicalAction()
        && JobState is not (JobRunState.Preparing or JobRunState.Framing or JobRunState.Running or JobRunState.Paused);

    /// <summary>
    /// Why Start/Frame is currently unavailable, in the operator's words, or null when it is available.
    ///
    /// The preflight already produced these sentences — but only inside the command bodies, which never
    /// run while the button is disabled. So the one moment the operator needs the reason was the one
    /// moment it was unreachable. These read the same <see cref="JobPreflight"/> result and change no
    /// gating: CanRun/CanFrame decide availability exactly as before.
    /// </summary>
    [ObservableProperty] private string? _startBlockedReason;
    [ObservableProperty] private string? _frameBlockedReason;

    /// <summary>True while the machine is actually working on something, so progress UI can appear
    /// only then instead of showing a permanent empty 0% track.</summary>
    public bool IsJobActive => JobState is JobRunState.Preparing or JobRunState.Framing
        or JobRunState.Running or JobRunState.Paused;

    public string StartActionTooltip => StartBlockedReason ?? "Spustit připravenou úlohu";
    public string FrameActionTooltip => FrameBlockedReason ?? "Ověřit obrys a umístění úlohy před spuštěním";

    partial void OnJobStateChanged(JobRunState value) => OnPropertyChanged(nameof(IsJobActive));

    partial void OnStartBlockedReasonChanged(string? value) => OnPropertyChanged(nameof(StartActionTooltip));
    partial void OnFrameBlockedReasonChanged(string? value) => OnPropertyChanged(nameof(FrameActionTooltip));

    private void RefreshBlockedReasons()
    {
        StartBlockedReason = CanRun() ? null : DescribeBlockedAction(EvaluatePreflight());
        FrameBlockedReason = CanFrame()
            ? null
            : DescribeBlockedAction(EvaluatePreflight(includeFramingRequirement: false));
    }

    private string DescribeBlockedAction(JobPreflightResult preflight)
    {
        if (preflight.FirstBlockingIssue is { } blocking) return blocking.Message;
        return JobState switch
        {
            JobRunState.Preparing => "Úloha se právě připravuje.",
            JobRunState.Framing => "Právě probíhá rámování.",
            JobRunState.Running => "Úloha už běží.",
            JobRunState.Paused => "Úloha je pozastavená. Pokračujte v ní, nebo ji zastavte.",
            _ => "Úlohu zatím nelze spustit.",
        };
    }

    public JobPreflightResult EvaluatePreflight(bool includeFramingRequirement = true) => JobPreflight.Evaluate(new JobPreflightContext
    {
        IsConnected = _connection.State == GrblConnectionState.Connected,
        Document = Document,
        MachineStatus = _connection.LastStatus,
        MachineStatusAge = _connection.LastStatusReceivedUtc is { } timestamp ? DateTime.UtcNow - timestamp : null,
        RequireFraming = includeFramingRequirement && _settingsStore.Current.Safety.RequireFramingBeforeStart,
        HasFramedCurrentDocument = IsCurrentDocumentFramed,
        WorkAreaWidthMm = _settingsStore.Current.Machine.WorkAreaWidthMm,
        WorkAreaHeightMm = _settingsStore.Current.Machine.WorkAreaHeightMm,
        Layers = ImportKind == ImportKind.GCode ? null : Layers.ToList(),
        RasterOptions = BuildEffectiveRasterOptionsForPreflight(),
    });

    private IReadOnlyList<RasterImportOptions>? BuildEffectiveRasterOptionsForPreflight()
    {
        if (ImportKind == ImportKind.GCode) return null;
        return _scene.Objects
            .Where(item => item.IsVisible && item.IncludeInOutput && item.RasterOptions is not null)
            .Select(item =>
            {
                var layer = Layers.FirstOrDefault(candidate => candidate.IsRaster && UsesLayer(item, candidate));
                return layer is { IsEnabled: true } ? item.BuildRasterOutputOptions(layer) : null;
            })
            .Where(item => item is not null)
            .Cast<RasterImportOptions>()
            .ToList();
    }

    private void ClearPlacementOrigin()
    {
        PlacementMode = JobPlacementMode.AbsoluteCoordinates;
    }

    partial void OnPlacementModeChanged(JobPlacementMode value)
    {
        HasPlacementOrigin = value == JobPlacementMode.CurrentPosition;
        IsCurrentDocumentFramed = false;
        RefreshEffectivePlacement();
        if (_scene.Objects.Count > 0 && ImportKind != ImportKind.GCode)
            RegenerateFromScene();
        PlacementChanged?.Invoke();
        LastMessage = value == JobPlacementMode.CurrentPosition
            ? "Úloha začne v aktuální poloze laseru. Polohu můžete změnit šipkami před rámováním nebo spuštěním."
            : "Úloha používá absolutní souřadnice návrhu. Bod 0,0 odpovídá pracovní nule stroje.";
    }

    partial void OnOriginAnchorChanged(JobOriginAnchor value)
    {
        if (PlacementMode != JobPlacementMode.CurrentPosition) return;
        RefreshEffectivePlacement();
        if (_scene.Objects.Count > 0 && ImportKind != ImportKind.GCode)
            RegenerateFromScene();
        PlacementChanged?.Invoke();
    }

    private void UpdatePlacementLabel()
    {
        PlacementLabel = PlacementMode switch
        {
            JobPlacementMode.AbsoluteCoordinates => "0,0 odpovídá pracovní nule stroje",
            JobPlacementMode.CurrentPosition when _connection.LastStatus is null =>
                "Připojte zařízení pro načtení aktuální polohy",
            _ => $"{OriginAnchorLabel(OriginAnchor)}: X {PlacementReferenceX:0.###} · Y {PlacementReferenceY:0.###} mm",
        };
    }

    private void RefreshEffectivePlacement()
    {
        if (PlacementMode != JobPlacementMode.CurrentPosition)
        {
            _placement = null;
            PlacementReferenceX = 0;
            PlacementReferenceY = 0;
            UpdatePlacementLabel();
            return;
        }

        if (_connection.LastStatus is { } status)
        {
            PlacementReferenceX = status.WorkPosition.X;
            PlacementReferenceY = status.WorkPosition.Y;
            _placement = new JobPlacement(status.WorkPosition, OriginAnchor);
        }
        else
        {
            _placement = null;
        }

        UpdatePlacementLabel();
    }

    private void UpdateLivePlacement(MachineStatus status)
    {
        if (PlacementMode != JobPlacementMode.CurrentPosition ||
            JobState is JobRunState.Preparing or JobRunState.Framing or JobRunState.Running or JobRunState.Paused)
            return;

        var moved = Math.Abs(PlacementReferenceX - status.WorkPosition.X) > 0.001 ||
                    Math.Abs(PlacementReferenceY - status.WorkPosition.Y) > 0.001;
        PlacementReferenceX = status.WorkPosition.X;
        PlacementReferenceY = status.WorkPosition.Y;
        _placement = new JobPlacement(status.WorkPosition, OriginAnchor);
        UpdatePlacementLabel();
        if (moved)
            IsCurrentDocumentFramed = false;
    }

    private static string OriginAnchorLabel(JobOriginAnchor anchor) => anchor switch
    {
        JobOriginAnchor.TopLeft => "Levý horní roh",
        JobOriginAnchor.Center => "Střed",
        JobOriginAnchor.TopRight => "Pravý horní roh",
        JobOriginAnchor.BottomLeft => "Levý dolní roh",
        JobOriginAnchor.BottomRight => "Pravý dolní roh",
        _ => "Referenční bod",
    };

    public void RestorePlacement(ProjectJobPlacement? placement)
    {
        if (placement is null)
        {
            _placement = null;
            HasPlacementOrigin = false;
            PlacementMode = JobPlacementMode.AbsoluteCoordinates;
            UpdatePlacementLabel();
            return;
        }

        OriginAnchor = placement.Anchor;
        PlacementReferenceX = placement.ReferenceX;
        PlacementReferenceY = placement.ReferenceY;
        PlacementMode = placement.Mode;
        HasPlacementOrigin = PlacementMode == JobPlacementMode.CurrentPosition;
        _placement = HasPlacementOrigin
            ? new JobPlacement(new Position(placement.ReferenceX, placement.ReferenceY, 0), placement.Anchor)
            : null;
        UpdatePlacementLabel();
    }

    private bool CanPreviewSimulation() => (_scene.Objects.Count > 0 || Document is { Segments.Count: > 0 })
        && JobState is not (JobRunState.Running or JobRunState.Paused or JobRunState.Framing);

    [RelayCommand(CanExecute = nameof(CanPreviewSimulation))]
    private void PreviewSimulation()
    {
        EnsureSceneDocumentCurrent(refreshCurrentPosition: true);
        if (Document is null || _timeEstimate is null) return;

        if (SimulationProgressPercent >= 100) SetSimulationTime(TimeSpan.Zero);
        IsSimulationActive = true;
        IsSimulationPlaying = true;
        _lastSimulationTickUtc = DateTime.UtcNow;
        _simulationTimer.Start();
        SimulationStarted?.Invoke();
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void PauseResumeSimulation()
    {
        if (!IsSimulationActive) return;
        IsSimulationPlaying = !IsSimulationPlaying;
        _lastSimulationTickUtc = DateTime.UtcNow;
    }

    private bool CanPauseSimulation() => IsSimulationActive;

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void RestartSimulation()
    {
        if (Document is null || _timeEstimate is null) return;
        SetSimulationTime(TimeSpan.Zero);
        IsSimulationPlaying = true;
        _lastSimulationTickUtc = DateTime.UtcNow;
    }

    [RelayCommand(CanExecute = nameof(CanPauseSimulation))]
    private void CloseSimulation() => StopSimulation(reset: true);

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        if (!IsSimulationActive || !IsSimulationPlaying || _timeEstimate is null) return;
        var now = DateTime.UtcNow;
        var delta = now - _lastSimulationTickUtc;
        _lastSimulationTickUtc = now;
        var next = TimeSpan.FromSeconds(SimulationElapsedSeconds + delta.TotalSeconds * Math.Max(0.1, SimulationSpeedMultiplier));
        SetSimulationTime(next);
        if (next >= _timeEstimate.Duration)
        {
            IsSimulationPlaying = false;
            _simulationTimer.Stop();
        }
    }

    partial void OnSimulationProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(VisualProgressPercent));
        if (!IsSimulationActive || _timeEstimate is null) return;
        var target = TimeSpan.FromSeconds(_timeEstimate.Duration.TotalSeconds * Math.Clamp(value, 0, 100) / 100);
        if (Math.Abs(target.TotalSeconds - SimulationElapsedSeconds) > 0.05)
            ApplySimulationState(JobSimulator.GetStateAtTime(Document!, _timeEstimate, target));
    }

    partial void OnIsSimulationActiveChanged(bool value) => OnPropertyChanged(nameof(VisualProgressPercent));

    private void SetSimulationTime(TimeSpan time)
    {
        if (Document is null || _timeEstimate is null) return;
        ApplySimulationState(JobSimulator.GetStateAtTime(Document, _timeEstimate, time));
    }

    private void ApplySimulationState(JobSimulationState state)
    {
        SimulationPosition = state.LaserPosition;
        SimulationCompletedSegments = state.CompletedSegments;
        SimulationElapsedSeconds = state.Elapsed.TotalSeconds;
        var percent = state.Progress * 100;
        if (Math.Abs(SimulationProgressPercent - percent) > 0.001)
            SimulationProgressPercent = percent;
        SimulationTimeLabel = $"{FormatDuration(state.Elapsed)} / {FormatDuration(state.Duration)}";
    }

    private void StopSimulation(bool reset)
    {
        _simulationTimer.Stop();
        IsSimulationPlaying = false;
        IsSimulationActive = false;
        if (reset)
        {
            SimulationProgressPercent = 0;
            SimulationElapsedSeconds = 0;
            SimulationCompletedSegments = 0;
            SimulationPosition = Position.Zero;
            SimulationTimeLabel = $"00:00 / {FormatDuration(EstimatedDuration)}";
        }
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunJob()
    {
        StopSimulation(reset: false);
        JobState = JobRunState.Preparing;
        RefreshCommands();
        // A completed frame has already verified this exact placement. Keep it
        // pinned for the following run instead of reading the frame endpoint.
        EnsureSceneDocumentCurrent(refreshCurrentPosition: !IsCurrentDocumentFramed);
        if (Document is null)
        {
            JobState = JobRunState.Idle;
            RefreshCommands();
            return;
        }
        var preflight = EvaluatePreflight();
        if (!preflight.CanStart)
        {
            JobState = JobRunState.Ready;
            PreflightMessage = preflight.FirstBlockingIssue?.Message;
            LastMessage = PreflightMessage;
            RefreshCommands();
            return;
        }

        var enabledLayers = Layers.Where(layer => layer.IsEnabled).ToList();
        var settingsSummary = enabledLayers.Count switch
        {
            0 => "Parametry jsou součástí načteného G-code.",
            1 => $"Výkon: {enabledLayers[0].Power:0.#} %\nRychlost: {enabledLayers[0].Speed:0.#} mm/min\n" +
                 (enabledLayers[0].Mode is LayerMode.Fill or LayerMode.FillAndCut ? $"Rozestup řádků: {enabledLayers[0].FillLineIntervalMm:0.###} mm\n" : string.Empty) +
                 $"Průchody: {enabledLayers[0].Passes}",
            _ => $"Aktivní vrstvy: {enabledLayers.Count} (parametry se liší podle vrstvy)",
        };
        var confirmed = LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
            "Spustit gravírování",
            $"Úloha: {FileLabel}\n" +
            $"Rozměr: {Document.BoundingBox.Width:0.#} × {Document.BoundingBox.Height:0.#} mm\n" +
            $"{settingsSummary}\nOdhadovaný čas: {EstimatedTimeLabel}\n\n" +
            "Před spuštěním zkontrolujte materiál, odsávání a ochranný kryt.",
            "Spustit úlohu",
            CancelText: "Ještě zkontrolovat",
            Tone: LaseroDialogTone.Warning)) == LaseroDialogChoice.Primary;
        if (!confirmed)
        {
            JobState = JobRunState.Ready;
            LastMessage = "Spuštění bylo zrušeno.";
            RefreshCommands();
            return;
        }

        PreflightMessage = null;
        _runStopwatch.Restart();
        ElapsedDuration = TimeSpan.Zero;
        RemainingDuration = EstimatedDuration;
        UpdateRunTiming();
        _runTimer.Start();
        SaveLastEngravingSettings();
        var label = FileLabel;
        try
        {
            await RunLinesAsync(Document.RawLines, abortOnError: true);
        }
        finally
        {
            _runTimer.Stop();
            _runStopwatch.Stop();
            UpdateRunTiming();
        }

        if (JobState == JobRunState.Completed)
        {
            ProgressPercent = 100;
            RemainingDuration = TimeSpan.Zero;
            RemainingTimeLabel = FormatDuration(RemainingDuration);
            LastMessage = $"Gravírování bylo dokončeno.\nSkutečný čas: {FormatDuration(ElapsedDuration)}\n{settingsSummary}";
            JobCompleted?.Invoke(this, new JobCompletedEventArgs
            {
                Name = label,
                DurationSeconds = _runStopwatch.Elapsed.TotalSeconds,
                CompletedUtc = DateTime.UtcNow,
            });
        }
    }

    private bool CanFrame() => Document is not null
        && !Document.BoundingBox.IsEmpty
        && IsMachineReadyForPhysicalAction()
        && JobState is not (JobRunState.Preparing or JobRunState.Framing or JobRunState.Running or JobRunState.Paused);

    [RelayCommand(CanExecute = nameof(CanFrame))]
    private async Task RunFraming()
    {
        EnsureSceneDocumentCurrent(refreshCurrentPosition: true);
        if (Document is null) return;
        var preflight = EvaluatePreflight(includeFramingRequirement: false);
        if (!preflight.CanStart)
        {
            PreflightMessage = preflight.FirstBlockingIssue?.Message;
            LastMessage = PreflightMessage;
            return;
        }

        IsCurrentDocumentFramed = false;
        _isFramingOperation = true;
        JobState = JobRunState.Framing;
        RefreshCommands();
        var options = new FramingOptions
        {
            FeedRatePerMinute = FramingFeedRate,
            Mode = FramingMode,
            LaserPower = 0,
        };
        var frameLines = FramingService.BuildFrameGCode(Document.BoundingBox, options).ToList();
        if (PlacementMode == JobPlacementMode.CurrentPosition)
        {
            frameLines.Add("M5");
            frameLines.Add(FormattableString.Invariant(
                $"G0 X{PlacementReferenceX:0.###} Y{PlacementReferenceY:0.###}"));
        }
        try
        {
            await RunLinesAsync(frameLines, abortOnError: true);
            IsCurrentDocumentFramed = _activeRunner?.State == JobRunState.Completed;
        }
        finally
        {
            _isFramingOperation = false;
            JobState = IsCurrentDocumentFramed ? JobRunState.Ready : _activeRunner?.State ?? JobRunState.Error;
            LastMessage = IsCurrentDocumentFramed
                ? "Rámování bylo dokončeno. Umístění úlohy je ověřené."
                : "Rámování nebylo dokončeno.";
            RefreshCommands();
        }
    }

    private async Task RunLinesAsync(IReadOnlyList<string> lines, bool abortOnError)
    {
        var runner = new GCodeJobRunner(_connection);
        _activeRunner = runner;

        runner.StateChanged += state => Application.Current.Dispatcher.Invoke(() =>
        {
            JobState = _isFramingOperation && state == JobRunState.Running ? JobRunState.Framing : state;
            RefreshCommands();
        });
        runner.ProgressChanged += (current, total) => Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentLine = current;
            TotalLines = total;
            ProgressPercent = total == 0 ? 0 : 100.0 * current / total;
            OnPropertyChanged(nameof(VisualProgressPercent));
            UpdateRunTiming();
        });
        runner.LineFailed += (line, message) => Application.Current.Dispatcher.Invoke(() =>
            LastMessage = $"Řádek {line + 1}: {message}");

        await runner.RunAsync(lines, abortOnError);
    }

    private bool CanPauseResume() => _activeRunner?.State is JobRunState.Running or JobRunState.Paused;

    [RelayCommand(CanExecute = nameof(CanPauseResume))]
    private void PauseResume()
    {
        if (_activeRunner is null) return;
        if (_activeRunner.State == JobRunState.Running) _activeRunner.Pause();
        else if (_activeRunner.State == JobRunState.Paused) _activeRunner.Resume();
    }

    [RelayCommand(CanExecute = nameof(CanPauseResume))]
    private void Abort() => _activeRunner?.Abort();

    private void RefreshCommands()
    {
        RunJobCommand.NotifyCanExecuteChanged();
        RunFramingCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        PreviewSimulationCommand.NotifyCanExecuteChanged();
        PauseResumeSimulationCommand.NotifyCanExecuteChanged();
        RestartSimulationCommand.NotifyCanExecuteChanged();
        CloseSimulationCommand.NotifyCanExecuteChanged();
        RefreshBlockedReasons();
    }

    partial void OnDocumentChanged(GCodeDocument? value)
    {
        IsCurrentDocumentFramed = false;
        PreflightMessage = null;
        OnPropertyChanged(nameof(HasJobContent));
        OnPropertyChanged(nameof(JobSourceLabel));
        RefreshCommands();
    }

    private void UpdateRunTiming()
    {
        ElapsedDuration = _runStopwatch.Elapsed;
        ElapsedTimeLabel = FormatDuration(ElapsedDuration);
        var fraction = TotalLines <= 0 ? 0 : Math.Clamp((double)CurrentLine / TotalLines, 0, 1);
        RemainingDuration = fraction <= 0
            ? EstimatedDuration
            : TimeSpan.FromSeconds(Math.Max(0, ElapsedDuration.TotalSeconds / fraction - ElapsedDuration.TotalSeconds));
        RemainingTimeLabel = FormatDuration(RemainingDuration);
    }

    private void SaveLastEngravingSettings()
    {
        var profileId = _settingsStore.Current.Machine.ActiveProfileId;
        var layer = Layers.FirstOrDefault(item => item.IsEnabled);
        if (string.IsNullOrWhiteSpace(profileId) || layer is null ||
            !_settingsStore.Current.Machine.Profiles.TryGetValue(profileId, out var profile)) return;

        profile.LastPowerPercent = layer.Power;
        profile.LastSpeedMmPerMinute = layer.Speed;
        profile.LastLineIntervalMm = layer.FillLineIntervalMm;
        profile.LastPasses = layer.Passes;
        _settingsStore.Save();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
