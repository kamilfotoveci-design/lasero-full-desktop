using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Import;

namespace Lasero.Core.Jobs;

public enum PreflightSeverity
{
    Warning,
    Blocking,
}

public sealed record PreflightIssue(string Code, string Message, PreflightSeverity Severity);

public sealed record JobPreflightContext
{
    public required bool IsConnected { get; init; }
    public required GCodeDocument? Document { get; init; }
    public required MachineStatus? MachineStatus { get; init; }
    public TimeSpan? MachineStatusAge { get; init; }
    public required bool RequireFraming { get; init; }
    public required bool HasFramedCurrentDocument { get; init; }
    public required double WorkAreaWidthMm { get; init; }
    public required double WorkAreaHeightMm { get; init; }
    public IReadOnlyList<LayerSettings>? Layers { get; init; }
    public IReadOnlyList<RasterImportOptions>? RasterOptions { get; init; }
}

public sealed record JobPreflightResult(IReadOnlyList<PreflightIssue> Issues)
{
    public bool CanStart => Issues.All(issue => issue.Severity != PreflightSeverity.Blocking);
    public PreflightIssue? FirstBlockingIssue => Issues.FirstOrDefault(issue => issue.Severity == PreflightSeverity.Blocking);
}

public static class JobPreflight
{
    public static JobPreflightResult Evaluate(JobPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<PreflightIssue>();

        if (!context.IsConnected)
            issues.Add(Block("device.disconnected", "Gravírovací zařízení není připojeno."));

        if (context.Document is null || context.Document.RawLines.Count == 0)
            issues.Add(Block("job.empty", "Projekt neobsahuje žádnou úlohu ke spuštění."));
        else if (context.Document.BoundingBox.IsEmpty)
            issues.Add(Block("job.no-motion", "Úloha neobsahuje žádnou vykreslitelnou dráhu."));
        else
            ValidateBounds(context, issues);

        ValidateEngravingSettings(context.Layers, issues);
        ValidateRasterSettings(context.RasterOptions, issues);

        if (context.IsConnected)
        {
            if (context.MachineStatus is null)
            {
                issues.Add(Block("machine.no-status", "Čekám na aktuální stav zařízení."));
            }
            else
            {
                if (context.MachineStatusAge is { } age && age > TimeSpan.FromSeconds(2))
                    issues.Add(Block("machine.stale-status", "Stav zařízení není aktuální. Zkontrolujte připojení."));
                ValidateMachineState(context.MachineStatus, issues);
            }
        }

        if (context.RequireFraming && !context.HasFramedCurrentDocument)
            issues.Add(Block("job.framing-required", "Před spuštěním zkontrolujte umístění pomocí rámování."));

        return new JobPreflightResult(issues);
    }

    private static void ValidateBounds(JobPreflightContext context, ICollection<PreflightIssue> issues)
    {
        if (context.WorkAreaWidthMm <= 0 || context.WorkAreaHeightMm <= 0)
        {
            issues.Add(Block("machine.invalid-work-area", "Rozměry pracovní plochy nejsou platné."));
            return;
        }

        var bounds = context.Document!.BoundingBox;
        const double tolerance = 0.001;
        if (bounds.MinX < -tolerance || bounds.MinY < -tolerance
            || bounds.MaxX > context.WorkAreaWidthMm + tolerance
            || bounds.MaxY > context.WorkAreaHeightMm + tolerance)
        {
            issues.Add(Block(
                "job.outside-work-area",
                $"Dráha přesahuje pracovní plochu {context.WorkAreaWidthMm:0.#} × {context.WorkAreaHeightMm:0.#} mm."));
        }
    }

    private static void ValidateMachineState(MachineStatus status, ICollection<PreflightIssue> issues)
    {
        if (status.Mode != GrblMachineMode.Idle)
        {
            var message = status.Mode switch
            {
                GrblMachineMode.Alarm => "Zařízení je v alarmu. Nejprve odstraňte příčinu a zařízení odemkněte.",
                GrblMachineMode.Door => "Bezpečnostní kryt nebo dveře jsou otevřené.",
                GrblMachineMode.Hold => "Zařízení je pozastavené.",
                GrblMachineMode.Run or GrblMachineMode.Jog or GrblMachineMode.Home => "Zařízení se právě pohybuje.",
                _ => $"Zařízení není připravené (stav {status.Mode}).",
            };
            issues.Add(Block("machine.not-idle", message));
        }

        var pins = status.TriggeredPins ?? string.Empty;
        if (pins.IndexOfAny(['X', 'Y', 'Z']) >= 0)
            issues.Add(Block("machine.limit-triggered", "Je aktivní koncový spínač některé osy."));
        if (pins.Contains('D'))
            issues.Add(Block("machine.door-triggered", "Je aktivní vstup bezpečnostních dveří."));
        if (pins.Contains('P'))
            issues.Add(new PreflightIssue("machine.probe-triggered", "Je aktivní vstup sondy.", PreflightSeverity.Warning));
    }

    private static void ValidateEngravingSettings(IReadOnlyList<LayerSettings>? layers, ICollection<PreflightIssue> issues)
    {
        if (layers is null || layers.Count == 0) return;

        foreach (var layer in layers.Where(item => item.IsEnabled))
        {
            var prefix = string.IsNullOrWhiteSpace(layer.Name) ? "Vrstva" : $"Vrstva „{layer.Name}“";
            if (!double.IsFinite(layer.Power) || layer.Power is <= 0 or > 100)
                issues.Add(Block("settings.invalid-power", $"{prefix}: výkon musí být v rozsahu 1–100 %."));
            if (!double.IsFinite(layer.Speed) || layer.Speed <= 0)
                issues.Add(Block("settings.invalid-speed", $"{prefix}: rychlost musí být kladné číslo v mm/min."));
            if (layer.Passes < 1)
                issues.Add(Block("settings.invalid-passes", $"{prefix}: počet průchodů musí být alespoň 1."));
            if (layer.Mode is LayerMode.Fill or LayerMode.FillAndCut &&
                (!double.IsFinite(layer.FillLineIntervalMm) || layer.FillLineIntervalMm <= 0))
                issues.Add(Block("settings.invalid-interval", $"{prefix}: rozestup řádků musí být kladné číslo v mm."));
        }
    }

    private static void ValidateRasterSettings(IReadOnlyList<RasterImportOptions>? options, ICollection<PreflightIssue> issues)
    {
        if (options is null) return;
        foreach (var item in options)
        {
            if (!double.IsFinite(item.MaxPower) || item.MaxPower is <= 0 or > 100)
                issues.Add(Block("settings.invalid-power", "Rastrový obrázek: výkon musí být v rozsahu 1–100 %."));
            if (!double.IsFinite(item.FeedRatePerMinute) || item.FeedRatePerMinute <= 0)
                issues.Add(Block("settings.invalid-speed", "Rastrový obrázek: rychlost musí být kladné číslo v mm/min."));
            if (!double.IsFinite(item.LineIntervalMm) || item.LineIntervalMm <= 0)
                issues.Add(Block("settings.invalid-interval", "Rastrový obrázek: rozestup řádků musí být kladné číslo v mm."));
            if (item.Passes < 1)
                issues.Add(Block("settings.invalid-passes", "Rastrový obrázek: počet průchodů musí být alespoň 1."));
        }
    }

    private static PreflightIssue Block(string code, string message) => new(code, message, PreflightSeverity.Blocking);
}
