using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Layers;
using Lasero.Core.Materials;
using Lasero.Core.LaseroApi;
using Serilog;

namespace Lasero.App.ViewModels;

/// <summary>Backs the Materials nav panel: a saved catalog of Speed/Power/Passes/Mode combos the user
/// can apply onto any layer row. Persists every live edit (not just add/duplicate/delete) so the
/// catalog behaves like a normal settings list, not something requiring an explicit save step.</summary>
public partial class MaterialsViewModel : ObservableObject
{
    private readonly MaterialPresetStore _store;
    private readonly AppSettingsStore _settingsStore;
    private readonly MaterialSyncClient _syncClient;
    private readonly AccountViewModel _account;

    public ObservableCollection<MaterialPreset> Presets { get; } = new();
    public ObservableCollection<MaterialRecipe> RecommendedRecipes { get; } = new();
    public IReadOnlyList<MaterialDefinition> MaterialDefinitions { get; } = MaterialCatalog.Materials;
    public IReadOnlyList<LaserTechnology> Technologies { get; } = Enum.GetValues<LaserTechnology>();
    public IReadOnlyList<int> PowerClasses { get; } = MaterialCatalog.PowerClasses;
    public IReadOnlyList<LayerMode> Operations { get; } = [LayerMode.Fill, LayerMode.Cut];
    public bool HasPresets => Presets.Count > 0;
    public bool HasRecommendedRecipes => RecommendedRecipes.Count > 0;
    public string ActiveProfileLabel => $"{TechnologyLabel(SelectedTechnology)} · {SelectedPowerWatts} W";

    [ObservableProperty] private LaserTechnology _selectedTechnology;
    [ObservableProperty] private int _selectedPowerWatts;
    [ObservableProperty] private LayerMode _selectedOperation = LayerMode.Fill;
    [ObservableProperty] private MaterialDefinition? _selectedMaterial;
    [ObservableProperty] private MaterialRecipe? _selectedRecipe;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string? _syncStatus;

    public MaterialsViewModel(MaterialPresetStore store, AppSettingsStore settingsStore,
        MaterialSyncClient syncClient, AccountViewModel account)
    {
        _store = store;
        _settingsStore = settingsStore;
        _syncClient = syncClient;
        _account = account;
        _account.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AccountViewModel.IsSignedIn) or nameof(AccountViewModel.IsOffline))
                SynchronizeCommand.NotifyCanExecuteChanged();
        };
        _selectedTechnology = settingsStore.Current.Machine.LaserTechnology;
        _selectedPowerWatts = settingsStore.Current.Machine.LaserPowerWatts;
        foreach (var preset in _store.Presets)
        {
            Presets.Add(preset);
            preset.PropertyChanged += OnPresetPropertyChanged;
        }
        RefreshRecommendedRecipes();
    }

    partial void OnSelectedTechnologyChanged(LaserTechnology value)
    {
        _settingsStore.Current.Machine.LaserTechnology = value;
        _settingsStore.Save();
        RefreshRecommendedRecipes();
        OnPropertyChanged(nameof(ActiveProfileLabel));
    }

    partial void OnSelectedPowerWattsChanged(int value)
    {
        _settingsStore.Current.Machine.LaserPowerWatts = value;
        _settingsStore.Save();
        RefreshRecommendedRecipes();
        OnPropertyChanged(nameof(ActiveProfileLabel));
    }

    partial void OnSelectedOperationChanged(LayerMode value) => RefreshRecommendedRecipes();
    partial void OnSelectedMaterialChanged(MaterialDefinition? value) => RefreshRecommendedRecipes();
    partial void OnSelectedRecipeChanged(MaterialRecipe? value)
    {
        OnPropertyChanged(nameof(HasSelectedRecipe));
        OnPropertyChanged(nameof(HasSelectedNote));
        OnPropertyChanged(nameof(HasSelectedWarning));
    }
    partial void OnSearchTextChanged(string value) => RefreshRecommendedRecipes();

    public bool HasSelectedRecipe => SelectedRecipe is not null;
    public bool HasSelectedNote => !string.IsNullOrWhiteSpace(SelectedRecipe?.Note);
    public bool HasSelectedWarning => !string.IsNullOrWhiteSpace(SelectedRecipe?.Warning);

    public IEnumerable<MaterialRecipe> RecipesFor(LayerMode mode) => MaterialCatalog.Find(SelectedTechnology, SelectedPowerWatts, mode);

    private void RefreshRecommendedRecipes()
    {
        var search = SearchText.Trim();
        var previousId = SelectedRecipe?.Id;
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
        SelectedRecipe = RecommendedRecipes.FirstOrDefault(recipe => recipe.Id == previousId)
            ?? RecommendedRecipes.FirstOrDefault();
        OnPropertyChanged(nameof(HasRecommendedRecipes));
    }

    [RelayCommand]
    private void ShowAllMaterials() => SelectedMaterial = null;

    [RelayCommand]
    private void SaveAsPersonal(MaterialRecipe? recipe)
    {
        if (recipe is null) return;
        var preset = MaterialPreset.Create($"{recipe.MaterialName} – {recipe.OperationName}", recipe.Mode,
            recipe.SpeedMmPerMinute, recipe.PowerPercent, recipe.Passes, recipe.FillLineIntervalMm);
        _store.Add(preset);
        preset.PropertyChanged += OnPresetPropertyChanged;
        Presets.Add(preset);
        OnPropertyChanged(nameof(HasPresets));
    }

    public static string TechnologyLabel(LaserTechnology technology) => technology switch
    {
        LaserTechnology.Diode => "Diodový laser",
        LaserTechnology.Infrared => "Infračervený laser",
        LaserTechnology.Co2 => "CO₂ laser",
        _ => technology.ToString(),
    };

    private bool CanSynchronize() => !IsSyncing && _account.IsSignedIn && !_account.IsOffline;

    [RelayCommand(CanExecute = nameof(CanSynchronize))]
    private async Task Synchronize()
    {
        IsSyncing = true;
        SyncStatus = "Synchronizuji vzorník…";
        try
        {
            var token = await _account.GetIdTokenAsync();
            var remote = await _syncClient.GetAsync(token);
            if (!_store.HasLocalChanges)
            {
                ReplacePersonalRecipes(remote.Items, remote.Revision);
            }
            else
            {
                IReadOnlyCollection<MaterialSyncItem> outgoing;
                if (_store.SyncRevision == remote.Revision)
                {
                    outgoing = ToSyncItems(Presets);
                }
                else
                {
                    outgoing = MergeLocalChanges(remote.Items);
                }

                MaterialSyncSnapshot saved;
                try
                {
                    saved = await _syncClient.PutAsync(token, remote.Revision, outgoing, "desktop");
                }
                catch (MaterialSyncConflictException conflict)
                {
                    var retryItems = MergeLocalChanges(conflict.ServerSnapshot.Items);
                    saved = await _syncClient.PutAsync(token, conflict.ServerSnapshot.Revision, retryItems, "desktop");
                }
                ReplacePersonalRecipes(saved.Items, saved.Revision);
            }
            SyncStatus = "Vzorník je synchronizovaný.";
        }
        catch (MaterialSyncConflictException conflict)
        {
            SyncStatus = $"Vzorník se změnil na jiném zařízení ({conflict.ServerSnapshot.Items.Count} receptů). Spusťte synchronizaci znovu.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Warning(ex, "Material synchronization failed");
            SyncStatus = "Synchronizace se nezdařila. Offline vzorník zůstává dostupný.";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void ReplacePersonalRecipes(IEnumerable<MaterialSyncItem> items, string? revision)
    {
        foreach (var preset in Presets) preset.PropertyChanged -= OnPresetPropertyChanged;
        var presets = items.Select(item => new MaterialPreset
        {
            Id = item.Id, Name = item.Name, Mode = item.Mode, Speed = item.Speed, Power = item.Power,
            Passes = item.Passes, FillLineIntervalMm = item.FillLineIntervalMm,
        }).ToList();
        _store.ReplaceFromSync(presets, revision);
        Presets.Clear();
        foreach (var preset in presets)
        {
            preset.PropertyChanged += OnPresetPropertyChanged;
            Presets.Add(preset);
        }
        OnPropertyChanged(nameof(HasPresets));
    }

    private static IReadOnlyCollection<MaterialSyncItem> ToSyncItems(IEnumerable<MaterialPreset> presets) => presets
        .Select(preset => new MaterialSyncItem(preset.Id, preset.Name, preset.Mode, preset.Speed,
            preset.Power, preset.Passes, preset.FillLineIntervalMm))
        .ToArray();

    private IReadOnlyCollection<MaterialSyncItem> MergeLocalChanges(IEnumerable<MaterialSyncItem> remoteItems)
    {
        var merged = remoteItems.ToDictionary(item => item.Id);
        var localItems = ToSyncItems(Presets);
        var localIds = localItems.Select(item => item.Id).ToHashSet();
        foreach (var deletedId in _store.LastSyncedIds.Where(id => !localIds.Contains(id)))
            merged.Remove(deletedId);
        foreach (var local in localItems) merged[local.Id] = local;
        return merged.Values.ToArray();
    }

    partial void OnIsSyncingChanged(bool value) => SynchronizeCommand.NotifyCanExecuteChanged();

    private void OnPresetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MaterialPreset preset && string.IsNullOrEmpty(preset.Error)) _store.Update(preset);
    }

    [RelayCommand]
    private void Add()
    {
        var preset = MaterialPreset.Create("Nový materiál", LayerMode.Cut, speed: 300, power: 80, passes: 1);
        _store.Add(preset);
        preset.PropertyChanged += OnPresetPropertyChanged;
        Presets.Add(preset);
        OnPropertyChanged(nameof(HasPresets));
    }

    [RelayCommand]
    private void Duplicate(MaterialPreset? preset)
    {
        if (preset is null) return;
        var copy = preset.Clone(preset.Name + " (kopie)");
        _store.Add(copy);
        copy.PropertyChanged += OnPresetPropertyChanged;
        Presets.Add(copy);
        OnPropertyChanged(nameof(HasPresets));
    }

    [RelayCommand]
    private void Delete(MaterialPreset? preset)
    {
        if (preset is null) return;
        var confirmed = LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
            "Odstranit materiál",
            $"Materiál „{preset.Name}“ bude odstraněn z vašeho vzorníku. Tuto akci lze vrátit pouze jeho opětovným vytvořením.",
            "Odstranit materiál",
            CancelText: "Ponechat",
            Tone: LaseroDialogTone.Danger,
            DestructivePrimary: true)) == LaseroDialogChoice.Primary;
        if (!confirmed) return;

        preset.PropertyChanged -= OnPresetPropertyChanged;
        _store.Remove(preset.Id);
        Presets.Remove(preset);
        OnPropertyChanged(nameof(HasPresets));
    }
}
