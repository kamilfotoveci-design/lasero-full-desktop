using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.History;
using Lasero.Core.Jobs;

namespace Lasero.App.ViewModels;

/// <summary>
/// The Home dashboard's own state: recent projects, today's job history, and the stat tiles derived
/// from it. Deliberately doesn't own project-create/open logic itself — MainViewModel already has
/// NewProject/LoadProject (with their dirty-state confirmation and screen-switching); this class only
/// raises OpenRecentProjectRequested for MainViewModel to act on, same idiom as SceneViewModel.Changed.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly RecentProjectsStore _recentProjectsStore;
    private readonly JobHistoryStore _jobHistoryStore;

    public ConnectionViewModel Connection { get; }
    public MachineStatusViewModel MachineStatus { get; }
    public GCodeViewModel GCode { get; }

    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();
    public ObservableCollection<RecentProjectItemViewModel> RecentProjectRows { get; } = new();
    public HomeSafetyViewModel Safety { get; }
    public ObservableCollection<JobHistoryEntry> TodayJobs { get; } = new();
    public ObservableCollection<MaterialUsage> RecentMaterials { get; } = new();

    /// <summary>What the device card shows. Every one of these reads through to live connection state,
    /// so a disconnected app names no machine, no port and no firmware instead of inventing them.</summary>
    public string DeviceName => Connection.IsConnected ? Connection.ActiveMachineName : "Žádné zařízení";

    /// <summary>The badge is about the link — is there a machine on the other end of the cable. The
    /// "Stav" row below it is about what that machine is doing, which is a different question.</summary>
    public string ConnectionBadgeLabel => Connection.IsConnected ? "Připojeno" : "Nepřipojeno";

    public string DeviceConnectionLabel => Connection.IsConnected
        ? $"USB  ·  {Connection.SelectedPort}  ·  {Connection.BaudRate} Bd"
        : "Připojte gravírku kabelem USB";

    /// <summary>GRBL's banner is "Grbl 1.1h ['$' for help]" — the version is the useful half.</summary>
    public string FirmwareLabel
    {
        get
        {
            var banner = Connection.FirmwareBanner;
            if (string.IsNullOrWhiteSpace(banner)) return "—";
            var bracket = banner.IndexOf('[');
            return (bracket > 0 ? banner[..bracket] : banner).Trim();
        }
    }

    public bool HasRecentProjects => RecentProjects.Count > 0;
    public bool HasTodayJobs => TodayJobs.Count > 0;
    public bool HasMaterials => RecentMaterials.Count > 0;
    public bool IsJobRunning => GCode.JobState is JobRunState.Running or JobRunState.Paused;

    [ObservableProperty] private string _totalEngravingTimeLabel = "0 h 0 min";
    [ObservableProperty] private int _completedJobsCount;
    [ObservableProperty] private int _materialsUsedCount;
    [ObservableProperty] private string _mostUsedMaterialLabel = "—";

    /// <summary>Raised when the user clicks a recent-project card; MainViewModel subscribes and owns
    /// the actual confirm-replacement + load flow (same as the existing OpenProjectCommand file-dialog path).</summary>
    public event Action<string>? OpenRecentProjectRequested;

    public HomeViewModel(
        RecentProjectsStore recentProjectsStore,
        JobHistoryStore jobHistoryStore,
        ConnectionViewModel connection,
        MachineStatusViewModel machineStatus,
        GCodeViewModel gcode)
    {
        _recentProjectsStore = recentProjectsStore;
        _jobHistoryStore = jobHistoryStore;
        Connection = connection;
        MachineStatus = machineStatus;
        GCode = gcode;
        Safety = new HomeSafetyViewModel(connection, machineStatus);

        Connection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionViewModel.IsConnected)
                or nameof(ConnectionViewModel.ActiveMachineName)
                or nameof(ConnectionViewModel.SelectedPort)
                or nameof(ConnectionViewModel.BaudRate)
                or nameof(ConnectionViewModel.FirmwareBanner))
            {
                OnPropertyChanged(nameof(DeviceName));
                OnPropertyChanged(nameof(ConnectionBadgeLabel));
                OnPropertyChanged(nameof(DeviceConnectionLabel));
                OnPropertyChanged(nameof(FirmwareLabel));
            }
        };

        _recentProjectsStore.Changed += RefreshRecentProjects;
        _jobHistoryStore.Changed += RefreshJobHistory;
        GCode.PropertyChanged += OnGCodePropertyChanged;

        RefreshRecentProjects();
        RefreshJobHistory();
    }

    private void OnGCodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GCodeViewModel.JobState))
            OnPropertyChanged(nameof(IsJobRunning));
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        RecentProjectRows.Clear();
        var now = DateTime.Now;
        foreach (var entry in _recentProjectsStore.Recent)
        {
            RecentProjects.Add(entry);
            RecentProjectRows.Add(new RecentProjectItemViewModel(entry, now));
        }
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private void RefreshJobHistory()
    {
        var entries = _jobHistoryStore.Entries;
        var summary = JobHistorySummary.Compute(entries);
        TotalEngravingTimeLabel = FormatDuration(summary.TotalDurationSeconds);
        CompletedJobsCount = summary.CompletedCount;
        MaterialsUsedCount = summary.DistinctMaterialCount;
        MostUsedMaterialLabel = summary.MostUsedMaterial ?? "—";

        TodayJobs.Clear();
        foreach (var entry in JobHistorySummary.Today(entries, DateTime.UtcNow).OrderByDescending(e => e.CompletedUtc))
            TodayJobs.Add(entry);
        OnPropertyChanged(nameof(HasTodayJobs));

        RecentMaterials.Clear();
        foreach (var usage in JobHistorySummary.GroupByMaterial(entries)) RecentMaterials.Add(usage);
        OnPropertyChanged(nameof(HasMaterials));
    }

    private static string FormatDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    [RelayCommand]
    private void OpenRecentProject(RecentProjectEntry entry) => OpenRecentProjectRequested?.Invoke(entry.Path);

    [RelayCommand]
    private void RemoveRecentProject(RecentProjectEntry entry) => _recentProjectsStore.Remove(entry.Path);
}
