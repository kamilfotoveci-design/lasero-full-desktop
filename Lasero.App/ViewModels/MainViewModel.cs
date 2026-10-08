using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.App.Thumbnails;
using Lasero.Core.History;
using Lasero.Core.Machines;
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
    public JobStatusViewModel JobStatus { get; }
    public MachineStatusViewModel MachineStatus { get; }
    public JogViewModel Jog { get; }
    public ConsoleViewModel Console { get; }
    public SceneViewModel Scene { get; }
    public GCodeViewModel GCode { get; }
    public AccountViewModel Account { get; }
    public HomeViewModel Home { get; }
    public MaterialsViewModel Materials { get; }
    public ChatViewModel Chat { get; }

    /// <summary>The floating assistant. One instance for the whole shell, so moving between screens
    /// never tears down the conversation.</summary>
    public KamilAssistantViewModel Kamil { get; }
    public AppSettings Settings => _settingsStore.Current;

    private void OnKamilPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KamilAssistantViewModel.State)) return;
        // Hidden is a view-model-only state with no UI path; it says nothing about how the operator
        // left the panel.
        if (Kamil.State == KamilAssistantState.Hidden) return;
        var expanded = Kamil.State == KamilAssistantState.Expanded;
        if (Settings.Workspace.AssistantExpanded == expanded) return;
        try
        {
            Settings.Workspace.AssistantExpanded = expanded;
            _settingsStore.Save();
        }
        catch (Exception ex)
        {
            // Remembering the panel shape is a convenience and must never interrupt the session.
            Serilog.Log.Warning(ex, "Failed to persist Kamil panel state");
        }
    }

    [ObservableProperty] private string _projectName = "Nový projekt";
    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private AppScreen _currentScreen = AppScreen.Home;

    /// <summary>Optional, free-text material for the piece currently loaded — there's no material
    /// library yet, so this is the only source job history has for its material stat/list.</summary>
    [ObservableProperty] private string? _materialName;

    /// <summary>The status strip's machine badge: resolved machine state, not just link state.</summary>
    public MachineBadgeInfo StripMachineBadge => MachineBadge.For(
        MachineStatus.DisplayState,
        Connection.ConnectionError,
        Connection.IsConnected && Lasero.Core.Grbl.VirtualGrblTransport.IsVirtualPort(Connection.SelectedPort));

    public string MachineBadgeLabel => StripMachineBadge.Label;
    public Components.StatePillKind MachineBadgeKind => StripMachineBadge.Kind;

    private void NotifyMachineBadge()
    {
        OnPropertyChanged(nameof(StripMachineBadge));
        OnPropertyChanged(nameof(MachineBadgeLabel));
        OnPropertyChanged(nameof(MachineBadgeKind));
    }

    public bool HasRecoverySnapshot => _recoveryStore.HasSnapshot;
    public DateTime? RecoveryTimestampUtc => _recoveryStore.LastWriteTimeUtc;

    /// <summary>True once there's a real project loaded (saved-and-path-known, or a restored/edited
    /// snapshot that hasn't been saved yet) — drives the Home dashboard's "continue" hero vs. its
    /// generic welcome. Deliberately not just "ProjectPath is not null", so a just-restored autosave
    /// still personalizes the hero even before its first save.</summary>
    public bool HasOpenProject => IsDirty || !string.IsNullOrWhiteSpace(ProjectPath);

    /// <summary>First-run guidance for the signed-in account: welcome, tour progress, micro-tips.</summary>
    public Lasero.App.Tour.GuidanceService Guidance { get; }

    /// <summary>Raised when the operator asks for the guided tour again (Home, Settings). The window owns
    /// the overlay; the view model only carries the request, same idiom as DeviceWizardRequested.</summary>
    public event Action? TourRequested;

    [RelayCommand]
    private void ReplayTour() => TourRequested?.Invoke();

    /// <summary>
    /// Points the guidance service at the current account and decides, once, whether this is somebody
    /// new (welcome owed) or somebody who has been here before. "Been here" means any sign of earlier
    /// use: recent project history, a recovery snapshot, an open project, or the old onboarding marker.
    /// Call it when a window opens for an account and after every sign-in, before the recovery prompt.
    /// </summary>
    public void EvaluateGuidanceForAccount(bool legacyOnboardingMarkerPresent)
    {
        var hasPriorWork = _recentProjectsStore.Recent.Count > 0
            || HasRecoverySnapshot
            || HasOpenProject
            || legacyOnboardingMarkerPresent;
        Guidance.SwitchAccount(Account.UserId, hasPriorWork);
    }

    /// <summary>A fresh wizard per run — it holds scan results and a step position, and reopening it
    /// should start over rather than resume wherever the operator abandoned it last time.</summary>
    public DeviceWizardViewModel CreateDeviceWizard()
    {
        var wizard = _deviceWizardFactory();
        // The status-strip Připojit hands over here when it could not choose a controller alone; the
        // wizard then starts the search itself so the operator does not have to click twice.
        wizard.AutoStartRequested = _autoStartNextWizard;
        // The strip already probed the ports; the wizard shows that result rather than opening every
        // port a second time.
        wizard.PreScanResult = _pendingScan;
        _autoStartNextWizard = false;
        _pendingScan = null;
        return wizard;
    }

    private bool _autoStartNextWizard;
    private GrblPortScanResult? _pendingScan;

    /// <summary>The assistant's context follows the shell. Switching screens updates what Kamil is
    /// told, and never touches the conversation.</summary>
    partial void OnCurrentScreenChanged(AppScreen value)
    {
        Kamil.ScreenLabel = DescribeScreen(value);
        NotifyScreenChrome();
    }

    /// <summary>Strip and title-bar visibility per screen; the rules live in <see cref="ScreenChrome"/>.</summary>
    public bool ShowStripJobDetails => ScreenChrome.ShowJobDetails(CurrentScreen, GCode.JobState);
    public bool ShowStripJobActionZone => ScreenChrome.ShowJobActionZone(CurrentScreen, GCode.JobState);
    public bool ShowDeviceSettingsShortcut => ScreenChrome.ShowDeviceSettingsShortcut(CurrentScreen);
    public bool ShowStripConnect => ScreenChrome.ShowStripConnect(CurrentScreen);

    /// <summary>Set by the window when it is narrower than <see cref="ScreenChrome.CompactWidth"/>.</summary>
    [ObservableProperty] private bool _isCompactChrome;

    partial void OnIsCompactChromeChanged(bool value) => NotifyScreenChrome();

    public bool ShowStripJobBadge => ScreenChrome.ShowJobBadge(CurrentScreen, GCode.JobState, IsCompactChrome);

    /// <summary>The quiet next-step line under the Home and Zařízení headers; see <see cref="GuidanceText.ScreenNextStep"/>.</summary>
    public string? ScreenNextStep => GuidanceText.ScreenNextStep(new ScreenHintContext(
        CurrentScreen,
        GCode.IsJobActive,
        Connection.IsConnected,
        Connection.IsConnecting,
        Scene.Objects.Count > 0,
        HasOpenProject,
        Home.HasRecentProjects));

    private void NotifyScreenChrome()
    {
        OnPropertyChanged(nameof(ShowStripJobDetails));
        OnPropertyChanged(nameof(ShowStripJobActionZone));
        OnPropertyChanged(nameof(ShowDeviceSettingsShortcut));
        OnPropertyChanged(nameof(ShowStripConnect));
        OnPropertyChanged(nameof(ShowStripJobBadge));
        OnPropertyChanged(nameof(ScreenNextStep));
    }

    private static string DescribeScreen(AppScreen screen) => screen switch
    {
        AppScreen.Home => "Domů",
        AppScreen.Designer => "Návrh",
        AppScreen.Device => "Zařízení",
        AppScreen.Chat => "Kamil",
        _ => "Lasero",
    };

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasOpenProject));
        OnPropertyChanged(nameof(ScreenNextStep));
    }
    partial void OnProjectPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasOpenProject));
        OnPropertyChanged(nameof(ScreenNextStep));
    }

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
        KamilAssistantViewModel kamil,
        ProjectRecoveryStore recoveryStore,
        AppSettingsStore settingsStore,
        RecentProjectsStore recentProjectsStore,
        JobHistoryStore jobHistoryStore,
        Func<DeviceWizardViewModel> deviceWizardFactory)
    {
        Connection = connection;
        DeviceSetup = new DeviceSetupViewModel(connection, () => DeviceWizardRequested?.Invoke());
        connection.AutoConnectNeedsWizard += scan =>
        {
            _autoStartNextWizard = true;
            _pendingScan = scan;
            DeviceWizardRequested?.Invoke();
        };
        MachineStatus = machineStatus;
        Jog = jog;
        Console = console;
        Scene = scene;
        GCode = gcode;
        JobStatus = new JobStatusViewModel(gcode);
        Account = account;
        Home = home;
        Materials = materials;
        Chat = chat;
        Kamil = kamil;
        Kamil.FullChatRequested += ShowChat;
        // Questions now carry the workspace with them: selected material, operation and the connected
        // machine, instead of the nulls the send path used to hardcode.
        Chat.ContextProvider = Kamil.BuildContext;
        GCode.MachineIdentity = () => (
            Connection.ActiveMachineName,
            Connection.SelectedPort,
            Lasero.Core.Grbl.VirtualGrblTransport.IsVirtualPort(Connection.SelectedPort));
        Kamil.ScreenLabel = DescribeScreen(CurrentScreen);
        // Homing is refused while a job is active; the Jog view model asks, the shell answers.
        Jog.IsJobActive = () => GCode.IsJobActive;
        GCode.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GCodeViewModel.JobState)) { NotifyScreenChrome(); Jog.RefreshJobGuard(); }
        };
        MachineStatus.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MachineStatusViewModel.DisplayState)) NotifyMachineBadge();
        };
        Connection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionViewModel.ConnectionError)
                or nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.SelectedPort))
                NotifyMachineBadge();
            if (args.PropertyName is nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.IsConnecting))
                OnPropertyChanged(nameof(ScreenNextStep));
        };
        _recoveryStore = recoveryStore;
        _recoveryStore.SwitchAccount(Account.UserId);
        Account.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AccountViewModel.UserId))
                _recoveryStore.SwitchAccount(Account.UserId);
        };
        _settingsStore = settingsStore;
        Guidance = new Lasero.App.Tour.GuidanceService(settingsStore);
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
        // KAMIL comes back the way the operator left it. Set before any view exists, so the host's
        // first render already shows the restored shape and no transition plays at startup.
        if (Settings.Workspace.AssistantExpanded) Kamil.State = KamilAssistantState.Expanded;
        Kamil.PropertyChanged += OnKamilPropertyChanged;
        Scene.Changed += OnSceneChanged;
        GCode.PlacementChanged += OnSceneChanged;
        Home.OpenRecentProjectRequested += OnOpenRecentProjectRequested;
        Home.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HomeViewModel.HasRecentProjects)) OnPropertyChanged(nameof(ScreenNextStep));
        };
        GCode.JobCompleted += OnJobCompleted;
        Connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsConnected) &&
            Connection.IsConnected &&
            CurrentScreen != AppScreen.Designer &&
            Settings.Safety.ShowMachineStatusAfterConnect)
        {
            CurrentScreen = AppScreen.Device;
        }

        if (e.PropertyName == nameof(ConnectionViewModel.DetectedDevice))
        {
            Jog.ConfigurePositioningLaser(
                Connection.DetectedDevice?.MaxSpindleSpeed,
                Connection.DetectedDevice?.LaserModeEnabled);
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

    /// <summary>
    /// The Home "Importovat" button. Import puts the file on the canvas, which Home cannot show, so
    /// a successful import continues into the design workspace. A cancelled dialog changes nothing
    /// and leaves the operator on Home.
    /// </summary>
    [RelayCommand]
    private void ImportFromHome()
    {
        var changed = false;
        var labelBefore = GCode.FileLabel;
        void OnChanged() => changed = true;
        Scene.Changed += OnChanged;
        try { GCode.LoadFileCommand.Execute(null); }
        finally { Scene.Changed -= OnChanged; }
        if (changed || GCode.FileLabel != labelBefore) ShowDesigner();
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

    private void OnSceneChanged()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(ScreenNextStep));
    }

    /// <summary>Clear the prior account's live document after sign-out has resolved unsaved work.
    /// The caller must not invoke this while a physical job is active.</summary>
    public void ClearWorkspaceForAccountSwitch()
    {
        Scene.ResetDocument();
        GCode.ClearDocument();
        ProjectName = "Nový projekt";
        ProjectPath = null;
        MaterialName = null;
        IsDirty = false;
        CurrentScreen = AppScreen.Home;
    }

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

    /// <summary>"Uložit jako…" — the same save, forced through the file dialog so the operator can
    /// branch a project without overwriting the one it came from. Separate from SaveProject because
    /// a plain save on a known path must never ask.</summary>
    [RelayCommand]
    private void SaveProjectAs() => TrySaveProject(forceDialog: true);

    public bool TrySaveProject(bool forceDialog = false)
    {
        var path = ProjectPath;
        if (forceDialog || string.IsNullOrWhiteSpace(path))
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
            Serilog.Log.Warning(ex, "Saving the project failed");
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Projekt se nepodařilo uložit",
                UserFacingErrors.ProjectSaveFailed(ex),
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
                "Vybraný soubor už na tomto místě není. Projekt lze otevřít přímo z Lasero a zvolit jeho nové umístění.",
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
            Serilog.Log.Warning(ex, "Opening the project failed");
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Projekt se nepodařilo otevřít",
                UserFacingErrors.ProjectOpenFailed(ex),
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
            Serilog.Log.Warning(ex, "Restoring the autosave failed");
            LaseroDialogWindow.Show(Application.Current.MainWindow, new LaseroDialogOptions(
                "Zálohu se nepodařilo obnovit",
                UserFacingErrors.RecoveryFailed(ex),
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
