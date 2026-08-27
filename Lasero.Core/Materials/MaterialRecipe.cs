using Lasero.Core.Layers;

namespace Lasero.Core.Materials;

public sealed record MaterialRecipe(
    string Id,
    string MaterialId,
    string MaterialName,
    LaserTechnology Technology,
    int PowerWatts,
    LayerMode Mode,
    double SpeedMmPerMinute,
    double PowerPercent,
    int Passes,
    int? Dpi = null,
    MaterialCompatibility Compatibility = MaterialCompatibility.Standard,
    string? Note = null,
    string? Warning = null)
{
    public double FillLineIntervalMm => Dpi is > 0 ? 25.4 / Dpi.Value : 25.4 / 254;
    public string OperationName => Mode == LayerMode.Fill ? "Gravírování" : "Řezání";
    public string TechnologyName => Technology switch
    {
        LaserTechnology.Diode => "Diodový",
        LaserTechnology.Infrared => "Infračervený",
        LaserTechnology.Co2 => "CO₂",
        _ => Technology.ToString(),
    };
}
