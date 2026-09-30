using Lasero.Core.Layers;

namespace Lasero.Core.Materials;

/// <summary>Ranges, defaults and plain-language hints for personal recipes. The ranges are the same
/// ones the inspector sliders use (power 0-100 %, speed 10-12000 mm/min), so a recipe can never hold a
/// value the layer inspector would refuse.</summary>
public static class MaterialRecipeRules
{
    public const double MinSpeed = 10;
    public const double MaxSpeed = 12000;
    public const double MinPower = 0;
    public const double MaxPower = 100;
    public const int MinPasses = 1;
    public const int MaxPasses = 100;

    public const string BaseName = "Nový materiál";

    public static double ClampSpeed(double value) => double.IsFinite(value) ? Math.Clamp(value, MinSpeed, MaxSpeed) : MinSpeed;
    public static double ClampPower(double value) => double.IsFinite(value) ? Math.Clamp(value, MinPower, MaxPower) : MinPower;
    public static int ClampPasses(int value) => Math.Clamp(value, MinPasses, MaxPasses);

    /// <summary>Conservative starting values, not a promise: they scale with the machine's laser power
    /// class and always need a test on a scrap piece.</summary>
    public static (double Speed, double Power, int Passes) DefaultsFor(LayerMode mode, int laserWatts)
    {
        var watts = Math.Max(1, laserWatts);
        if (mode == LayerMode.Cut)
        {
            var speed = ClampSpeed(Math.Round(watts * 20d / 10) * 10);
            var passes = watts <= 5 ? 3 : watts <= 10 ? 2 : 1;
            return (Math.Max(60, speed), 100, passes);
        }
        var fillSpeed = ClampSpeed(Math.Round(Math.Clamp(watts * 300d, 1500, 9000) / 100) * 100);
        return (fillSpeed, 30, 1);
    }

    public static string ModeLabel(LayerMode mode) => mode switch
    {
        LayerMode.Cut => "Řezání",
        LayerMode.Fill => "Gravírování",
        _ => "Vyplnění s obrysem",
    };

    public static string ModeHint(LayerMode mode) => mode switch
    {
        LayerMode.Cut => "Laser projede po obrysu tvaru a materiál prořízne.",
        LayerMode.Fill => "Laser vyplní celou plochu tvaru řádky, vhodné pro logo nebo text.",
        _ => "Laser nejdřív vyplní plochu a potom projede obrys.",
    };

    public const string CutSafetyHint = "Před řezáním si recept vyzkoušejte na odřezku.";

    /// <summary>First free "Nový materiál", "Nový materiál 2", ... ignoring case.</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = existing.Select(n => n.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
