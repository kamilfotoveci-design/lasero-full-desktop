using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Lasero.Avalonia.Services;
using Lasero.Core.History;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// One row in Home's recent-projects list. Matches Lasero.App's RecentProjectItemViewModel wording
/// (minus ThumbnailPath/SizeText — thumbnail rendering is not ported yet, see MIGRATION.md).
/// </summary>
public sealed class RecentProjectRow
{
    public RecentProjectEntry Entry { get; }
    public string Name => Entry.Name;
    public string ModifiedText { get; }

    public RecentProjectRow(RecentProjectEntry entry, DateTime nowLocal)
    {
        Entry = entry;
        ModifiedText = DescribeMoment(entry.LastOpenedUtc.ToLocalTime(), nowLocal);
    }

    private static string DescribeMoment(DateTime moment, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var day = moment.Date;
        if (day == today) return $"Upraveno dnes, {moment:HH:mm}";
        if (day == today.AddDays(-1)) return $"Upraveno včera, {moment:HH:mm}";
        if (day > today.AddDays(-7))
            return $"Upraveno {moment.ToString("dddd", CultureInfo.GetCultureInfo("cs-CZ"))}, {moment:HH:mm}";
        return $"Upraveno {moment:d. M.}, {moment:HH:mm}";
    }
}

/// <summary>One row in Home's recent-materials list. Matches Lasero.App's MaterialUsageItemViewModel wording.</summary>
public sealed class MaterialUsageRow
{
    public MaterialUsage Usage { get; }
    public string Name => Usage.Name;
    public string UsageLabel { get; }

    public MaterialUsageRow(MaterialUsage usage, DateTime nowLocal)
    {
        Usage = usage;
        UsageLabel = $"{DescribeCount(usage.UsageCount)} · {DescribeMoment(usage.LastUsedUtc.ToLocalTime(), nowLocal)}";
    }

    private static string DescribeCount(int count) => count switch
    {
        1 => "1 úloha",
        2 or 3 or 4 => $"{count} úlohy",
        _ => $"{count} úloh",
    };

    private static string DescribeMoment(DateTime moment, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var day = moment.Date;
        if (day == today) return "dnes";
        if (day == today.AddDays(-1)) return "včera";
        if (day > today.AddDays(-7)) return moment.ToString("dddd", CultureInfo.GetCultureInfo("cs-CZ"));
        return moment.ToString("d. M. yyyy", CultureInfo.GetCultureInfo("cs-CZ"));
    }
}

/// <summary>
/// Avalonia port of Lasero.App/ViewModels/HomeViewModel.cs. Recent projects and job/material history
/// are real — they read the same on-disk stores the WPF app writes. The device rail is deliberately
/// honest instead of ported: WPF's HomeViewModel wires a live ConnectionViewModel/MachineStatusViewModel
/// backed by a GRBL serial connection, and that connection layer has not been migrated yet (see
/// MigrationInventory — "Připojení zařízení" is still "Další"). So this class reports the only state
/// that is actually true right now: no device is connected. Once device connection lands in
/// Lasero.Avalonia, DeviceName/ConnectionBadgeLabel/etc. should read through to it exactly the way the
/// WPF properties of the same name do (kept identical on purpose so that swap is a drop-in).
/// </summary>
public sealed class HomeViewModel : ReactiveObject
{
    private readonly RecentProjectsStore _recentProjectsStore;
    private readonly JobHistoryStore _jobHistoryStore;

    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();
    public ObservableCollection<RecentProjectRow> RecentProjectRows { get; } = new();
    public ObservableCollection<MaterialUsage> RecentMaterials { get; } = new();
    public ObservableCollection<MaterialUsageRow> RecentMaterialRows { get; } = new();
    public ObservableCollection<JobHistoryEntry> TodayJobs { get; } = new();

    private bool hasRecentProjects;
    public bool HasRecentProjects
    {
        get => hasRecentProjects;
        private set => this.RaiseAndSetIfChanged(ref hasRecentProjects, value);
    }

    private bool hasRecentMaterials;
    public bool HasRecentMaterials
    {
        get => hasRecentMaterials;
        private set => this.RaiseAndSetIfChanged(ref hasRecentMaterials, value);
    }

    private bool hasTodayJobs;
    public bool HasTodayJobs
    {
        get => hasTodayJobs;
        private set => this.RaiseAndSetIfChanged(ref hasTodayJobs, value);
    }

    private string totalEngravingTimeLabel = "0 h 0 min";
    public string TotalEngravingTimeLabel { get => totalEngravingTimeLabel; private set => this.RaiseAndSetIfChanged(ref totalEngravingTimeLabel, value); }

    private int completedJobsCount;
    public int CompletedJobsCount { get => completedJobsCount; private set => this.RaiseAndSetIfChanged(ref completedJobsCount, value); }

    private int materialsUsedCount;
    public int MaterialsUsedCount { get => materialsUsedCount; private set => this.RaiseAndSetIfChanged(ref materialsUsedCount, value); }

    private string mostUsedMaterialLabel = "Zatím žádný";
    public string MostUsedMaterialLabel { get => mostUsedMaterialLabel; private set => this.RaiseAndSetIfChanged(ref mostUsedMaterialLabel, value); }

    private string? lastJobName;
    public string? LastJobName
    {
        get => lastJobName;
        private set
        {
            this.RaiseAndSetIfChanged(ref lastJobName, value);
            this.RaisePropertyChanged(nameof(HasLastJob));
        }
    }

    private string lastJobResultLabel = string.Empty;
    public string LastJobResultLabel { get => lastJobResultLabel; private set => this.RaiseAndSetIfChanged(ref lastJobResultLabel, value); }

    private string? lastJobMaterialLabel;
    public string? LastJobMaterialLabel { get => lastJobMaterialLabel; private set => this.RaiseAndSetIfChanged(ref lastJobMaterialLabel, value); }

    private string lastJobCompletedLabel = string.Empty;
    public string LastJobCompletedLabel { get => lastJobCompletedLabel; private set => this.RaiseAndSetIfChanged(ref lastJobCompletedLabel, value); }

    public bool HasLastJob => !string.IsNullOrWhiteSpace(LastJobName);

    // --- Device rail: now backed by the real, ported ConnectionViewModel/JogViewModel (Machine
    // Control pass) — matches Lasero.App's own HomeViewModel exactly: Connection/Jog are exposed
    // directly for the view to bind (Home.Connection.IsConnected, Home.Jog.Home), and these five
    // labels are the same convenience pass-throughs WPF's HomeViewModel computes, kept in sync via
    // Connection.PropertyChanged the same way. RunFraming/its tooltip are NOT reintroduced here —
    // WPF binds GCode.RunFramingCommand directly, and GCode/job generation still isn't ported.
    public ConnectionViewModel Connection { get; }
    public JogViewModel Jog { get; }

    public string DeviceName => Connection.IsConnected ? Connection.ActiveMachineName : "Žádné zařízení";
    public string ConnectionBadgeLabel => Connection.IsConnected ? "Připojeno" : "Nepřipojeno";
    public string DeviceConnectionLabel => Connection.IsConnected
        ? $"USB  ·  {Connection.SelectedPort}  ·  {Connection.BaudRate} Bd"
        : "Připojte gravírku kabelem USB";
    public string ConnectionSummaryLabel => Connection.IsConnected ? $"USB ({Connection.SelectedPort})" : "Nepřipojeno";
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

    public ReactiveCommand<RecentProjectEntry, Unit> OpenRecentProject { get; }
    public ReactiveCommand<RecentProjectEntry, Unit> RemoveRecentProject { get; }

    /// <summary>Raised when the user opens a recent-project card; the shell owns the actual
    /// dirty-state confirmation + load flow (same idiom as the WPF original).</summary>
    public event Action<string>? OpenRecentProjectRequested;

    public HomeViewModel(RecentProjectsStore recentProjectsStore, JobHistoryStore jobHistoryStore,
        ConnectionViewModel connection, JogViewModel jog)
    {
        _recentProjectsStore = recentProjectsStore;
        _jobHistoryStore = jobHistoryStore;
        Connection = connection;
        Jog = jog;

        Connection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionViewModel.IsConnected)
                or nameof(ConnectionViewModel.ActiveMachineName)
                or nameof(ConnectionViewModel.SelectedPort)
                or nameof(ConnectionViewModel.BaudRate)
                or nameof(ConnectionViewModel.FirmwareBanner))
            {
                this.RaisePropertyChanged(nameof(DeviceName));
                this.RaisePropertyChanged(nameof(ConnectionBadgeLabel));
                this.RaisePropertyChanged(nameof(DeviceConnectionLabel));
                this.RaisePropertyChanged(nameof(ConnectionSummaryLabel));
                this.RaisePropertyChanged(nameof(FirmwareLabel));
            }
        };

        OpenRecentProject = ReactiveCommand.Create<RecentProjectEntry>(OpenProject);
        RemoveRecentProject = ReactiveCommand.Create<RecentProjectEntry>(RemoveProject);

        _recentProjectsStore.Changed += RefreshRecentProjects;
        _jobHistoryStore.Changed += RefreshJobHistory;

        RefreshRecentProjects();
        RefreshJobHistory();
    }

    public void OpenProject(RecentProjectEntry entry) => OpenRecentProjectRequested?.Invoke(entry.Path);
    public void RemoveProject(RecentProjectEntry entry) => _recentProjectsStore.Remove(entry.Path);

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        RecentProjectRows.Clear();
        var now = DateTime.Now;
        foreach (var entry in _recentProjectsStore.Recent)
        {
            RecentProjects.Add(entry);
            RecentProjectRows.Add(new RecentProjectRow(entry, now));
        }
        HasRecentProjects = RecentProjects.Count > 0;
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
        HasTodayJobs = TodayJobs.Count > 0;

        RecentMaterials.Clear();
        RecentMaterialRows.Clear();
        var nowLocal = DateTime.Now;
        foreach (var usage in JobHistorySummary.GroupByMaterial(entries))
        {
            RecentMaterials.Add(usage);
            RecentMaterialRows.Add(new MaterialUsageRow(usage, nowLocal));
        }
        HasRecentMaterials = RecentMaterials.Count > 0;

        RefreshLastJob(entries, DateTime.Now);
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

        LastJobResultLabel = $"Dokončeno · {FormatJobDuration(last.DurationSeconds)}";
        LastJobMaterialLabel = string.IsNullOrWhiteSpace(last.MaterialName) ? null : last.MaterialName;
        LastJobCompletedLabel = DescribeCompletion(last.CompletedUtc.ToLocalTime(), nowLocal);
    }

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
}
