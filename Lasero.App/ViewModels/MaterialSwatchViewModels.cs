using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Materials;

namespace Lasero.App.ViewModels;

/// <summary>One material rendered as a 4x4 test grid: speed down the Y axis, power across the X axis.
/// The speeds bracket the catalog recipe's own speed, so the recipe sits inside the grid rather than
/// at one edge and the operator can see what happens on either side of it.</summary>
public sealed partial class MaterialSwatchCardViewModel : ObservableObject
{
    private static readonly double[] SpeedFactors = [2.0, 1.25, 0.70, 0.35];

    public MaterialDefinition Material { get; }
    public IReadOnlyList<int> Powers { get; } = [30, 55, 80, 100];
    public IReadOnlyList<double> Speeds { get; }
    public ObservableCollection<MaterialSwatchCellViewModel> Cells { get; }

    [ObservableProperty] private MaterialSwatchCellViewModel? _selectedCell;

    public bool HasSelection => SelectedCell is not null;

    public MaterialSwatchCardViewModel(MaterialDefinition material, MaterialRecipe recipe)
    {
        Material = material;
        Speeds = SpeedFactors
            .Select(factor => Math.Max(50, Math.Round(recipe.SpeedMmPerMinute * factor / 50) * 50))
            .ToArray();
        var (surface, mark) = MaterialColors(material.Id);
        var slowest = Speeds.Min();
        var strongest = Powers.Max();
        Cells = new ObservableCollection<MaterialSwatchCellViewModel>(
            Speeds.SelectMany(speed => Powers.Select(power =>
                CreateCell(material.Id, recipe, speed, power, slowest, strongest, surface, mark))));
    }

    partial void OnSelectedCellChanged(MaterialSwatchCellViewModel? oldValue, MaterialSwatchCellViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
    }

    private static MaterialSwatchCellViewModel CreateCell(
        string materialId,
        MaterialRecipe recipe,
        double speed,
        int power,
        double slowestSpeed,
        int strongestPower,
        string surface,
        string mark)
    {
        var intensity = Intensity(speed, power, slowestSpeed, strongestPower);
        return new MaterialSwatchCellViewModel(
            materialId,
            speed,
            power,
            recipe.Passes,
            recipe.Dpi,
            intensity,
            // The tile itself only hazes toward the burn colour; the mark carries the full range.
            // Engraving does not repaint the whole surface, but the hotter tiles do come back
            // scorched, and without it the grid reads as sixteen identical squares.
            Blend(surface, mark, intensity * 0.16),
            Blend(surface, mark, intensity));
    }

    /// <summary>Relative dose, normalised so the slowest and strongest cell is exactly 1. Energy per
    /// unit length scales with power over speed, so that ratio — not the raw numbers — is what the
    /// grid shows. The 0.45 exponent is perceptual: without it the four fastest rows collapse into
    /// one indistinguishable band at the light end.</summary>
    internal static double Intensity(double speed, int power, double slowestSpeed, int strongestPower)
    {
        var dose = power / (double)strongestPower * (slowestSpeed / speed);
        return Math.Pow(Math.Clamp(dose, 0, 1), 0.45);
    }

    /// <summary>Surface and mark colour per material. Both directions occur: wood darkens, slate and
    /// anodized aluminium mark light. The pair is picked for contrast against each other, because a
    /// mark the operator cannot see in the swatch tells them nothing.</summary>
    private static (string Surface, string Mark) MaterialColors(string materialId) => materialId switch
    {
        "wood" => ("#C89A5B", "#2A1B0E"),
        "plywood" => ("#D9BD86", "#2E1F10"),
        "mdf" => ("#B08A5E", "#2A1C10"),
        "acrylic" => ("#6F8FA6", "#FFFFFF"),
        "leather" => ("#8A5A3B", "#2A170D"),
        "anodized" => ("#4A5C6B", "#F2F5F8"),
        "metal" => ("#B9C0C7", "#2B3138"),
        "fabric" => ("#D9CDBD", "#3B2E22"),
        "paper" => ("#EDE4D2", "#43301C"),
        "slate" => ("#4A5055", "#D8DDE1"),
        "glass" => ("#8FB4CB", "#FFFFFF"),
        "rubber" => ("#232527", "#A9AEB3"),
        _ => ("#C89A5B", "#2A1B0E"),
    };

    private static string Blend(string from, string to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        var (r1, g1, b1) = Channels(from);
        var (r2, g2, b2) = Channels(to);
        return string.Create(CultureInfo.InvariantCulture,
            $"#{Mix(r1, r2, t):X2}{Mix(g1, g2, t):X2}{Mix(b1, b2, t):X2}");
    }

    private static int Mix(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);

    private static (int R, int G, int B) Channels(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

public sealed partial class MaterialSwatchCellViewModel : ObservableObject
{
    public string MaterialId { get; }
    public double Speed { get; }
    public int Power { get; }
    public int Passes { get; }
    public int? Dpi { get; }
    public double Intensity { get; }
    public string SurfaceColor { get; }
    public string MarkColor { get; }
    public string AccessibleName => $"Rychlost {Speed:0} mm/min, výkon {Power} %";

    [ObservableProperty] private bool _isSelected;

    public MaterialSwatchCellViewModel(
        string materialId,
        double speed,
        int power,
        int passes,
        int? dpi,
        double intensity,
        string surfaceColor,
        string markColor)
    {
        MaterialId = materialId;
        Speed = speed;
        Power = power;
        Passes = passes;
        Dpi = dpi;
        Intensity = intensity;
        SurfaceColor = surfaceColor;
        MarkColor = markColor;
    }
}
