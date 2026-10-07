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
        "Klávesa F zobrazí celý návrh v okně, kolečko myši s klávesou Ctrl přibližuje a oddaluje.",
        "Šipky na klávesnici posouvají vybraný objekt po půl milimetru, se Shiftem po pěti milimetrech.",
        "Plátno lze posouvat tažením myší při stisknutém mezerníku.",
        "Dvojklik na čáru ukáže body, ze kterých se skládá. Jejich tažením se tvar upraví, Enter úpravu ukončí.",
        "Ctrl+D vytvoří kopii vybraného objektu, Ctrl+G spojí vybrané objekty do jedné skupiny.",
        "Klávesa Esc vrací o krok zpět: zavře úpravu, zruší nástroj a nakonec zruší výběr.",
        "Klávesy V, T, R, E a L přepínají nástroje, přehled všech zkratek otevře klávesa F1.",
        "Alt+T převede obrázek na čáry, které laser umí vyříznout nebo vyrýt.",
        "Před ostrou prací je dobré vyzkoušet nastavení na zbytku stejného materiálu.",
        "Laser pracuje po krocích shora dolů, pořadí kroků se mění šipkami v panelu Operace.",
        "Projekt se každých 30 sekund sám zálohuje, Ctrl+S ho uloží natrvalo.",
        "Tlačítko Rámovat obkreslí okraj práce slabým světlem, takže je vidět, kam laser dosáhne.",
    ];

    /// <summary>Deterministic for a date, so the tip does not change on every visit to Home.
    /// <paramref name="offset"/> is the number of "Další tip" presses in this session.</summary>
    public static string For(DateTime date, int offset = 0)
    {
        var index = (date.DayOfYear + date.Year * 7 + offset) % All.Count;
        return All[(index + All.Count) % All.Count];
    }

    /// <summary>1-based place of a tip in the rotation (1 when it is not one of the list).</summary>
    public static int PositionOf(string tip)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i] == tip) return i + 1;
        return 1;
    }
}
