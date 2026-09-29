using System.Globalization;
using System.Text.RegularExpressions;
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
    public double? MaxSpindleSpeed { get; init; }
    public bool? LaserModeEnabled { get; init; }
    public bool IsRawGCode { get; init; }
}

public sealed record JobPreflightResult(IReadOnlyList<PreflightIssue> Issues)
{
    public bool CanStart => Issues.All(issue => issue.Severity != PreflightSeverity.Blocking);
    public PreflightIssue? FirstBlockingIssue => Issues.FirstOrDefault(issue => issue.Severity == PreflightSeverity.Blocking);
}

public static class JobPreflight
{
    /// <summary>The reason Start is unavailable while no laser is connected. Public so the UI can show
    /// the very same sentence in its own surfaces instead of a second, drifting copy.</summary>
    public const string DisconnectedMessage = "Připojte laser, aby bylo možné úlohu připravit a odeslat.";

    private static readonly Regex GCodeWordPattern = new(@"([A-Za-z])\s*(-?\d+\.?\d*)", RegexOptions.Compiled);

    public static JobPreflightResult Evaluate(JobPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<PreflightIssue>();

        if (!context.IsConnected)
            issues.Add(Block("device.disconnected", DisconnectedMessage));

        if (context.Document is null || context.Document.RawLines.Count == 0)
            issues.Add(Block("job.empty", "Návrh je prázdný. Přidejte text nebo tvar, případně importujte grafiku."));
        else if (context.Document.BoundingBox.IsEmpty)
            issues.Add(Block("job.no-motion", "Úloha neobsahuje žádnou dráhu pro laser. Zkontrolujte, zda jsou operace zapnuté a objekty zahrnuté do úlohy."));
        else
            ValidateBounds(context, issues);

        ValidateEngravingSettings(context.Layers, issues);
        ValidateRasterSettings(context.RasterOptions, issues);
        if ((context.Layers?.Any(layer => layer.IsEnabled) == true || context.RasterOptions?.Count > 0)
            && (context.MaxSpindleSpeed is not { } maxS || !double.IsFinite(maxS) || maxS <= 0))
            issues.Add(Block("machine.power-range-unknown", "Rozsah výkonu zařízení není známý. Načtěte nastavení GRBL včetně $30 a úlohu připravte znovu."));
        if ((context.Layers?.Any(layer => layer.IsEnabled) == true || context.RasterOptions?.Count > 0)
            && context.LaserModeEnabled != true)
            issues.Add(Block("machine.laser-mode-disabled", "Laserový režim GRBL ($32=1) není potvrzen. Zapněte jej v průvodci zařízením a znovu načtěte profil."));
        if (context.IsRawGCode && context.Document is { } rawDocument)
            ValidateRawGCodePower(context, rawDocument, issues);

        if (context.IsConnected)
        {
            if (context.MachineStatus is null)
            {
                issues.Add(Block("machine.no-status", "Čeká se na první odpověď laseru. Chvíli počkejte, případně zkontrolujte USB kabel."));
            }
            else
            {
                if (context.MachineStatusAge is { } age && age > TimeSpan.FromSeconds(2))
                    issues.Add(Block("machine.stale-status", "Laser přestal posílat stav. Zkontrolujte USB kabel a napájení, případně se znovu připojte."));
                ValidateMachineState(context.MachineStatus, issues);
            }
        }

        if (context.RequireFraming && !context.HasFramedCurrentDocument)
            // Short enough to be read where it actually appears: the status strip gives a block
            // reason about 260px, and the previous wording was cut off mid-sentence there.
            issues.Add(Block("job.framing-required", "Nejprve ověřte umístění tlačítkem Rámovat."));

        return new JobPreflightResult(issues);
    }

    private static void ValidateRawGCodePower(
        JobPreflightContext context,
        GCodeDocument document,
        ICollection<PreflightIssue> issues)
    {
        double? currentPower = null;
        var spindleOn = false;
        var anyPoweredCommand = false;
        var maxS = context.MaxSpindleSpeed;

        foreach (var rawLine in document.RawLines)
        {
            var line = rawLine.Split(';', 2)[0];
            line = Regex.Replace(line, @"\([^)]*\)", string.Empty).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('$'))
            {
                issues.Add(Block("job.controller-command-not-allowed", "Importovaný G-code obsahuje přímý příkaz pro řadič. Takové příkazy odesílejte přes ovládací prvky zařízení."));
                continue;
            }

            var spindleEnable = false;
            var spindleDisable = false;
            foreach (Match match in GCodeWordPattern.Matches(line))
            {
                var letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
                if (letter == 'S' && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPower))
                    currentPower = parsedPower;
                else if (letter == 'M' && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mCode))
                {
                    spindleEnable |= mCode is 3 or 4;
                    spindleDisable |= mCode == 5;
                }
            }

            if (spindleDisable) spindleOn = false;
            if (spindleEnable) spindleOn = true;
            if (!spindleOn) continue;

            if (currentPower is null)
            {
                issues.Add(Block("job.spindle-power-unspecified", "G-code zapíná laser bez výkonu S určeného v tomto souboru."));
                continue;
            }
            if (currentPower > 0) anyPoweredCommand = true;
            if (!double.IsFinite(currentPower.Value) || currentPower < 0)
                issues.Add(Block("settings.invalid-power", "Importovaný G-code obsahuje neplatnou hodnotu S."));
            else if (maxS is { } maximum && double.IsFinite(maximum) && maximum > 0 && currentPower > maximum)
                issues.Add(Block("job.power-exceeds-controller-range", $"G-code požaduje výkon nad maximem $30 ({maximum:0.###}). Hodnoty S v importovaném G-code zůstávají beze změny."));
        }

        if (anyPoweredCommand && (maxS is not { } reportedMaximum || !double.IsFinite(reportedMaximum) || reportedMaximum <= 0))
            issues.Add(Block("machine.power-range-unknown", "G-code zapíná laser, ale maximum $30 není načteno."));
        if (anyPoweredCommand && context.LaserModeEnabled != true)
            issues.Add(Block("machine.laser-mode-disabled", "G-code zapíná laser, ale režim GRBL ($32=1) není potvrzen."));
    }

    private static void ValidateBounds(JobPreflightContext context, ICollection<PreflightIssue> issues)
    {
        if (!double.IsFinite(context.WorkAreaWidthMm) || !double.IsFinite(context.WorkAreaHeightMm) || context.WorkAreaWidthMm <= 0 || context.WorkAreaHeightMm <= 0)
        {
            issues.Add(Block("machine.invalid-work-area", "Rozměry pracovní plochy nejsou platné."));
            return;
        }

        var bounds = context.Document!.BoundingBox;
        if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY) ||
            !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY))
        {
            issues.Add(Block("job.invalid-bounds", "Dráha obsahuje neplatné souřadnice. Připravte úlohu znovu."));
            return;
        }
        const double tolerance = 0.001;
        if (bounds.MinX < -tolerance || bounds.MinY < -tolerance
            || bounds.MaxX > context.WorkAreaWidthMm + tolerance
            || bounds.MaxY > context.WorkAreaHeightMm + tolerance)
        {
            issues.Add(Block(
                "job.outside-work-area",
                $"Návrh přesahuje pracovní plochu {context.WorkAreaWidthMm:0.#} × {context.WorkAreaHeightMm:0.#} mm. Přesuňte jej dovnitř plochy nebo zmenšete."));
        }
    }

    private static void ValidateMachineState(MachineStatus status, ICollection<PreflightIssue> issues)
    {
        if (status.Mode != GrblMachineMode.Idle)
        {
            var message = status.Mode switch
            {
                GrblMachineMode.Alarm => "Laser je v alarmu. Zkontrolujte prostor stroje, odstraňte příčinu a laser odemkněte v ručním ovládání.",
                GrblMachineMode.Door => "Bezpečnostní kryt nebo dveře jsou otevřené. Zavřete je a zkuste to znovu.",
                GrblMachineMode.Hold => "Laser je pozastavený. Pokračujte v úloze, nebo ji zastavte.",
                GrblMachineMode.Run or GrblMachineMode.Jog or GrblMachineMode.Home => "Laser se právě pohybuje. Počkejte, až pohyb skončí.",
                _ => $"Laser zatím není připraven (stav {status.Mode}). Počkejte na stav Připraveno.",
            };
            issues.Add(Block("machine.not-idle", message));
        }

        var pins = status.TriggeredPins ?? string.Empty;
        if (pins.IndexOfAny(['X', 'Y', 'Z']) >= 0)
            issues.Add(Block("machine.limit-triggered", "Některá osa stojí na koncovém spínači. Odsuňte hlavu ručně a zkontrolujte, zda nic nebrání pohybu."));
        if (pins.Contains('D'))
            issues.Add(Block("machine.door-triggered", "Je aktivní vstup bezpečnostních dveří. Zavřete dveře a zkontrolujte kabel dveřního spínače."));
        if (pins.Contains('P'))
            issues.Add(new PreflightIssue("machine.probe-triggered", "Je aktivní vstup sondy.", PreflightSeverity.Warning));
    }

    private static void ValidateEngravingSettings(IReadOnlyList<LayerSettings>? layers, ICollection<PreflightIssue> issues)
    {
        if (layers is null || layers.Count == 0) return;

        foreach (var layer in layers.Where(item => item.IsEnabled))
        {
            var prefix = string.IsNullOrWhiteSpace(layer.Name) ? "Operace" : $"Operace „{layer.Name}“";
            if (!double.IsFinite(layer.Power) || layer.Power is <= 0 or > 100)
                issues.Add(Block("settings.invalid-power", $"{prefix}: výkon musí být v rozsahu 1 až 100 %. Upravte jej v nastavení operace."));
            if (!double.IsFinite(layer.Speed) || layer.Speed <= 0)
                issues.Add(Block("settings.invalid-speed", $"{prefix}: rychlost musí být větší než nula (mm/min). Upravte ji v nastavení operace."));
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
                issues.Add(Block("settings.invalid-power", "Rastrový obrázek: výkon musí být v rozsahu 1 až 100 %."));
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
