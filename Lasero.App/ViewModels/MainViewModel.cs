using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.App.Thumbnails;
using Lasero.Core.History;
using Microsoft.Win32;

namespace Lasero.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProjectRecoveryStore _recoveryStore;
    private readonly AppSettingsStore _settingsStore;

    /// <summary>Exposed so the window can persist editor chrome (panel sizing). MainWindow is
    /// constructed directly rather than resolved from the container, so it has no other route to
    /// the store.</summary>
    public AppSettingsStore SettingsStore => _settingsStore;
    private readonly RecentProjectsStore _recentProjectsStore;
    private readonly JobHistoryStore _jobHistoryStore;
    private readonly Func<DeviceWizardViewModel> _deviceWizardFactory;

    public ConnectionViewModel Connection { get; }
    public DeviceSetupViewModel DeviceSetup { get; }
    public MachineStatusViewModel MachineStatus { get; }
    public JogViewModel Jog { get; }
    public ConsoleViewModel Console { get; }
    public SceneViewModel Scene { get; }
    public GCodeViewModel GCode { get; }
    public AccountViewModel Account { get; }
    public HomeViewModel Home { get; }
    public MaterialsViewModel Materials { get; }
    public ChatViewModel Chat { get; }
    public AppSettings Settings => _settingsStore.Current;

    [ObservableProperty] private string _projectName = "Nový projekt";
    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private AppScreen _currentScreen = AppScreen.Home;

    /// <summary>Optional, free-text material for the piece currently loaded — there's no material
    /// library yet, so this is the only source job history has for its material stat/list.</summary>
    [ObservableProperty] private string? _materialName;

    public bool HasRecoverySnapshot => _recoveryStore.HasSnapshot;
    public DateTime? RecoveryTimestampUtc => _recoveryStore.LastWriteTimeUtc;

    /// <summary>True once there's a real project loaded (saved-and-path-known, or a restored/edited
    /// snapshot that hasn't been saved yet) — drives the Home dashboard's "continue" hero vs. its
    /// generic welcome. Deliberately not just "ProjectPath is not null", so a just-restored autosave
    /// still personalizes the hero even before its first save.</summary>
    public bool HasOpenProject => IsDirty || !string.IsNullOrWhiteSpace(ProjectPath);

    /// <summary>A fresh wizard per run — it holds scan results and a step position, and reopening it
    /// should start over rather than resume wherever the operator abandoned it last time.</summary>
    public DeviceWizardViewModel CreateDeviceWizard() => _deviceWizardFactory();

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(HasOpenProject));
    partial void OnProjectPathChanged(string? value) => OnPropertyChanged(nameof(HasOpenProject));

    public MainViewModel(
        ConnectionViewModel connection,
        MachineStatusViewModel machineStatus,
        JogViewModel jog,
        ConsoleViewModel console,
        SceneViewModel scene,
        GCodeViewModel gcode,
        AccountViewModel account,
        HomeViewModel home,
        MaterialsViewModel materials,
        ChatViewModel chat,
        ProjectRecoveryStore recoveryStore,
        AppSettingsStore settingsStore,
        RecentProjectsStore recentProjectsStore,
        JobHistoryStore jobHistoryStore,
        Func<DeviceWizardViewModel> deviceWizardFactory)
    {
        Connection = connection;
        DeviceSetup = new DeviceSetupViewModel(connection, () => DeviceWizardRequested?.Invoke());
        MachineStatus = machineStatus;
        Jog = jog;
        Console = console;
        Scene = scene;
        GCode = gcode;
        Account = account;
        Home = home;
        Materials = materials;
        Chat = chat;
        _recoveryStore = recoveryStore;
        _settingsStore = settingsStore;
        _recentProjectsStore = recentProjectsStore;
        _jobHistoryStore = jobHistoryStore;
        _deviceWizardFactory = deviceWizardFactory;
        Connection.BaudRate = Settings.Device.BaudRate;
        if (!string.IsNullOrWhiteSpace(Settings.Device.Port) && Connection.AvailablePorts.Contains(Settings.Device.Port))
            Connection.SelectedPort = Settings.Device.Port;
        Jog.StepSizeMm = Settings.Machine.JogStepMm;
        Jog.JogFeedRate = Settings.Machine.JogFeedRateMmPerMinute;
        Jog.EnableZAxis = Settings.Machine.EnableZAxis;
        Jog.InvertZAxis = Settings.Machine.InvertZAxis;
        Jog.ZStepSizeMm = Settings.Machine.ZJogStepMm;
        Jog.ZJogFeedRate = Settings.Machine.ZJogFeedRateMmPerMinute;
        Scene.Changed += OnSceneChanged;
        GCode.PlacementChanged += OnSceneChanged;
        Home.OpenRecentProjectRequested += OnOpenRecentProjectRequested;
        GCode.JobCompleted += OnJobCompleted;
        Connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsConnected) &&
            Connection.IsConnected &&
            Settings.Safety.ShowMachineStatusAfterConnect)
        {
            CurrentScreen = AppScreen.Device;
        }
    }

    [RelayCommand]
    private void ShowHome() => CurrentScreen = AppScreen.Home;

    [RelayCommand]
    private void ShowDesigner()
    {
        Scene.ActiveTool = DesignerTool.Select;
        CurrentScreen = AppScreen.Designer;
    }

    [RelayCommand]
    private void ShowDevice() => CurrentScreen = AppScreen.Device;

    /// <summary>Raised instead of opening a window here — the ViewModel has no business knowing about
    /// Window types, and MainWindow already owns every other dialog the app shows.</summary>
    public event Action? DeviceWizardRequested;

    [RelayCommand]
    private void OpenDeviceWizard() => DeviceWizardRequested?.Invoke();

    [RelayCommand]
    private void ShowChat() => CurrentScreen = AppScreen.Chat;

    private void OnOpenRecentProjectRequested(string path)
    {
        if (!ConfirmProjectReplacement()) return;
        LoadProject(path);
    }

    private void OnJobCompleted(object? sender, JobCompletedEventArgs e) => _jobHistoryStore.Append(new JobHistoryEntry(
        e.Name,
        string.IsNullOrWhiteSpace(MaterialName) ? null : MaterialName.Trim(),
        e.DurationSeconds,
        e.CompletedUtc));

    /// <summary>Renders a fresh thumbnail from the live scene and records/moves this project to the
    /// front of the Home dashboard's recent list — called after every successful save or open. A
    /// thumbnail-render failure is logged but never blocks the save/open itself.</summary>
    private void UpdateRecentProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath)) return;

        string? thumbnailPath = null;
        try
        {
            thumbnailPath = ComputeThumbnailPath(ProjectPath);
            SceneThumbnailRenderer.RenderToFile(Scene.Scene, thumbnailPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Nepodařilo se vykreslit náhled projektu");
            thumbnailPath = null;
        }

        _recentProjectsStore.Touch(ProjectPath, ProjectName, thumbnailPath);
    }

    private static string ComputeThumbnailPath(string projectPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant())));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lasero", "thumbnails", $"{hash}.png");
    }

    public void SaveSettings()
    {
        Settings.Device.Port = Connection.SelectedPort;
        Settings.Device.BaudRate = Connection.BaudRate;
        Settings.Machine.JogStepMm = Jog.StepSizeMm;
        Settings.Machine.JogFeedRateMmPerMinute = Jog.JogFeedRate;
        Settings.Machine.EnableZAxis = Jog.EnableZAxis;
        Settings.Machine.InvertZAxis = Jog.InvertZAxis;
        Settings.Machine.ZJogStepMm = Jog.ZStepSizeMm;
        Settings.Machine.ZJogFeedRateMmPerMinute = Jog.ZJogFeedRate;
        _settingsStore.Save();
    }

    private void OnSceneChanged() => IsDirty = true;

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmProjectReplacement()) return;
        Scene.ResetDocument();
        ProjectName = "Nový projekt";
        ProjectPath = null;
        MaterialName = null;
        _recoveryStore.Discard();
        GCode.ClearDocument();
        IsDirty = false;
        GCode.LastMessage = "Nový projekt je připraven.";
        CurrentScreen = AppScreen.Designer;
    }

    [RelayCommand]
    private void SaveProject() => TrySaveProject();

    public bool TrySaveProject()
    {
        var path = ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Projekt Lasero (*.lasero)|*.lasero",
                DefaultExt = ".lasero",
                FileName = ProjectName
            };
            if (dialog.ShowDialog() != true) return false;
            path = dialog.FileName;
        }

        try
        {
            var project = CreateProjectSnapshot();
            ProjectFileSerializer.Save(path, project);
            ProjectPath = path;
            ProjectName = Path.GetFileNameWithoutExtension(path);
            IsDirty = false;
            _recoveryStore.Discard();
            GCode.LastMessage = "Projekt byl uložen.";
            UpdateRecentProject();
            return true;
        }
        catch (Exception ex)
        {
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Projekt se nepodařilo uložit",
                ex.Message,
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
            return false;
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        var dialog = new OpenFileDialog { Filter = "Projekt Lasero (*.lasero)|*.lasero|Všechny soubory (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        if (!ConfirmProjectReplacement()) return;
        LoadProject(dialog.FileName);
    }

    public void OpenProjectFromShell(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Projekt nebyl nalezen",
                "Vybraný soubor už na tomto místě není. Otevřete projekt přímo z Lasero a zvolte jeho nové umístění.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Warning));
            return;
        }

        LoadProject(fullPath);
    }

    private void LoadProject(string path)
    {
        try
        {
            var project = ProjectFileSerializer.Load(path);
            Scene.LoadProject(project);
            GCode.RestorePlacement(project.Placement);
            ProjectPath = path;
            ProjectName = string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileNameWithoutExtension(path) : project.Name;
            IsDirty = false;
            _recoveryStore.Discard();
            GCode.RegenerateFromSceneCommand.Execute(null);
            GCode.LastMessage = "Projekt byl otevřen.";
            UpdateRecentProject();
            CurrentScreen = AppScreen.Designer;
        }
        catch (Exception ex)
        {
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Projekt se nepodařilo otevřít",
                ex.Message,
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    public bool TryRestoreRecoverySnapshot()
    {
        try
        {
            var project = _recoveryStore.TryLoad();
            if (project is null) return false;
            Scene.LoadProject(project);
            GCode.RestorePlacement(project.Placement);
            ProjectName = string.IsNullOrWhiteSpace(project.Name) ? "Obnovený projekt" : project.Name;
            ProjectPath = null;
            IsDirty = true;
            GCode.RegenerateFromSceneCommand.Execute(null);
            GCode.LastMessage = "Projekt byl obnoven z automatické zálohy.";
            return true;
        }
        catch (Exception ex)
        {
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Zálohu se nepodařilo obnovit",
                ex.Message,
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
            return false;
        }
    }

    public void DiscardRecoverySnapshot() => _recoveryStore.Discard();

    public void SaveRecoverySnapshot()
    {
        if (!IsDirty) return;
        try
        {
            var project = CreateProjectSnapshot();
            _recoveryStore.Save(project);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Autosave snapshot failed");
        }
    }

    private LaseroProjectFile CreateProjectSnapshot()
    {
        var project = Scene.CreateProject();
        project.Name = ProjectName;
        project.Placement = GCode.HasPlacementOrigin
            ? new ProjectJobPlacement
            {
                Mode = GCode.PlacementMode,
                Anchor = GCode.OriginAnchor,
                ReferenceX = GCode.PlacementReferenceX,
                ReferenceY = GCode.PlacementReferenceY,
            }
            : null;
        return project;
    }

    private bool ConfirmProjectReplacement()
    {
        if (!IsDirty) return true;
        var decision = LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
            "Neuložené změny",
            "Aktuální projekt obsahuje změny, které ještě nejsou uložené.",
            "Uložit projekt",
            SecondaryText: "Pokračovat bez uložení",
            CancelText: "Zrušit",
            Tone: LaseroDialogTone.Warning));
        return decision switch
        {
            LaseroDialogChoice.Primary => TrySaveProject(),
            LaseroDialogChoice.Secondary => true,
            _ => false,
        };
    }
}
