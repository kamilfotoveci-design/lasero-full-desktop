using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Lasero.Avalonia.Services;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

public enum ShellScreen
{
    Home,
    Projects,
    Materials,
    Design,
    Devices,
    Settings,
}

public sealed record MigrationArea(string Name, string State, string Detail);

// The app is Windows-only in practice already (GRBL serial I/O, DPAPI session storage); this just
// documents that explicitly at the shell root instead of fighting the platform-compat analyzer
// screen by screen. See AccountViewModel's own remarks.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ShellViewModel : ReactiveObject
{
    private ShellScreen currentScreen;

    public ShellViewModel(
        IMigrationInventory inventory,
        MaterialCatalogViewModel materials,
        ProjectBrowserViewModel projects,
        EditorViewModel editor,
        HomeViewModel home,
        ConnectionViewModel connection,
        JogViewModel jog,
        MachineStatusViewModel machineStatus,
        ConsoleViewModel console,
        SettingsViewModel settings,
        Func<DeviceWizardViewModel> deviceWizardFactory)
    {
        Materials = materials;
        Projects = projects;
        Editor = editor;
        Home = home;
        Connection = connection;
        Jog = jog;
        MachineStatus = machineStatus;
        Console = console;
        Settings = settings;
        this.deviceWizardFactory = deviceWizardFactory;

        OpenDeviceWizard = ReactiveCommand.Create(() =>
        {
            // A fresh instance per run — matches WPF's own "reopening always starts over" design.
            DeviceWizard = this.deviceWizardFactory();
            IsDeviceWizardOpen = true;
        });
        CloseDeviceWizard = ReactiveCommand.Create(() =>
        {
            IsDeviceWizardOpen = false;
            DeviceWizard = null;
        });
        // Matches WPF's OnCloseClick closing the modal SettingsWindow — the shell is screen-based, so
        // a successful save returns to Home instead. Also re-loads the deferred-commit safety toggles
        // every time Settings is opened, matching a fresh SettingsWindow re-reading them on construction.
        Settings.Saved += () => CurrentScreen = ShellScreen.Home;
        Home.OpenRecentProjectRequested += _ => CurrentScreen = ShellScreen.Design;
        var snapshot = inventory.Load();
        if (snapshot.IsSuccess)
        {
            MaterialCount = snapshot.Value.MaterialCount;
            RecipeCount = snapshot.Value.RecipeCount;
            KnownMachineCount = snapshot.Value.KnownMachineCount;
            CoreStatus = $"Načteno z {snapshot.Value.Source}";
        }
        else
        {
            CoreStatus = snapshot.Error;
        }

        MigrationAreas =
        [
            new("Aplikační shell", "Hotovo", "Avalonia okno, navigace, Fluent theme a DI."),
            new("Sdílené jádro", "Připojeno", "Materiály, recepty a profily strojů běží z Lasero.Core."),
            new("Knihovna materiálů", "Hotovo", "Filtrování receptů běží přes ReactiveUI a DynamicData."),
            new("Otevírání projektů", "Hotovo", "StorageProvider načte a ověří JSON i zabalený .lasero projekt."),
            new("Návrhové plátno", "Další", "Oddělit WPF rendering a vstupní adaptéry od modelu dokumentu."),
            new("Připojení zařízení", "Probíhá", "Připojení, ruční posun a konzola běží; úlohy a framing čekají na G-code pipeline."),
            new("Dialogy a soubory", "Probíhá", "Otevření projektu už používá Avalonia StorageProvider API."),
        ];

        GoHome = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Home; });
        GoProjects = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Projects; });
        GoMaterials = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Materials; });
        GoDesign = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Design; });
        GoDevices = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Devices; });
        GoSettings = ReactiveCommand.Create(() => { CurrentScreen = ShellScreen.Settings; });

        // Matches WPF MainViewModel's own default ("Nový projekt" until a project is created/opened,
        // then the opened project's name or its file name — see MainViewModel.cs NewProject/OpenProject).
        this.WhenAnyValue(x => x.Projects.Project)
            .Select(p => p is null ? "Nový projekt" : string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(p.Path) : p.Name)
            .ToProperty(this, x => x.ProjectName, out projectName, initialValue: "Nový projekt");
    }

    public string Title => "Lasero";
    public string ProjectName => projectName.Value;
    private readonly ObservableAsPropertyHelper<string> projectName;
    public MaterialCatalogViewModel Materials { get; }
    public ProjectBrowserViewModel Projects { get; }
    public EditorViewModel Editor { get; }
    public HomeViewModel Home { get; }
    public ConnectionViewModel Connection { get; }
    public JogViewModel Jog { get; }
    public MachineStatusViewModel MachineStatus { get; }
    public ConsoleViewModel Console { get; }
    public SettingsViewModel Settings { get; }
    private readonly Func<DeviceWizardViewModel> deviceWizardFactory;

    private DeviceWizardViewModel? deviceWizard;
    public DeviceWizardViewModel? DeviceWizard { get => deviceWizard; private set => this.RaiseAndSetIfChanged(ref deviceWizard, value); }

    private bool isDeviceWizardOpen;
    public bool IsDeviceWizardOpen { get => isDeviceWizardOpen; private set => this.RaiseAndSetIfChanged(ref isDeviceWizardOpen, value); }

    public ReactiveCommand<Unit, Unit> OpenDeviceWizard { get; }
    public ReactiveCommand<Unit, Unit> CloseDeviceWizard { get; }

    public int MaterialCount { get; }
    public int RecipeCount { get; }
    public int KnownMachineCount { get; }
    public string CoreStatus { get; } = "Jádro není dostupné";
    public IReadOnlyList<MigrationArea> MigrationAreas { get; }

    public ShellScreen CurrentScreen
    {
        get => currentScreen;
        private set
        {
            if (currentScreen == value) return;
            this.RaiseAndSetIfChanged(ref currentScreen, value);
            if (value == ShellScreen.Settings) Settings.LoadSafetyToggles();
            this.RaisePropertyChanged(nameof(IsHomeVisible));
            this.RaisePropertyChanged(nameof(IsProjectsVisible));
            this.RaisePropertyChanged(nameof(IsMaterialsVisible));
            this.RaisePropertyChanged(nameof(IsDesignVisible));
            this.RaisePropertyChanged(nameof(IsDevicesVisible));
            this.RaisePropertyChanged(nameof(IsSettingsVisible));
            this.RaisePropertyChanged(nameof(IsStandardShellVisible));
            this.RaisePropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsHomeVisible => CurrentScreen == ShellScreen.Home;
    public bool IsProjectsVisible => CurrentScreen == ShellScreen.Projects;
    public bool IsMaterialsVisible => CurrentScreen == ShellScreen.Materials;
    public bool IsDesignVisible => CurrentScreen == ShellScreen.Design;
    public bool IsDevicesVisible => CurrentScreen == ShellScreen.Devices;
    public bool IsSettingsVisible => CurrentScreen == ShellScreen.Settings;
    public bool IsStandardShellVisible => CurrentScreen != ShellScreen.Design;
    public bool IsPlaceholderVisible => CurrentScreen is ShellScreen.Design or ShellScreen.Devices or ShellScreen.Settings;

    public ReactiveCommand<Unit, Unit> GoHome { get; }
    public ReactiveCommand<Unit, Unit> GoProjects { get; }
    public ReactiveCommand<Unit, Unit> GoMaterials { get; }
    public ReactiveCommand<Unit, Unit> GoDesign { get; }
    public ReactiveCommand<Unit, Unit> GoDevices { get; }
    public ReactiveCommand<Unit, Unit> GoSettings { get; }
}
