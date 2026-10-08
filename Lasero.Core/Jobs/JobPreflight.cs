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
    public double? MachineMaxTravelXmm { get; init; }
    public double? MachineMaxTravelYmm { get; init; }
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
    public const string DisconnectedMessage = "K přípravě a odeslání úlohy je potřeba připojit laser.";

    private static readonly Regex GCodeWordPattern = new(@"([A-Za-z])\s*(-?\d+\.?\d*)", RegexOptions.Compiled);
    private static readonly Regex RawGCodeTokenPattern = new(@"[A-Za-z]\s*-?\d+\.?\d*", RegexOptions.Compiled);

    public static JobPreflightResult Evaluate(JobPreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<PreflightIssue>();

        if (!context.IsConnected)
            issues.Add(Block("device.disconnected", DisconnectedMessage));

        if (context.Document is null || context.Document.RawLines.Count == 0)
            issues.Add(Block("job.empty", "Návrh je prázdný. Lze přidat text nebo tvar, případně importovat grafiku."));
        else if (context.Document.BoundingBox.IsEmpty)
            issues.Add(Block("job.no-motion", "Úloha neobsahuje žádnou dráhu pro laser. Je potřeba ověřit, zda jsou operace zapnuté a objekty zahrnuté do úlohy."));
        else
            ValidateBounds(context, issues);

        ValidateEngravingSettings(context.Layers, issues);
        ValidateRasterSettings(context.RasterOptions, issues);
        if ((context.Layers?.Any(layer => layer.IsEnabled) == true || context.RasterOptions?.Count > 0)
            && (context.MaxSpindleSpeed is not { } maxS || !double.IsFinite(maxS) || maxS <= 0))
            issues.Add(Block("machine.power-range-unknown", "Rozsah výkonu zařízení není známý. Je potřeba načíst nastavení GRBL včetně $30 a úlohu připravit znovu."));
        if ((context.Layers?.Any(layer => layer.IsEnabled) == true || context.RasterOptions?.Count > 0)
            && context.LaserModeEnabled != true)
            issues.Add(Block("machine.laser-mode-disabled", "Laserový režim GRBL ($32=1) není potvrzen. Je potřeba jej zapnout v průvodci zařízením a znovu načíst profil."));
        if (context.IsRawGCode && context.Document is { } rawDocument)
            ValidateRawGCodePower(context, rawDocument, issues);

        if (context.IsConnected)
        {
            if (context.MachineStatus is null)
            {
                issues.Add(Block("machine.no-status", "Čeká se na první odpověď laseru. Je potřeba chvíli počkat, případně zkontrolovat USB kabel."));
            }
            else
            {
                if (context.MachineStatusAge is { } age && age > TimeSpan.FromSeconds(2))
                    issues.Add(Block("machine.stale-status", "Laser přestal posílat stav. Je potřeba zkontrolovat USB kabel a napájení, případně zařízení připojit znovu."));
                ValidateMachineState(context.MachineStatus, issues);
            }
        }

        if (context.RequireFraming && !context.HasFramedCurrentDocument)
            // Short enough to be read where it actually appears: the status strip gives a block
            // reason about 260px, and the previous wording was cut off mid-sentence there.
            issues.Add(Block("job.framing-required", "Nejprve je potřeba ověřit umístění tlačítkem Rámovat."));

        return new JobPreflightResult(issues);
    }

    private static void ValidateRawGCodePower(
        JobPreflightContext context,
        GCodeDocument document,
        ICollection<PreflightIssue> issues)
    {
        double? currentPower = null;
        var spindleOn = false;
        var motionMode = 0;
        var anyPoweredCommand = false;
        var maxS = context.MaxSpindleSpeed;

        foreach (var rawLine in document.RawLines)
        {
            var line = rawLine.Split(';', 2)[0];
            line = Regex.Replace(line, @"\([^)]*\)", string.Empty).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('$'))
            {
                issues.Add(Block("job.controller-command-not-allowed", "Importovaný G-code obsahuje přímý příkaz pro řadič. Takové příkazy se odesílají přes ovládací prvky zařízení."));
                continue;
            }

            // Preview/bounds validation only models this small 2D subset. Reject syntax the
            // tokenizer cannot account for as well as recognized but unsupported commands;
            // otherwise the controller could execute a path that was never previewed.
            var residue = RawGCodeTokenPattern.Replace(line, string.Empty);
            if (!string.IsNullOrWhiteSpace(residue))
            {
                issues.Add(Block("job.unsupported-gcode-command", "Importovaný G-code obsahuje nepodporovaný zápis. Lze spustit jen příkazy G0–G3, G20–G21, G90–G91, M3–M5 a souřadnice X/Y/Z/I/J/R/S/F."));
                continue;
            }
            var spindleEnable = false;
            var spindleDisable = false;
            int? blockMotionCode = null;
            int? blockSpindleCode = null;
            var lineHasAxis = false;
            var unsupportedCommand = false;
            foreach (Match match in GCodeWordPattern.Matches(line))
            {
                var letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
                if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                {
                    unsupportedCommand = true;
                    continue;
                }

                switch (letter)
                {
                    case 'G':
                        if (value != Math.Truncate(value) || value is not (0 or 1 or 2 or 3 or 20 or 21 or 90 or 91))
                            unsupportedCommand = true;
                        else if (value is 0 or 1 or 2 or 3)
                        {
                            var code = (int)value;
                            if (blockMotionCode is { } previousMotion && previousMotion != code)
                                unsupportedCommand = true;
                            blockMotionCode = code;
                            motionMode = code;
                        }
                        break;
                    case 'M':
                        if (value != Math.Truncate(value) || value is not (3 or 4 or 5))
                            unsupportedCommand = true;
                        else
                        {
                            var code = (int)value;
                            if (blockSpindleCode is { } previousSpindle && previousSpindle != code)
                                unsupportedCommand = true;
                            blockSpindleCode = code;
                            spindleEnable |= value is 3 or 4;
                            spindleDisable |= value == 5;
                        }
                        break;
                    case 'S': currentPower = value; break;
                    case 'F': case 'X': case 'Y': case 'Z': case 'I': case 'J': case 'R':
                        lineHasAxis |= letter is 'X' or 'Y' or 'Z';
                        break;
                    case 'N': // Optional source line number.
                        break;
                    default:
                        unsupportedCommand = true;
                        break;
                }
            }

            if (unsupportedCommand)
            {
                issues.Add(Block("job.unsupported-gcode-command", "Importovaný G-code obsahuje příkaz mimo bezpečně podporovanou podmnožinu. Souřadnicové transformace, změny souřadnicového systému a další CNC příkazy nejsou podporované."));
                continue;
            }

            if (spindleDisable) spindleOn = false;
            if (spindleEnable) spindleOn = true;
            if (spindleOn && currentPower is > 0 && motionMode == 0 && lineHasAxis)
                issues.Add(Block("job.laser-on-rapid", "Importovaný G-code přesouvá hlavu rychloposuvem při zapnutém laseru. Před příkazem G0 je potřeba laser vypnout."));
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
            issues.Add(Block("job.invalid-bounds", "Dráha obsahuje neplatné souřadnice. Úlohu je potřeba připravit znovu."));
            return;
        }
        const double tolerance = 0.001;
        if (bounds.MinX < -tolerance || bounds.MinY < -tolerance
            || bounds.MaxX > context.WorkAreaWidthMm + tolerance
            || bounds.MaxY > context.WorkAreaHeightMm + tolerance)
        {
            issues.Add(Block(
                "job.outside-work-area",
                $"Návrh přesahuje pracovní plochu {context.WorkAreaWidthMm:0.#} × {context.WorkAreaHeightMm:0.#} mm. Je potřeba jej přesunout dovnitř plochy nebo zmenšit."));
        }

        var exceedsReportedTravel =
            context.MachineMaxTravelXmm is { } maxX && double.IsFinite(maxX) && maxX > 0 && bounds.MaxX > maxX + tolerance ||
            context.MachineMaxTravelYmm is { } maxY && double.IsFinite(maxY) && maxY > 0 && bounds.MaxY > maxY + tolerance;
        if (exceedsReportedTravel)
        {
            issues.Add(Block(
                "job.exceeds-reported-machine-travel",
                "Návrh přesahuje maximální zdvih nahlášený řadičem. Je potřeba upravit pracovní plochu nebo přesunout návrh dovnitř rozsahu stroje."));
        }
    }

    private static void ValidateMachineState(MachineStatus status, ICollection<PreflightIssue> issues)
    {
        if (status.Mode != GrblMachineMode.Idle)
        {
            var message = status.Mode switch
            {
                GrblMachineMode.Alarm => "Laser je v alarmu. Je potřeba zkontrolovat prostor stroje, odstranit příčinu a laser odemknout v ručním ovládání.",
                GrblMachineMode.Door => "Bezpečnostní kryt nebo dveře jsou otevřené. Je potřeba je zavřít, potom lze akci zkusit znovu.",
                GrblMachineMode.Hold => "Laser je pozastavený. V úloze lze pokračovat, nebo ji zastavit.",
                GrblMachineMode.Run or GrblMachineMode.Jog or GrblMachineMode.Home => "Laser se právě pohybuje. Je potřeba počkat, až pohyb skončí.",
                _ => $"Laser zatím není připraven (stav {status.Mode}). Je potřeba počkat na stav Připraveno.",
            };
            issues.Add(Block("machine.not-idle", message));
        }

        var pins = status.TriggeredPins ?? string.Empty;
        if (pins.IndexOfAny(['X', 'Y', 'Z']) >= 0)
            issues.Add(Block("machine.limit-triggered", "Některá osa stojí na koncovém spínači. Je potřeba hlavu odsunout ručně a ověřit, zda nic nebrání pohybu."));
        if (pins.Contains('D'))
            issues.Add(Block("machine.door-triggered", "Je aktivní vstup bezpečnostních dveří. Je potřeba zavřít dveře a zkontrolovat kabel dveřního spínače."));
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
                issues.Add(Block("settings.invalid-power", $"{prefix}: výkon musí být v rozsahu 1 až 100 %. Lze jej upravit v nastavení operace."));
            if (!double.IsFinite(layer.Speed) || layer.Speed <= 0)
                issues.Add(Block("settings.invalid-speed", $"{prefix}: rychlost musí být větší než nula (mm/min). Lze ji upravit v nastavení operace."));
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
