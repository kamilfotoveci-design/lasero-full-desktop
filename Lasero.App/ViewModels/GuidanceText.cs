using Lasero.Core.Jobs;

namespace Lasero.App.ViewModels;

/// <summary>
/// One contextual hint for the design canvas. <see cref="Key"/> identifies the situation (not the
/// wording), so a hint the operator has dismissed stays dismissed for the session even if its text is
/// later reworded, and so each situation can be dismissed independently.
/// </summary>
public sealed record CanvasHint(string Key, string Text);

/// <summary>What the canvas hint needs to know. Plain values, so the decision is testable without WPF.</summary>
public sealed record DesignerHintContext(
    DesignerTool ActiveTool,
    int ObjectCount,
    int SelectedCount,
    bool SelectedIsVectorPath,
    bool IsNodeEditActive,
    int SelectedNodeCount);

/// <summary>
/// The wording for progressive disclosure: the right sentence at the moment it is needed, decided in
/// one place. Nothing here is shown all the time. Every hint answers "what do I do now" for exactly
/// one situation, and the canvas lets the operator dismiss it for the rest of the session.
///
/// Texts are Czech in a neutral form, without question or exclamation marks and with plain hyphens.
/// </summary>
public static class GuidanceText
{
    public const string NoSelection = "Vyberte objekt, jehož vlastnosti chcete upravit";

    /// <summary>Same sentence <see cref="JobPreflight"/> gives as the reason Start is unavailable, so
    /// the strip, the tooltip and the preflight can never disagree about it.</summary>
    public const string NoMachine = JobPreflight.DisconnectedMessage;

    /// <summary>The last step before Start when the laser is ready and the placement has not been
    /// checked yet. Preflight enforces it; this only says it before the operator presses Start.</summary>
    public const string FramingNextStep = "Před spuštěním ověřte umístění tlačítkem Rámovat";

    public const string EmptyCanvasTitle = "Plátno je prázdné";

    public const string EmptyCanvasDescription =
        "Vložte text nebo tvar nástroji vlevo, případně importujte SVG, obrázek či G-code. " +
        "Laser pracuje jen uvnitř vyznačené pracovní plochy.";

    /// <summary>
    /// The single most useful hint for the current canvas state, or null when the canvas already
    /// explains itself (an empty scene shows its own empty state instead of a hint).
    /// Order matters: an editing mode in progress always outranks a general hint.
    /// </summary>
    public static CanvasHint? DesignerHint(DesignerHintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsNodeEditActive)
        {
            return context.SelectedNodeCount switch
            {
                0 => new CanvasHint("nodes-none", "Vyberte uzel kliknutím nebo více uzlů tažením výběrového obdélníku"),
                1 => new CanvasHint("nodes-one", "Uzel přesunete tažením, pravým tlačítkem zobrazíte akce pro uzel"),
                _ => new CanvasHint("nodes-many", "Vybrané uzly přesunete tažením, pravým tlačítkem na uzel zobrazíte další akce"),
            };
        }

        switch (context.ActiveTool)
        {
            case DesignerTool.Text:
                return new CanvasHint("tool-text", "Kliknutím na plátno vložíte text, jeho znění upravíte přímo na plátně");
            case DesignerTool.Line:
                return new CanvasHint("tool-line", "Klikáním přidávejte body čáry, dokončíte ji dvojklikem nebo klávesou Enter");
            case DesignerTool.Select:
                break;
            case DesignerTool.Pan:
                return null;
            default:
                return new CanvasHint("tool-shape", "Tažením po plátně nakreslíte tvar");
        }

        if (context.ObjectCount == 0) return null;
        if (context.SelectedCount == 0) return new CanvasHint("no-selection", NoSelection);
        if (context.SelectedCount == 1 && context.SelectedIsVectorPath)
            return new CanvasHint("path-selected", "Dvojklikem na vybranou dráhu upravíte její uzly");
        return null;
    }

    /// <summary>
    /// Why the machine actions are unavailable before any job even exists, phrased as the next step.
    /// Null when there is nothing to say: either a laser is connected (the preflight then speaks for
    /// itself) or there is no design yet, in which case asking for a laser would be premature.
    /// </summary>
    public static string? MachineNextStep(bool hasDesign, bool isConnected, bool isConnecting)
    {
        if (!hasDesign || isConnected || isConnecting) return null;
        return NoMachine;
    }

    /// <summary>
    /// The next step once everything else allows a start: check the placement with framing. Null when
    /// Start is not available for another reason (then that reason speaks) or framing is not required.
    /// </summary>
    public static string? FramingStep(bool startAvailable, bool needsFraming) =>
        startAvailable && needsFraming ? FramingNextStep : null;
}
