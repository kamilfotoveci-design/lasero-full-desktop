using Lasero.Core.Layers;

namespace Lasero.Core.Materials;

/// <summary>
/// Offline, immutable catalog shared by every desktop workflow. Values mirror the existing
/// lasero.net material database; generated IR/CO₂ rows deliberately use the same formulas as the
/// web app so all clients can later consume one versioned server contract without data drift.
/// </summary>
public static class MaterialCatalog
{
    public const int SchemaVersion = 1;
    public static IReadOnlyList<int> PowerClasses { get; } = [5, 10, 20, 40, 60];

    public static IReadOnlyList<MaterialDefinition> Materials { get; } =
    [
        new("wood", "Dřevo", "Lípa, borovice, buk, olše, třešeň"),
        new("plywood", "Překližka", "Překližka 3–6 mm"),
        new("mdf", "MDF", "Středně hustá dřevovláknitá deska"),
        new("acrylic", "Akryl", "Litý nebo extrudovaný akryl"),
        new("leather", "Kůže", "Přírodní a syntetická kůže"),
        new("anodized", "Anodizovaný hliník", "Barevně anodizovaný hliník"),
        new("metal", "Kov a ocel", "Nerez, ocel a měď"),
        new("fabric", "Textil", "Bavlna, džínovina a filc"),
        new("paper", "Papír a karton", "Papír, karton a balza"),
        new("slate", "Břidlice", "Břidlice, pískovec a keramika"),
        new("glass", "Sklo", "Float sklo a zrcadlo"),
        new("rubber", "Guma", "Gumová razítka a těsnění"),
    ];

    public static IReadOnlyList<MaterialRecipe> Recipes { get; } = BuildRecipes();

    public static IEnumerable<MaterialRecipe> Find(
        LaserTechnology technology,
        int powerWatts,
        LayerMode? mode = null,
        string? materialId = null) => Recipes.Where(recipe =>
            recipe.Technology == technology &&
            recipe.PowerWatts == powerWatts &&
            (!mode.HasValue || recipe.Mode == mode.Value) &&
            (string.IsNullOrWhiteSpace(materialId) || recipe.MaterialId == materialId));

    private static IReadOnlyList<MaterialRecipe> BuildRecipes()
    {
        var recipes = new List<MaterialRecipe>(300);
        AddDiode(recipes);
        foreach (var watts in PowerClasses)
        {
            AddInfrared(recipes, watts);
            AddCo2(recipes, watts);
        }
        return recipes;
    }

    /// <summary>
    /// Diode rows still mirror lasero.net except the plywood and MDF cut recipes, which were wrong
    /// by roughly 3x in dose and have been re-based on measurement plus published reference tables:
    /// <list type="bullet">
    /// <item>20 W plywood: measured on the shop's own machine — 350 mm/min, 100 %, one pass.</item>
    /// <item>10 / 40 W plywood and 10 / 20 / 40 W MDF: lasertinkerer.com 2026 diode settings tables,
    /// normalised to the power level this catalog already used for that row.</item>
    /// <item>60 W plywood: no published table exists that high; scaled from the 40 W row by optical
    /// watts. Flagged here because it is the one derived number, not a measured or cited one.</item>
    /// <item>5 W plywood: numbers left alone, but the reference tables agree a 5 W diode needs
    /// 10–15 passes on 3 mm plywood, so the row now carries a warning instead of pretending it is a
    /// normal job.</item>
    /// </list>
    /// Everything below cutting — engraving speeds, and every other material — is still the
    /// unverified web data and should be measured before it is trusted.
    /// </summary>
    private static void AddDiode(List<MaterialRecipe> r)
    {
        Add(r, LaserTechnology.Diode, 5, "wood", E(3000,60,1,300), C(150,100,4));
        Add(r, LaserTechnology.Diode, 5, "plywood", E(2500,65,1,300), C(100,100,6, warning:"Na 5 W je řezání překližky hraniční: je třeba počítat s mnoha průchody, dlouhým časem a stálým dohledem."));
        Add(r, LaserTechnology.Diode, 5, "acrylic", E(2000,55,1,300, note:"Pouze barevný akryl", compatibility:MaterialCompatibility.Suitable), C(80,100,5, compatibility:MaterialCompatibility.NotRecommended, warning:"Čirý akryl je pro diodový laser téměř průhledný."));
        Add(r, LaserTechnology.Diode, 5, "leather", E(3000,40,1,300), C(200,100,3));
        Add(r, LaserTechnology.Diode, 5, "anodized", E(1500,100,1,300, note:"Výsledek závisí na barvě anodizace", compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 5, "metal", E(800,100,2,200, note:"Nutný LaserBond nebo CerMark", compatibility:MaterialCompatibility.Suitable, warning:"Bez značicí pasty paprsek kov odráží."));
        Add(r, LaserTechnology.Diode, 5, "fabric", E(4000,30,1,300), C(300,80,2));
        Add(r, LaserTechnology.Diode, 5, "paper", E(5000,25,1,400), C(400,70,1));
        Add(r, LaserTechnology.Diode, 5, "slate", E(2000,100,1,300, compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 5, "glass", E(3000,60,1,300, note:"Výsledek závisí na složení skla", compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 5, "rubber", E(2000,70,1,300), C(200,100,3));
        Add(r, LaserTechnology.Diode, 5, "mdf", E(2500,65,1,300), C(120,100,5));

        Add(r, LaserTechnology.Diode, 10, "wood", E(4000,55,1,300), C(250,100,3));
        Add(r, LaserTechnology.Diode, 10, "plywood", E(3500,55,1,300), C(280,100,4));
        Add(r, LaserTechnology.Diode, 10, "acrylic", E(2500,50,1,300, note:"Pouze barevný akryl", compatibility:MaterialCompatibility.Suitable), C(120,100,4, compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 10, "leather", E(4000,35,1,300), C(300,90,2));
        Add(r, LaserTechnology.Diode, 10, "anodized", E(2000,100,1,300, compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 10, "metal", E(1000,100,2,200, note:"Nutný CerMark", compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 10, "fabric", E(5000,28,1,300), C(500,70,2));
        Add(r, LaserTechnology.Diode, 10, "paper", E(6000,20,1,400), C(600,60,1));
        Add(r, LaserTechnology.Diode, 10, "slate", E(2500,90,1,300));
        Add(r, LaserTechnology.Diode, 10, "glass", E(3500,55,1,300, compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 10, "rubber", E(2500,65,1,300), C(300,100,2));
        Add(r, LaserTechnology.Diode, 10, "mdf", E(3000,60,1,300), C(180,100,3));

        Add(r, LaserTechnology.Diode, 20, "wood", E(5000,50,1,300), C(400,100,2));
        // Measured on the shop's own 20 W diode, 3-6 mm plywood: one pass at 350 mm/min, full power.
        // The mirrored web value (280 mm/min, 3 passes) asked for roughly 3.75x the dose and made a
        // cut that finishes in one pass take three.
        Add(r, LaserTechnology.Diode, 20, "plywood", E(4500,50,1,300), C(350,100,1));
        Add(r, LaserTechnology.Diode, 20, "acrylic", E(3000,45,1,300, note:"Barevný a černý akryl; čirý je nevhodný", compatibility:MaterialCompatibility.Suitable), C(200,100,3, compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 20, "leather", E(5000,30,1,300), C(500,80,1));
        Add(r, LaserTechnology.Diode, 20, "anodized", E(2500,95,1,300, note:"Velmi dobré výsledky na černém anodizovaném hliníku"));
        Add(r, LaserTechnology.Diode, 20, "metal", E(1200,100,3,200, note:"CerMark nebo LaserBond je povinný", compatibility:MaterialCompatibility.Suitable, warning:"Bez značicí pasty paprsek kov odráží."));
        Add(r, LaserTechnology.Diode, 20, "fabric", E(6000,25,1,300), C(700,65,1));
        Add(r, LaserTechnology.Diode, 20, "paper", E(8000,18,1,400), C(800,55,1));
        Add(r, LaserTechnology.Diode, 20, "slate", E(3000,80,1,300));
        Add(r, LaserTechnology.Diode, 20, "glass", E(4000,50,1,300, note:"Vhodné mléčné sklo; přímé sklo vyžaduje přesný fokus", compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Diode, 20, "rubber", E(3000,60,1,300), C(400,100,2));
        Add(r, LaserTechnology.Diode, 20, "mdf", E(4000,55,1,300), C(480,100,2));

        //                                 wood plywood acryl leather anod metal fabric paper slate glass rubber mdf
        AddDiodeScaled(r, 40, new[] { 7000d,6000,4000,7000,3500,1500,8000,10000,4000,5000,4000,5000 }, new[] { 45d,45,40,25,85,100,20,15,70,45,55,50 }, new[] { 700d,700,350,800,0,0,1000,1200,0,0,600,800 }, new[] { 90d,100,95,70,0,0,55,45,0,0,90,90 }, new[] { 1,1,2,1,0,0,1,1,0,0,1,2 });
        AddDiodeScaled(r, 60, new[] { 9000d,8000,5000,9000,4500,2000,10000,12000,5000,6000,5000,7000 }, new[] { 40d,40,35,22,75,100,18,12,60,40,48,45 }, new[] { 1000d,1000,500,1200,0,0,1500,1500,0,0,900,700 }, new[] { 85d,100,90,60,0,0,50,40,0,0,80,90 }, new[] { 1,1,1,1,0,0,1,1,0,0,1,1 });
    }

    private static void AddDiodeScaled(List<MaterialRecipe> r, int watts, double[] engraveSpeed, double[] engravePower, double[] cutSpeed, double[] cutPower, int[] cutPasses)
    {
        var ids = new[] { "wood", "plywood", "acrylic", "leather", "anodized", "metal", "fabric", "paper", "slate", "glass", "rubber", "mdf" };
        var dpi = new[] { 300,300,300,300,300,200,300,400,300,300,300,300 };
        for (var i = 0; i < ids.Length; i++)
        {
            var note = ids[i] == "metal" ? "CerMark je povinný" : null;
            var compatibility = ids[i] is "acrylic" or "glass" or "metal" ? MaterialCompatibility.Suitable : MaterialCompatibility.Standard;
            var engrave = E(engraveSpeed[i], engravePower[i], ids[i] == "metal" && watts == 40 ? 3 : ids[i] == "metal" ? 2 : 1, dpi[i], note:note, compatibility:compatibility);
            RecipeValues? cut = cutSpeed[i] > 0 ? C(cutSpeed[i], cutPower[i], cutPasses[i], compatibility:ids[i] == "acrylic" ? MaterialCompatibility.Suitable : MaterialCompatibility.Standard) : null;
            Add(r, LaserTechnology.Diode, watts, ids[i], engrave, cut);
        }
    }

    private static void AddInfrared(List<MaterialRecipe> r, int watts)
    {
        var f = watts / 20d;
        Add(r, LaserTechnology.Infrared, watts, "wood", E(Round(3000*f),50,1,300,note:"IR 1064 nm karbonizuje hlouběji"), C(Round(300*f),95,2));
        Add(r, LaserTechnology.Infrared, watts, "plywood", E(Round(2800*f),52,1,300), C(Round(200*f),100,3));
        Add(r, LaserTechnology.Infrared, watts, "acrylic", E(Round(3000*f),55,1,300,note:"IR funguje i na čirém akrylu",compatibility:MaterialCompatibility.Suitable), C(Round(250*f),90,2,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Infrared, watts, "leather", E(Round(3500*f),38,1,300), C(Round(400*f),85,1));
        Add(r, LaserTechnology.Infrared, watts, "anodized", E(Round(2000*f),80,1,300,note:"Vhodné pro všechny barvy anodizace"));
        Add(r, LaserTechnology.Infrared, watts, "metal", E(Round(1500*f),100,1,200,note:"Přímé gravírování kovů bez pasty",compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Infrared, watts, "fabric", E(Round(4500*f),28,1,300), C(Round(600*f),70,1));
        Add(r, LaserTechnology.Infrared, watts, "paper", E(Round(6000*f),22,1,400), C(Round(700*f),60,1));
        Add(r, LaserTechnology.Infrared, watts, "slate", E(Round(2500*f),85,1,300));
        Add(r, LaserTechnology.Infrared, watts, "glass", E(Round(3500*f),55,1,300,compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Infrared, watts, "rubber", E(Round(2500*f),65,1,300), C(Round(350*f),95,2));
        Add(r, LaserTechnology.Infrared, watts, "mdf", E(Round(3000*f),58,1,300), C(Round(250*f),100,3));
    }

    private static void AddCo2(List<MaterialRecipe> r, int watts)
    {
        var f = watts / 40d;
        Add(r, LaserTechnology.Co2, watts, "wood", E(Round(5000*f),30,1,500,note:"Vynikající detail a světlý kontrast"), C(Round(600*f),80,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "plywood", E(Round(4500*f),32,1,500), C(Round(450*f),85,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "acrylic", E(Round(4000*f),25,1,500,note:"CO₂ je ideální pro akryl",compatibility:MaterialCompatibility.Excellent), C(Round(500*f),75,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "leather", E(Round(4500*f),22,1,400,compatibility:MaterialCompatibility.Excellent), C(Round(600*f),65,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "anodized", E(Round(2500*f),70,1,300,note:"Méně vhodné než IR pro kovy",compatibility:MaterialCompatibility.Suitable));
        Add(r, LaserTechnology.Co2, watts, "metal", E(Round(1000*f),100,2,200,note:"Nutná značicí pasta; IR je vhodnější",compatibility:MaterialCompatibility.Suitable,warning:"CO₂ na kovech vždy vyžaduje speciální značicí pastu."));
        Add(r, LaserTechnology.Co2, watts, "fabric", E(Round(6000*f),18,1,300,compatibility:MaterialCompatibility.Excellent), C(Round(800*f),55,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "paper", E(Round(8000*f),12,1,500,compatibility:MaterialCompatibility.Excellent), C(Round(1000*f),40,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "slate", E(Round(3000*f),65,1,300,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "glass", E(Round(4000*f),30,1,400,note:"Ideální pro mléčné leptání skla",compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "rubber", E(Round(3500*f),45,1,300,compatibility:MaterialCompatibility.Excellent), C(Round(500*f),70,1,compatibility:MaterialCompatibility.Excellent));
        Add(r, LaserTechnology.Co2, watts, "mdf", E(Round(4000*f),35,1,400,compatibility:MaterialCompatibility.Excellent), C(Round(400*f),80,1,compatibility:MaterialCompatibility.Excellent));
    }

    private static void Add(List<MaterialRecipe> recipes, LaserTechnology technology, int watts, string materialId, RecipeValues engrave, RecipeValues? cut = null)
    {
        AddOne(recipes, technology, watts, materialId, LayerMode.Fill, engrave);
        if (cut is not null) AddOne(recipes, technology, watts, materialId, LayerMode.Cut, cut);
    }

    private static void AddOne(List<MaterialRecipe> recipes, LaserTechnology technology, int watts, string materialId, LayerMode mode, RecipeValues value)
    {
        var material = Materials.Single(item => item.Id == materialId);
        var operation = mode == LayerMode.Fill ? "engrave" : "cut";
        recipes.Add(new MaterialRecipe($"builtin:{SchemaVersion}:{technology.ToString().ToLowerInvariant()}:{watts}:{materialId}:{operation}", materialId, material.Name, technology, watts, mode, value.Speed, value.Power, value.Passes, value.Dpi, value.Compatibility, value.Note, value.Warning));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static RecipeValues E(double speed, double power, int passes, int dpi, MaterialCompatibility compatibility = MaterialCompatibility.Standard, string? note = null, string? warning = null) => new(speed,power,passes,dpi,compatibility,note,warning);
    private static RecipeValues C(double speed, double power, int passes, MaterialCompatibility compatibility = MaterialCompatibility.Standard, string? note = null, string? warning = null) => new(speed,power,passes,null,compatibility,note,warning);
    private sealed record RecipeValues(double Speed, double Power, int Passes, int? Dpi, MaterialCompatibility Compatibility, string? Note, string? Warning);
}
