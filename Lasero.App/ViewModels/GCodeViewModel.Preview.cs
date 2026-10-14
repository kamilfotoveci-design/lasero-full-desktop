using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Machines;
using Serilog;

namespace Lasero.App.ViewModels;

/// <summary>
/// The job preview ("Náhled"). It builds the toolpath from the scene on its own, without touching
/// <see cref="Document"/>, the job state or the connection, so it works with no machine attached and can never
/// be mistaken for, or interfere with, Start, Frame or the stream to the controller.
/// </summary>
public partial class GCodeViewModel
{
    /// <summary>GRBL's default $30. Only scales S values for the preview when no controller has reported its own.</summary>
    public const double PreviewFallbackMaximumS = 1000;

    /// <summary>The preview is ready to be shown. Raised on the UI thread by <see cref="OpenPreviewCommand"/>.</summary>
    public event Action<JobPreviewModel>? PreviewRequested;

    /// <summary>The preview cannot be built (title, reason). The shell shows the reason in a dialog.</summary>
    public event Action<string, string>? PreviewBlocked;

    private bool CanOpenPreview() => HasJobContent;

    [RelayCommand(CanExecute = nameof(CanOpenPreview))]
    private void OpenPreview()
    {
        if (TryBuildPreview(out var preview, out var reason) && preview is not null)
            PreviewRequested?.Invoke(preview);
        else
            PreviewBlocked?.Invoke("Náhled není k dispozici", reason ?? "Dráhu úlohy se nepodařilo připravit.");
    }

    /// <summary>
    /// Builds the preview model for whatever the next job would make: the imported G-code as is, otherwise the
    /// scene, generated the same way as for a real job (same layers, order and placement) but scaled to the
    /// controller's $30 when known and to <see cref="PreviewFallbackMaximumS"/> otherwise.
    /// </summary>
    public bool TryBuildPreview(out JobPreviewModel? preview, out string? reason)
    {
        preview = null;
        reason = null;
        try
        {
            if (ImportKind == ImportKind.GCode && Document is { Segments.Count: > 0 } imported)
            {
                var strongest = imported.Segments.Max(segment => segment.Power);
                var rawMaximum = strongest > 0 ? strongest : PreviewFallbackMaximumS;
                preview = JobPreviewModel.Create(
                    imported,
                    [new PreviewOperation("Importovaný G-code", FileLabel, PreviewPalette.Graphite)],
                    [new PreviewLineSpan(0, 0, imported.RawLines.Count)],
                    rawMaximum,
                    PreviewNotes(JobPreflight.Evaluate(PreviewContext(imported, rawMaximum, isRaw: true))),
                    title: FileLabel);
                return true;
            }

            if (_scene.Objects.Count == 0)
            {
                reason = "Na plátně není nic, co by šlo zobrazit. Lze přidat text nebo tvar, případně importovat grafiku.";
                return false;
            }

            var maximumS = ReadControllerMaximumS() is { } known && double.IsFinite(known) && known > 0
                ? known
                : PreviewFallbackMaximumS;

            var spans = new List<(LayerSettings Layer, int First, int Count)>();
            var lines = BuildSceneGCode(0, 0, maximumS, spans);
            if (_placement is not null && lines.Count > 0)
            {
                var bounds = GCodeParser.Parse(lines, SceneJobLabel).BoundingBox;
                if (!bounds.IsEmpty && _placement.CalculateOffset(bounds) is (var offsetX, var offsetY) && (offsetX != 0 || offsetY != 0))
                {
                    spans.Clear();
                    lines = BuildSceneGCode(offsetX, offsetY, maximumS, spans);
                }
            }

            var document = GCodeParser.Parse(lines, SceneJobLabel);
            var preflight = JobPreflight.Evaluate(PreviewContext(document, maximumS, isRaw: false));
            if (preflight.Issues.FirstOrDefault(issue =>
                    issue.Code is "job.empty" or "job.no-motion" || issue.Code.StartsWith("settings.", StringComparison.Ordinal)) is { } blocking)
            {
                reason = blocking.Message;
                return false;
            }

            var operations = new List<PreviewOperation>();
            var operationOf = new Dictionary<Guid, int>();
            var lineSpans = new List<PreviewLineSpan>(spans.Count);
            foreach (var (layer, first, count) in spans)
            {
                if (!operationOf.TryGetValue(layer.Id, out var index))
                {
                    index = operations.Count;
                    operationOf[layer.Id] = index;
                    operations.Add(new PreviewOperation(
                        string.IsNullOrWhiteSpace(layer.Name) ? layer.ModeLabel : layer.Name,
                        layer.ProcessingSummary,
                        layer.Color));
                }

                lineSpans.Add(new PreviewLineSpan(index, first, count));
            }

            preview = JobPreviewModel.Create(document, operations, lineSpans, maximumS, PreviewNotes(preflight), title: SceneJobLabel);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Job preview could not be built");
            reason = "Dráhu úlohy se nepodařilo vypočítat. Lze zkusit upravit návrh, případně ověřit, že zdrojové obrázky jsou dostupné.";
            preview = null;
            return false;
        }
    }

    /// <summary>
    /// The geometry and settings half of the preflight. Everything that is about the machine (connection,
    /// laser mode, framing) is switched off: a preview does not need one, and the Start button keeps asking.
    /// </summary>
    private JobPreflightContext PreviewContext(GCodeDocument document, double maximumS, bool isRaw)
    {
        var machine = _settingsStore.Current.Machine;
        var profile = (_connection as IGrblDeviceProfileSource)?.DeviceProfile;
        return new JobPreflightContext
        {
            IsConnected = false,
            Document = document,
            MachineStatus = null,
            RequireFraming = false,
            HasFramedCurrentDocument = false,
            WorkAreaWidthMm = machine.WorkAreaWidthMm,
            WorkAreaHeightMm = machine.WorkAreaHeightMm,
            MachineMaxTravelXmm = profile?.MaxTravelXmm,
            MachineMaxTravelYmm = profile?.MaxTravelYmm,
            Layers = isRaw ? null : Layers.ToList(),
            HiddenOutputLayerNames = isRaw ? null : HiddenOutputLayerNames(),
            RasterOptions = isRaw ? null : BuildEffectiveRasterOptionsForPreflight(),
            MaxSpindleSpeed = maximumS,
            LaserModeEnabled = true,
            IsRawGCode = false,
        };
    }

    private static IReadOnlyList<string> PreviewNotes(JobPreflightResult preflight) =>
        preflight.Issues
            .Where(issue => issue.Code is "job.outside-work-area" or "job.exceeds-reported-machine-travel" or "job.invalid-bounds"
                or JobPreflight.HiddenOutputLayerCode)
            .Select(issue => issue.Message)
            .ToList();
}
