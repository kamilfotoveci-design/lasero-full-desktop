using System.Globalization;
using System.IO;
using Lasero.Core.History;

namespace Lasero.App.ViewModels;

/// <summary>
/// One row in Home's recent-projects list. Size and the "upraveno" line come off the file on disk
/// rather than from the history entry, so a project edited by something else still reads correctly —
/// and a project that has since been deleted or moved says so instead of showing a stale size.
/// </summary>
public sealed class RecentProjectItemViewModel
{
    public RecentProjectEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string? ThumbnailPath => Entry.ThumbnailPath;
    public bool HasThumbnail => !string.IsNullOrWhiteSpace(Entry.ThumbnailPath) && File.Exists(Entry.ThumbnailPath);
    public bool Exists { get; }
    public string ModifiedText { get; }
    public string SizeText { get; }

    public RecentProjectItemViewModel(RecentProjectEntry entry, DateTime nowLocal)
    {
        Entry = entry;
        var file = new FileInfo(entry.Path);
        Exists = file.Exists;
        ModifiedText = Exists
            ? DescribeMoment(file.LastWriteTime, nowLocal)
            : "Soubor nebyl nalezen";
        SizeText = Exists ? DescribeSize(file.Length) : string.Empty;
    }

    /// <summary>"Upraveno dnes, 14:32" reads faster than a date for the files someone actually opens
    /// again; anything older than a week is better as a plain date.</summary>
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

    private static string DescribeSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024) return string.Create(CultureInfo.GetCultureInfo("cs-CZ"), $"{kilobytes:0.#} kB");
        return string.Create(CultureInfo.GetCultureInfo("cs-CZ"), $"{kilobytes / 1024d:0.#} MB");
    }
}
