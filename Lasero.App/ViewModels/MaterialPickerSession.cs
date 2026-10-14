using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.App.ViewModels;

/// <summary>
/// One pass through the Vzorník materiálů on behalf of a single operation (layer): the person picks a
/// swatch, reads the numbers, and only Aplikovat writes them. Closing the window any other way leaves
/// the layer exactly as it was. The session owns nothing but the target and the decision; the catalogue,
/// the filters and the selected recipe stay on <see cref="MaterialsViewModel"/>, so the standalone
/// Vzorník and the picker show the same swatches.
///
/// The numbers go through <see cref="LayerSettings.ApplyRecipe"/>, the same call the previous recipe
/// menu and the Kamil recommendation use, so the layer records the material label as one unit and the
/// dirty/undo behaviour is identical.
/// </summary>
public sealed partial class MaterialPickerSession : ObservableObject, IDisposable
{
    public const string NothingSelectedReason = "Nejdřív je potřeba zvolit odstín ve vzorníku";
    public const string ScrapNote = "Před ostrou úlohou je vždy potřeba hodnoty ověřit na odřezku.";

    private readonly MaterialsViewModel _materials;

    public MaterialPickerSession(MaterialsViewModel materials, LayerSettings target)
    {
        _materials = materials;
        Target = target;
        _materials.PreselectFor(target);
        _materials.PropertyChanged += OnMaterialsPropertyChanged;
        target.PropertyChanged += OnTargetPropertyChanged;
    }

    public LayerSettings Target { get; }

    /// <summary>Raised once, after the person applied a recipe or cancelled; the window closes on it.</summary>
    public event EventHandler? Finished;

    /// <summary>True only after a recipe was written to the target.</summary>
    public bool Applied { get; private set; }

    public MaterialRecipe? SelectedRecipe => _materials.SelectedRecipe;

    public bool CanApply => SelectedRecipe is not null;

    /// <summary>Why Aplikovat is disabled; empty once it is enabled.</summary>
    public string ApplyHint => CanApply
        ? string.Empty
        : NothingSelectedReason;

    /// <summary>Applying a recipe also sets the operation mode, so a different mode is said out loud.</summary>
    public bool HasModeChange => SelectedRecipe is { } recipe && recipe.Mode != Target.Mode;

    public string ModeChangeNote => SelectedRecipe is { } recipe && recipe.Mode != Target.Mode
        ? $"Režim vrstvy se změní z „{OperationLabel(Target.Mode)}“ na „{OperationLabel(recipe.Mode)}“."
        : string.Empty;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (SelectedRecipe is not { } recipe) return;
        Write(recipe.Mode, recipe.SpeedMmPerMinute, recipe.PowerPercent, recipe.Passes,
            recipe.FillLineIntervalMm, recipe.MaterialName);
    }

    /// <summary>A saved personal recipe is applied the way the old recipe menu did: immediately.</summary>
    [RelayCommand]
    private void ApplyPreset(MaterialPreset? preset)
    {
        if (preset is null) return;
        Write(preset.Mode, preset.Speed, preset.Power, preset.Passes, preset.FillLineIntervalMm, preset.Name);
    }

    [RelayCommand]
    private void Cancel() => Finish();

    private void Write(LayerMode mode, double speed, double power, int passes, double fillLineIntervalMm, string label)
    {
        if (Applied) return;
        Target.ApplyRecipe(mode, speed, power, passes, fillLineIntervalMm, label);
        Applied = true;
        Finish();
    }

    private void Finish() => Finished?.Invoke(this, EventArgs.Empty);

    private void OnMaterialsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MaterialsViewModel.SelectedRecipe)) return;
        RaiseSelectionChanged();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayerSettings.Mode)) RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedRecipe));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyHint));
        OnPropertyChanged(nameof(HasModeChange));
        OnPropertyChanged(nameof(ModeChangeNote));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string OperationLabel(LayerMode mode) => mode switch
    {
        LayerMode.Cut => "Řezání",
        LayerMode.Fill => "Gravírování",
        _ => "Vyplnění s obrysem",
    };

    public void Dispose()
    {
        _materials.PropertyChanged -= OnMaterialsPropertyChanged;
        Target.PropertyChanged -= OnTargetPropertyChanged;
        // The standalone Vzorník shares the selection; leave it clean rather than showing a stale pick.
        _materials.SelectedRecipe = null;
    }
}
