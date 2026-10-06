namespace Lasero.App.Tour;

public sealed record GuidanceTip(string Id, string Text);

/// <summary>
/// The micro-tips and what triggers them. Each is shown at most once per account, only after the
/// welcome has been answered, and never while another tip or the tour is on screen. Wording is brief and
/// benefit-oriented, in the neutral form.
/// </summary>
public static class TipCatalog
{
    public const string Import = "import";
    public const string Selection = "selection";
    public const string NodeEdit = "node-edit";
    public const string Connect = "connect";
    public const string Frame = "frame";
    public const string Start = "start";
    public const string Kamil = "kamil";
    public const string EmptyCanvas = "empty-canvas";

    public static readonly IReadOnlyList<GuidanceTip> All =
    [
        new(Import, "Velikost se mění úchyty, poloha přetažením."),
        new(Selection, "Přesné rozměry a polohu lze zadat v liště nad plátnem."),
        new(NodeEdit, "Dvojklik na úsek mezi uzly přidá nový uzel, Enter úpravu ukončí."),
        new(Connect, "Připojení je aktivní. Další krok je Rámovat, tedy kontrola polohy na materiálu."),
        new(Frame, "Slabý bod ukazuje okraj práce. Sedí-li poloha, následuje Spustit."),
        new(Start, "Úlohu lze kdykoli Pozastavit nebo Zastavit. Laser nenechávat bez dozoru."),
        new(Kamil, "Kamil vidí vybraný materiál i operaci. Stačí napsat, co se má stát."),
        new(EmptyCanvas, "Plátno je prázdné. Text a tvary vloží nástroje vlevo, SVG, obrázek nebo G-code vloží Importovat grafiku."),
    ];

    public static GuidanceTip? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

/// <summary>The rotating "Tip dne" on Home: one tip per calendar day, "Další tip" steps through the list.</summary>
public static class TipOfDay
{
    public static readonly IReadOnlyList<string> All =
    [
        "Klávesa F přizpůsobí zobrazení oknu, Ctrl a kolečko myši přiblíží k ukazateli.",
        "Šipky posunou vybraný objekt o 0,5 mm, se Shiftem o 5 mm.",
        "Mezerník s tažením nebo prostřední tlačítko myši posouvá plátno.",
        "Dvojklik na vektorovou dráhu zapne úpravu uzlů, Enter ji ukončí.",
        "Ctrl+D vytvoří kopii vybraného objektu, Ctrl+G sloučí výběr do skupiny.",
        "Klávesa Esc vrací o krok zpět: zavře úpravu, zruší nástroj a nakonec výběr.",
        "Klávesy V, T, R, E a L přepínají nástroje, přehled zkratek otevře F1.",
        "Alt+T převede vybranou bitmapu na vektorové obrysy.",
        "Před ostrou prací vždy otestovat nastavení na odřezku stejného materiálu.",
        "Operace se provádějí shora dolů, pořadí lze měnit šipkami v panelu Operace.",
        "Projekt se každých 30 sekund automaticky zálohuje, Ctrl+S ho uloží natrvalo.",
        "Rámovat obkreslí okraj práce slabým bodem, kontrola polohy trvá jen pár sekund.",
    ];

    /// <summary>Deterministic for a date, so the tip does not change on every visit to Home.
    /// <paramref name="offset"/> is the number of "Další tip" presses in this session.</summary>
    public static string For(DateTime date, int offset = 0)
    {
        var index = (date.DayOfYear + date.Year * 7 + offset) % All.Count;
        return All[(index + All.Count) % All.Count];
    }
}
