using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Tour;

/// <summary>Which side of the spotlight the callout card prefers. The layout tries the preferred side
/// first and falls back through the others when the card would leave the window.</summary>
public enum TourPlacement
{
    Right,
    Left,
    Below,
    Above,
    Center,
}

/// <summary>A short term-and-meaning pair shown under a step's sentences ("Výkon - síla paprsku").</summary>
public sealed record TourPoint(string Term, string Meaning);

/// <summary>
/// One coach mark. <see cref="TargetIds"/> are x:Name or AutomationId values looked up in the live
/// visual tree, never pixel positions, so the spotlight follows the layout at any window size. The
/// first id that resolves to a visible element wins; when none does the card is shown centred without
/// a cut-out rather than the step being skipped silently.
/// </summary>
public sealed record TourStep(
    string Id,
    AppScreen Screen,
    IReadOnlyList<string> TargetIds,
    TourPlacement Placement,
    string IconKey,
    string Title,
    string Body,
    IReadOnlyList<TourPoint>? Points = null,
    string? SafetyNote = null);

/// <summary>The seven steps of the first-run tour, in journey order. The wording is the copy deck: Czech,
/// neutral form, no question or exclamation marks, classic hyphens, one or two short sentences.</summary>
public static class TourSteps
{
    public static readonly IReadOnlyList<TourStep> All =
    [
        new("home", AppScreen.Home, ["HomeActions"], TourPlacement.Below, "Glyph.Home",
            "Domov: tady práce začíná",
            "Nový projekt otevře prázdné plátno, Otevřít načte uložený soubor a Importovat vloží obrázek nebo vektorovou grafiku. Pro první zkoušku stačí Importovat."),

        new("designer", AppScreen.Designer, ["DesignerRail"], TourPlacement.Right, "Glyph.Design",
            "Návrh a nástroje",
            "Vlevo je lišta nástrojů: Vybrat, Text, Tvary, Čára a Importovat grafiku. Uprostřed je pracovní plocha laseru, zpracovat lze vše uvnitř jejího okraje."),

        new("operations", AppScreen.Designer, ["DesignerInspector"], TourPlacement.Left, "Glyph.Settings",
            "Operace a nastavení",
            "Každá barva v návrhu je jedna operace s vlastním nastavením. Hotové hodnoty vloží volba Materiál nebo tlačítko Použít na operaci u doporučení od Kamila.",
            [
                new TourPoint("Výkon", "síla paprsku v procentech"),
                new TourPoint("Rychlost", "jak rychle se hlava pohybuje"),
                new TourPoint("Průchody", "kolikrát se dráha zopakuje"),
            ]),

        new("materials", AppScreen.Designer, ["RailMaterialsButton"], TourPlacement.Right, "Glyph.Materials",
            "Materiály a recepty",
            "Vzorník nabízí ověřené hodnoty výkonu a rychlosti pro běžné materiály. Vlastní nastavení lze uložit jako recept a příště použít jedním klepnutím."),

        new("connection", AppScreen.Device, ["StripMachineZone"], TourPlacement.Above, "Glyph.Device",
            "Připojení laseru",
            "Tlačítko Připojit v dolní liště laser najde samo, port ani rychlost vybírat nemusí. Bez laseru lze v části Zařízení vyzkoušet simulátor a projít celý postup nanečisto."),

        new("run", AppScreen.Designer, ["StripJobActions"], TourPlacement.Above, "Glyph.Frame",
            "Rámovat a Spustit",
            "Rámovat obkreslí okraj práce slabým svítícím bodem o výkonu 1 %, takže je vidět, kam laser dosáhne. Spustit pak zahájí vlastní práci.",
            SafetyNote: "Nejdřív zkouška na odřezku, vždy s ochrannými brýlemi."),

        new("kamil", AppScreen.Designer, ["PersistentAvatarLayer"], TourPlacement.Left, "Glyph.Chat",
            "Asistent Kamil",
            "Kamil poradí s materiálem, nastavením i chybami. Stačí se zeptat, odpovědi jsou stručné."),
    ];

    public const string FinishTitle = "Hotovo";

    public const string FinishBody =
        "Prohlídka je u konce. Znovu ji lze otevřít kdykoli z Domova nebo v Nastavení.";

    public const string FinishReplay = "Přehrát znovu";
}

public enum TourState
{
    Active,
    Finished,
    Skipped,
}

/// <summary>
/// Where the tour is. Pure state machine: no WPF, no navigation, no persistence, so ordering, back and
/// forth, the closing card and the two ways out can be tested without a window. The index equal to the
/// step count is the closing "Hotovo" card.
/// </summary>
public sealed class TourSession
{
    private readonly IReadOnlyList<TourStep> _steps;

    public TourSession(IReadOnlyList<TourStep>? steps = null)
    {
        _steps = steps ?? TourSteps.All;
        if (_steps.Count == 0) throw new ArgumentException("A tour needs at least one step.", nameof(steps));
    }

    public IReadOnlyList<TourStep> Steps => _steps;
    public int Index { get; private set; }
    public TourState State { get; private set; } = TourState.Active;
    public int Total => _steps.Count;
    public bool IsActive => State == TourState.Active;
    public bool IsFinishCard => IsActive && Index == _steps.Count;
    public TourStep? CurrentStep => IsActive && Index < _steps.Count ? _steps[Index] : null;
    public bool CanGoBack => IsActive && Index > 0;

    /// <summary>"2 z 7"; empty on the closing card, which is not a numbered step.</summary>
    public string Counter => IsActive && Index < _steps.Count ? $"{Index + 1} z {_steps.Count}" : string.Empty;

    public TourOutcome Outcome => State switch
    {
        TourState.Finished => TourOutcome.Finished,
        TourState.Skipped => TourOutcome.Skipped,
        _ => TourOutcome.NotStarted,
    };

    public event Action? Changed;

    /// <summary>Advance one step; from the last step to the closing card; from the closing card finish.</summary>
    public void Next()
    {
        if (!IsActive) return;
        if (Index < _steps.Count) Index++;
        else State = TourState.Finished;
        Changed?.Invoke();
    }

    public void Back()
    {
        if (!CanGoBack) return;
        Index--;
        Changed?.Invoke();
    }

    public void Skip()
    {
        if (!IsActive) return;
        State = TourState.Skipped;
        Changed?.Invoke();
    }

    public void Apply(TourAction action)
    {
        switch (action)
        {
            case TourAction.Next: Next(); break;
            case TourAction.Back: Back(); break;
            case TourAction.Skip: Skip(); break;
        }
    }
}

public enum TourAction
{
    None,
    Next,
    Back,
    Skip,
}

/// <summary>
/// Key routing while the tour is open (docs/interaction-rules.md 1.9 shape: Enter runs the default,
/// Esc leaves). Enter and Right advance, Left goes back, Esc skips. Enter does nothing special while a
/// button has focus, so Tab to "Přeskočit" and Enter still skips instead of advancing. Anything with
/// Ctrl or Alt is left alone here; the overlay swallows it so no canvas shortcut can act underneath.
/// </summary>
public static class TourKeyRouter
{
    public static TourAction Route(Key key, ModifierKeys modifiers, bool focusOnButton)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return TourAction.None;
        return key switch
        {
            Key.Escape => TourAction.Skip,
            Key.Right => TourAction.Next,
            Key.Left => TourAction.Back,
            Key.Enter when !focusOnButton => TourAction.Next,
            _ => TourAction.None,
        };
    }
}
