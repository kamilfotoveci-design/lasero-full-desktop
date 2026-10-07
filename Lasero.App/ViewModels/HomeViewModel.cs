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
    private readonly AccountViewModel _account;

    public ConnectionViewModel Connection { get; }
    public MachineStatusViewModel MachineStatus { get; }
    public GCodeViewModel GCode { get; }

    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();
    public ObservableCollection<RecentProjectItemViewModel> RecentProjectRows { get; } = new();
    public RecentProjectItemViewModel? FeaturedProject => RecentProjectRows.Count > 0 ? RecentProjectRows[0] : null;
    public ObservableCollection<RecentProjectItemViewModel> OtherRecentProjectRows { get; } = new();
    public ObservableCollection<JobHistoryEntry> TodayJobs { get; } = new();
    public ObservableCollection<MaterialUsage> RecentMaterials { get; } = new();
    public ObservableCollection<MaterialUsageItemViewModel> RecentMaterialRows { get; } = new();

    /// <summary>What the device card shows. Every one of these reads through to live connection state,
    /// so a disconnected app names no machine, no port and no firmware instead of inventing them.</summary>
    public string DeviceName => Connection.IsConnected ? Connection.ActiveMachineName : "Žádné zařízení";

    /// <summary>The badge is about the link — is there a machine on the other end of the cable. The
    /// "Stav" row below it is about what that machine is doing, which is a different question.</summary>
    public string ConnectionBadgeLabel => Connection.IsConnected ? "Připojeno" : "Nepřipojeno";

    public string DeviceConnectionLabel => Connection.IsConnected
        ? $"USB  ·  {Connection.SelectedPort}  ·  {Connection.BaudRate} Bd"
        : "USB připojení není aktivní";

    /// <summary>The same fact as DeviceConnectionLabel, short enough for a half-width stat tile. The
    /// long form stays for places that have a full row to spend on it.</summary>
    public string ConnectionSummaryLabel => Connection.IsConnected ? $"USB ({Connection.SelectedPort})" : "Nepřipojeno";

    /// <summary>GRBL's banner is "Grbl 1.1h ['$' for help]" — the version is the useful half.</summary>
    public string FirmwareLabel
    {
        get
        {
            var banner = Connection.FirmwareBanner;
            if (string.IsNullOrWhiteSpace(banner)) return "Neznámo";
            var bracket = banner.IndexOf('[');
            return (bracket > 0 ? banner[..bracket] : banner).Trim();
        }
    }

    public bool HasRecentProjects => RecentProjects.Count > 0;
    public bool HasTodayJobs => TodayJobs.Count > 0;
    public bool HasMaterials => RecentMaterials.Count > 0;
    public bool IsJobRunning => GCode.JobState is JobRunState.Running or JobRunState.Paused;

    [ObservableProperty] private string _totalEngravingTimeLabel = "0 h 0 min";
    private int _tipOffset;

    /// <summary>The rotating "Tip dne": one per calendar day, "Další tip" steps through the list.</summary>
    [ObservableProperty] private string _tipOfDay = Lasero.App.Tour.TipOfDay.For(DateTime.Today);

    /// <summary>"3 z 12": which tip of the rotation is on screen, so the card can show that more follow.</summary>
    public int TipPosition => Lasero.App.Tour.TipOfDay.PositionOf(TipOfDay);
    public int TipCount => Lasero.App.Tour.TipOfDay.All.Count;
    partial void OnTipOfDayChanged(string value) => OnPropertyChanged(nameof(TipPosition));

    [RelayCommand]
    private void NextTip()
    {
        _tipOffset++;
        TipOfDay = Lasero.App.Tour.TipOfDay.For(DateTime.Today, _tipOffset);
    }

    [ObservableProperty] private int _completedJobsCount;
    [ObservableProperty] private int _materialsUsedCount;
    [ObservableProperty] private string _mostUsedMaterialLabel = "Zatím žádný";

    /// <summary>The most recently finished job, for Home's "poslední úloha" panel. Informational
    /// only: a history entry records a name, a material and a duration, not the file it came from,
    /// so there is nothing here that could honestly re-run the job.</summary>
    [ObservableProperty] private string? _lastJobName;
    [ObservableProperty] private string _lastJobResultLabel = string.Empty;
    [ObservableProperty] private string? _lastJobMaterialLabel;
    [ObservableProperty] private string _lastJobCompletedLabel = string.Empty;

    public bool HasLastJob => !string.IsNullOrWhiteSpace(LastJobName);
    public bool HasRecentMaterials => RecentMaterialRows.Count > 0;

    partial void OnLastJobNameChanged(string? value) => OnPropertyChanged(nameof(HasLastJob));

    /// <summary>Raised when the user clicks a recent-project card; MainViewModel subscribes and owns
    /// the actual confirm-replacement + load flow (same as the existing OpenProjectCommand file-dialog path).</summary>
    public event Action<string>? OpenRecentProjectRequested;

    public HomeViewModel(
        RecentProjectsStore recentProjectsStore,
        JobHistoryStore jobHistoryStore,
        ConnectionViewModel connection,
        MachineStatusViewModel machineStatus,
        GCodeViewModel gcode,
        AccountViewModel account)
    {
        _recentProjectsStore = recentProjectsStore;
        _jobHistoryStore = jobHistoryStore;
        _account = account;
        Connection = connection;
        MachineStatus = machineStatus;
        GCode = gcode;

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
                OnPropertyChanged(nameof(ConnectionSummaryLabel));
                OnPropertyChanged(nameof(FirmwareLabel));
            }
        };

        _recentProjectsStore.Changed += RefreshRecentProjects;
        _jobHistoryStore.Changed += RefreshJobHistory;
        GCode.PropertyChanged += OnGCodePropertyChanged;
        _account.PropertyChanged += OnAccountPropertyChanged;

        _jobHistoryStore.SwitchAccount(_account.UserId);
        RefreshRecentProjects();
        RefreshJobHistory();
    }

    /// <summary>Keeps the recent-projects cache scoped to whichever account is actually signed in —
    /// session-identity-driven (fires on every sign-in, sign-out, and account switch via
    /// AccountViewModel.UserId), not dependent on any window being reopened or shown. RefreshRecentProjects
    /// then runs automatically too, via RecentProjectsStore.Changed, which SwitchAccount always raises.</summary>
    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.UserId))
        {
            _recentProjectsStore.SwitchAccount(_account.UserId);
            _jobHistoryStore.SwitchAccount(_account.UserId);
        }
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
        OtherRecentProjectRows.Clear();
        var now = DateTime.Now;
        foreach (var entry in _recentProjectsStore.Recent)
        {
            RecentProjects.Add(entry);
            RecentProjectRows.Add(new RecentProjectItemViewModel(entry, now));
        }
        for (var index = 1; index < RecentProjectRows.Count; index++)
            OtherRecentProjectRows.Add(RecentProjectRows[index]);
        OnPropertyChanged(nameof(FeaturedProject));
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private void RefreshJobHistory()
    {
        var entries = _jobHistoryStore.Entries;
        var summary = JobHistorySummary.Compute(entries);
        TotalEngravingTimeLabel = FormatDuration(summary.TotalDurationSeconds);
        CompletedJobsCount = summary.CompletedCount;
        MaterialsUsedCount = summary.DistinctMaterialCount;
        MostUsedMaterialLabel = summary.MostUsedMaterial ?? "Zatím žádný";

        TodayJobs.Clear();
        foreach (var entry in JobHistorySummary.Today(entries, DateTime.UtcNow).OrderByDescending(e => e.CompletedUtc))
            TodayJobs.Add(entry);
        OnPropertyChanged(nameof(HasTodayJobs));

        RecentMaterials.Clear();
        RecentMaterialRows.Clear();
        var nowLocal = DateTime.Now;
        foreach (var usage in JobHistorySummary.GroupByMaterial(entries))
        {
            RecentMaterials.Add(usage);
            RecentMaterialRows.Add(new MaterialUsageItemViewModel(usage, nowLocal));
        }
        OnPropertyChanged(nameof(HasMaterials));
        OnPropertyChanged(nameof(HasRecentMaterials));

        RefreshLastJob(entries, nowLocal);
    }

    private void RefreshLastJob(IReadOnlyList<JobHistoryEntry> entries, DateTime nowLocal)
    {
        var last = entries.Count == 0 ? null : entries.MaxBy(e => e.CompletedUtc);
        LastJobName = last?.Name;
        if (last is null)
        {
            LastJobResultLabel = string.Empty;
            LastJobMaterialLabel = null;
            LastJobCompletedLabel = string.Empty;
            return;
        }

        // Only completed jobs are ever appended (see GCodeViewModel.JobCompleted), so "Dokončeno"
        // is a statement of fact here rather than an assumption about how the run ended.
        LastJobResultLabel = $"Dokončeno · {FormatJobDuration(last.DurationSeconds)}";
        LastJobMaterialLabel = string.IsNullOrWhiteSpace(last.MaterialName) ? null : last.MaterialName;
        LastJobCompletedLabel = DescribeCompletion(last.CompletedUtc.ToLocalTime(), nowLocal);
    }

    /// <summary>Job durations are minutes and seconds far more often than hours, so the hour part
    /// only appears once there is one.</summary>
    private static string FormatJobDuration(double totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min {span.Seconds} s";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} min {span.Seconds} s";
        return $"{span.Seconds} s";
    }

    private static string DescribeCompletion(DateTime moment, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var day = moment.Date;
        if (day == today) return $"dnes v {moment:HH:mm}";
        if (day == today.AddDays(-1)) return $"včera v {moment:HH:mm}";
        return $"{moment:d. M. yyyy} v {moment:HH:mm}";
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
