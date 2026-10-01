using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Layers;

namespace Lasero.Core.Materials;

/// <summary>
/// One saved "what worked last time" catalog entry — a flat Speed/Power/Passes/Mode combo a user can
/// apply straight onto a LayerSettings row. Deliberately flat (no Material/Thickness/Machine nesting):
/// there's no second dimension (per-machine variance) to justify that structure yet. Mutable
/// ObservableObject (not an immutable record), matching LayerSettings exactly, since both need live
/// inline editing in a table — CommunityToolkit's generated properties serialize fine with
/// System.Text.Json like every other persisted MVVM model in this app.
/// </summary>
public sealed partial class MaterialPreset : ObservableObject, System.ComponentModel.IDataErrorInfo
{
    public required Guid Id { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private LayerMode _mode = LayerMode.Cut;
    [ObservableProperty] private double _speed = 300;
    [ObservableProperty] private double _power = 80;
    [ObservableProperty] private int _passes = 1;

    /// <summary>Fill-mode scan line spacing in mm (25.4 / DPI) — irrelevant for Cut presets.</summary>
    [ObservableProperty] private double _fillLineIntervalMm = 25.4 / 254;

    /// <summary>Set by the owning list when another recipe already uses this name (not persisted).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [ObservableProperty] private bool _hasDuplicateName;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ValidationMessage => Error;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidationMessage => !string.IsNullOrEmpty(Error);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ModeHint => MaterialRecipeRules.ModeHint(Mode);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ValidationMessage) or nameof(HasValidationMessage) or nameof(ModeHint)) return;
        if (e.PropertyName == nameof(Mode)) OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    public string Error
    {
        get
        {
            var properties = new[] { nameof(Name), nameof(Speed), nameof(Power), nameof(Passes), nameof(FillLineIntervalMm) };
            return properties.Select(property => this[property]).FirstOrDefault(error => !string.IsNullOrEmpty(error)) ?? string.Empty;
        }
    }

    public string this[string columnName] => columnName switch
    {
        nameof(Name) when string.IsNullOrWhiteSpace(Name) => "Zadejte název materiálu.",
        nameof(Name) when Name.Trim().Length > 80 => "Název může mít nejvýše 80 znaků.",
        nameof(Name) when HasDuplicateName => "Recept s tímto názvem už máte. Zvolte jiný název.",
        nameof(Speed) when !double.IsFinite(Speed) || Speed is < MaterialRecipeRules.MinSpeed or > MaterialRecipeRules.MaxSpeed =>
            "Rychlost musí být mezi 10 a 12000 mm/min.",
        nameof(Power) when !double.IsFinite(Power) || Power is < MaterialRecipeRules.MinPower or > MaterialRecipeRules.MaxPower =>
            "Výkon musí být mezi 0 a 100 %.",
        nameof(Passes) when Passes is < MaterialRecipeRules.MinPasses or > MaterialRecipeRules.MaxPasses =>
            "Počet průchodů musí být alespoň 1 a nejvýše 100.",
        nameof(FillLineIntervalMm) when !double.IsFinite(FillLineIntervalMm) || FillLineIntervalMm <= 0 => "Rozteč řádků musí být větší než 0 mm.",
        _ => string.Empty,
    };

    public static MaterialPreset Create(string name, LayerMode mode, double speed, double power, int passes, double? fillLineIntervalMm = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Mode = mode,
        Speed = speed,
        Power = power,
        Passes = passes,
        FillLineIntervalMm = fillLineIntervalMm ?? 25.4 / 254,
    };

    public MaterialPreset Clone(string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name ?? Name,
        Mode = Mode,
        Speed = Speed,
        Power = Power,
        Passes = Passes,
        FillLineIntervalMm = FillLineIntervalMm,
    };
}
