namespace Lasero.Core.Machines;

public enum CapabilitySupport { Supported, Unsupported, Unresolved, NotApplicable }
public enum CapabilityVerification { Unverified, OfflineTested, HardwareVerified }
public enum MachineCapability { Connect, Identify, Status, Jog, Home, Frame, Vector, Raster, Pause, Resume, Cancel, Focus, AirAssist, SourceSelection, Accessories }
public sealed record CapabilityEvidence(CapabilitySupport Support, CapabilityVerification Verification);

/// <summary>Compatibility metadata, deliberately separate from executable settings and saved IDs.
/// No dimensions, speed, power or module defaults are inferred from these entries.</summary>
public sealed record MachineCompatibility(string Id, string DisplayName, string Limitation)
{
    public bool AllowsDirectConnection => Id == MachineCompatibilityCatalog.ExistingGrblId;
    public CapabilityEvidence GetCapability(MachineCapability capability) => new(
        CapabilitySupport.Unresolved, CapabilityVerification.Unverified);
}

public static class MachineCompatibilityCatalog
{
    public const string ExistingGrblId = "existing-grbl";
    public static IReadOnlyList<MachineCompatibility> All { get; } = Array.AsReadOnly(new[]
    {
        new MachineCompatibility(ExistingGrblId, "Stávající GRBL / simulátor", "Stávající ruční připojení GRBL. Identita modelu ani ověření na hardwaru se neodvozují z rozměrů."),
        new MachineCompatibility("algolaser-alpha-mk2-20w", "AlgoLaser MK2 / Alpha MK2", "Nejprve potvrďte přesný model a modul (20 W / 40 W). Firmware, identifikace a výkon nejsou ověřeny; přímé připojení není dostupné."),
        new MachineCompatibility("algolaser-pixi", "AlgoLaser PIXI", "Moduly 3 W / 5 W / 10 W. Je nutné ověřit firmware, identifikaci a nastavení konkrétního modulu; přímé připojení není dostupné."),
        new MachineCompatibility("xtool-f2", "xTool F2 (standard)", "Přímé rozhraní není ověřeno. SVG/DXF nebo obrázek lze importovat do xTool Studio; rozměry a parametry ověřte ve Studiu. Nejde o ovládání stroje z LASERO."),
        new MachineCompatibility("xtool-s1", "xTool S1", "20 W / 40 W mají odlišnou plochu. Neověřené příkazy, firmware a souřadnice brání přímému připojení. Kalibrace ukazatele je zatím testována pouze offline."),
        new MachineCompatibility("algolaser-diy-kit-mk2", "AlgoLaser DIY KIT MK2", "Pouze MK2, moduly 5 W / 10 W. Ověřte konkrétní firmware, USB rozhraní, souřadnice a výkon. Přímé připojení není dostupné."),
    });

    public static MachineCompatibility Get(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Neznámý profil stroje.", nameof(id));

    public static void RequireDirectConnection(string id)
    {
        var entry = Get(id);
        if (!entry.AllowsDirectConnection) throw new InvalidOperationException(entry.Limitation);
    }
}