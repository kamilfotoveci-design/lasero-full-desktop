using System.Collections.ObjectModel;
using System.Reactive;
using Lasero.Avalonia.Services;
using Lasero.Core.Layers;
using Lasero.Core.Materials;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// Port of Lasero.App/ViewModels/MaterialsViewModel.cs's offline catalog half — same explicit
/// rebuild-on-filter-change model as WPF (not a DynamicData reactive pipeline; WPF rebuilds
/// RecommendedRecipes/SwatchCards imperatively on every filter change, so this matches that exactly
/// rather than layering a different filtering mechanism on top).
///
/// NOT ported in this pass: "Moje recepty" (custom presets + Lasero-account cloud sync —
/// MaterialPresetStore/MaterialSyncClient, a separate subsystem depending on account sync that
/// doesn't exist in Avalonia yet). See MaterialsView's second tab for how that's flagged rather than
/// faked.
/// </summary>
public sealed class MaterialCatalogViewModel : ReactiveObject
{
    private readonly AppSettingsStore? settingsStore;

    public IReadOnlyList<LaserTechnology> Technologies { get; } = Enum.GetValues<LaserTechnology>();
    public IReadOnlyList<int> PowerClasses { get; } = MaterialCatalog.PowerClasses;
    public IReadOnlyList<LayerMode> Operations { get; } = [LayerMode.Fill, LayerMode.Cut];
    public IReadOnlyList<MaterialDefinition> MaterialDefinitions { get; } = MaterialCatalog.Materials;

    public ObservableCollection<MaterialRecipe> RecommendedRecipes { get; } = new();
    public ObservableCollection<MaterialSwatchCardViewModel> SwatchCards { get; } = new();

    private LaserTechnology selectedTechnology = LaserTechnology.Diode;
    private int selectedPowerWatts = 20;
    private LayerMode selectedOperation = LayerMode.Fill;
    private MaterialDefinition? selectedMaterial;
    private string searchText = string.Empty;
    private string previewText = "A";
    private MaterialRecipe? selectedRecipe;

    /// <summary>Doubles as both the catalog filter and the operator's default machine profile —
    /// matches Lasero.App's MaterialsViewModel exactly: SelectedTechnology/SelectedPowerWatts read
    /// from and persist to AppSettingsStore.Machine on every change (Settings' "Profil laseru" picker
    /// and this filter are the same underlying value in both apps, not two disconnected settings).
    /// The settingsStore parameter is optional only so existing parameterless-construction tests that
    /// don't care about persistence keep compiling; production/DI always supplies one.</summary>
    public MaterialCatalogViewModel(AppSettingsStore? settingsStore = null)
    {
        this.settingsStore = settingsStore;
        if (settingsStore is not null)
        {
            selectedTechnology = settingsStore.Current.Machine.LaserTechnology;
            selectedPowerWatts = settingsStore.Current.Machine.LaserPowerWatts;
        }

        SelectSwatchCell = ReactiveCommand.Create<MaterialSwatchCellViewModel>(DoSelectSwatchCell);
        ShowAllMaterials = ReactiveCommand.Create(() => { SelectedMaterial = null; });

        RefreshRecommendedRecipes();
    }

    public LaserTechnology SelectedTechnology
    {
        get => selectedTechnology;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedTechnology, value);
            if (settingsStore is not null)
            {
                settingsStore.Current.Machine.LaserTechnology = value;
                settingsStore.Save();
            }
            RefreshRecommendedRecipes();
        }
    }

    public int SelectedPowerWatts
    {
        get => selectedPowerWatts;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedPowerWatts, value);
            if (settingsStore is not null)
            {
                settingsStore.Current.Machine.LaserPowerWatts = value;
                settingsStore.Save();
            }
            RefreshRecommendedRecipes();
        }
    }

    public LayerMode SelectedOperation
    {
        get => selectedOperation;
        set { this.RaiseAndSetIfChanged(ref selectedOperation, value); RefreshRecommendedRecipes(); }
    }

    public MaterialDefinition? SelectedMaterial
    {
        get => selectedMaterial;
        set { this.RaiseAndSetIfChanged(ref selectedMaterial, value); RefreshRecommendedRecipes(); }
    }

    public string SearchText
    {
        get => searchText;
        set { this.RaiseAndSetIfChanged(ref searchText, value ?? string.Empty); RefreshRecommendedRecipes(); }
    }

    /// <summary>Text stamped into every swatch cell's preview mark — lets the operator see how their
    /// own text/logo would actually burn at each speed/power combination, not just an abstract square.</summary>
    public string PreviewText
    {
        get => previewText;
        set => this.RaiseAndSetIfChanged(ref previewText, value ?? string.Empty);
    }

    public MaterialRecipe? SelectedRecipe
    {
        get => selectedRecipe;
        set { this.RaiseAndSetIfChanged(ref selectedRecipe, value); this.RaisePropertyChanged(nameof(HasSelectedRecipe)); }
    }

    public bool HasSelectedRecipe => SelectedRecipe is not null;
    public bool HasRecommendedRecipes => RecommendedRecipes.Count > 0;
    public bool HasSwatchCards => SwatchCards.Count > 0;

    public ReactiveCommand<MaterialSwatchCellViewModel, Unit> SelectSwatchCell { get; }
    public ReactiveCommand<Unit, Unit> ShowAllMaterials { get; }

    /// <summary>Matches Lasero.App's MaterialsViewModel.RecipesFor — recipes for the current
    /// technology/power profile at a specific layer mode, used by the Inspector's inline "Změnit"
    /// picker (one layer's own Mode, not the screen's own SelectedOperation filter).</summary>
    public IEnumerable<MaterialRecipe> RecipesFor(LayerMode mode) => MaterialCatalog.Find(SelectedTechnology, SelectedPowerWatts, mode);

    public string ActiveProfileLabel => $"{TechnologyLabel(SelectedTechnology)} · {SelectedPowerWatts} W";

    public static string TechnologyLabel(LaserTechnology technology) => technology switch
    {
        LaserTechnology.Diode => "Diodový laser",
        LaserTechnology.Infrared => "Infračervený laser",
        LaserTechnology.Co2 => "CO₂ laser",
        _ => technology.ToString(),
    };

    private void RefreshRecommendedRecipes()
    {
        var search = SearchText.Trim();
        var matches = MaterialCatalog.Find(SelectedTechnology, SelectedPowerWatts, SelectedOperation)
            .Where(recipe => (SelectedMaterial is null || recipe.MaterialId == SelectedMaterial.Id) &&
                (search.Length == 0 ||
                recipe.MaterialName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                (recipe.Note?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                (recipe.Warning?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            .OrderBy(recipe => recipe.MaterialName)
            .ToArray();

        RecommendedRecipes.Clear();
        foreach (var recipe in matches) RecommendedRecipes.Add(recipe);

        SwatchCards.Clear();
        foreach (var recipe in matches)
        {
            var material = MaterialDefinitions.First(item => item.Id == recipe.MaterialId);
            SwatchCards.Add(new MaterialSwatchCardViewModel(material, recipe));
        }

        SelectedRecipe = null;
        this.RaisePropertyChanged(nameof(HasRecommendedRecipes));
        this.RaisePropertyChanged(nameof(HasSwatchCards));
    }

    private void DoSelectSwatchCell(MaterialSwatchCellViewModel? cell)
    {
        if (cell is null) return;

        foreach (var card in SwatchCards)
            card.SelectedCell = card.Cells.Contains(cell) ? cell : null;

        var recipe = MaterialCatalog.Find(SelectedTechnology, SelectedPowerWatts, SelectedOperation, cell.MaterialId).FirstOrDefault();
        if (recipe is null) return;

        // Never touch SelectedMaterial or SearchText here: both re-run RefreshRecommendedRecipes,
        // which rebuilds the cards and clears the recipe this command has just set.
        SelectedRecipe = recipe with
        {
            Id = $"swatch:{recipe.Id}:{cell.Speed:0}:{cell.Power}",
            SpeedMmPerMinute = cell.Speed,
            PowerPercent = cell.Power,
        };
    }
}
