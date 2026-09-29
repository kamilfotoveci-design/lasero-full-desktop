using System.Globalization;

namespace Lasero.App.ViewModels;

/// <summary>Everything the Start confirmation states, as plain values.</summary>
public sealed record StartSummaryInput(
    string JobName,
    double WidthMm,
    double HeightMm,
    string MachineName,
    string? MachinePort,
    bool IsSimulator,
    string PlacementLabel,
    string SettingsSummary,
    string EstimatedTime,
    bool IsFramed);

/// <summary>
/// The wording of the Start confirmation. Start must never be ambiguous, so the dialog answers the
/// four things an operator standing at the machine needs before agreeing: which machine it is about
/// to drive (and whether that machine is the simulator), what will be made and where, whether the
/// placement was checked with framing, and that the laser starts working the moment they confirm.
///
/// Presentation only. It reads a decision that preflight already made and changes nothing about it.
/// </summary>
public static class StartSummary
{
    public const string SimulatorNote = "simulátor, laser se nezapne";

    public static string Build(StartSummaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var culture = CultureInfo.CurrentCulture;

        var machine = string.IsNullOrWhiteSpace(input.MachinePort)
            ? input.MachineName
            : $"{input.MachineName} ({input.MachinePort})";
        if (input.IsSimulator) machine += $" - {SimulatorNote}";

        var framing = input.IsFramed
            ? "ověřeno, umístění na materiálu bylo zkontrolováno"
            : "neověřeno, umístění na materiálu nebylo zkontrolováno";

        return string.Join("\n",
            $"Zařízení: {machine}",
            $"Úloha: {input.JobName}",
            string.Format(culture, "Rozměr: {0:0.#} × {1:0.#} mm", input.WidthMm, input.HeightMm),
            $"Umístění: {input.PlacementLabel}",
            $"Rámování: {framing}",
            input.SettingsSummary,
            $"Odhadovaný čas: {input.EstimatedTime}",
            string.Empty,
            input.IsSimulator
                ? "Po potvrzení se úloha odehraje na simulátoru, žádný fyzický laser nebude pracovat."
                : "Po potvrzení laser okamžitě začne pracovat. Zastavit jej lze tlačítkem Zastavit.",
            "Před spuštěním zkontrolujte materiál, odsávání a ochranný kryt.");
    }
}
