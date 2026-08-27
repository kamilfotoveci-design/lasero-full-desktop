namespace Lasero.Core.History;

/// <summary>How often a given material name shows up in job history, for the Home dashboard's
/// materials list — ordered by most recently used.</summary>
public sealed record MaterialUsage(string Name, int UsageCount, DateTime LastUsedUtc);

/// <summary>Pure aggregation over a job-history log — kept free of file I/O so it's directly testable
/// (the persistence side, JobHistoryStore, lives in Lasero.App next to the other stores).</summary>
public readonly record struct JobHistorySummary(
    double TotalDurationSeconds,
    int CompletedCount,
    int DistinctMaterialCount,
    string? MostUsedMaterial)
{
    public static JobHistorySummary Compute(IReadOnlyCollection<JobHistoryEntry> entries)
    {
        if (entries.Count == 0) return new JobHistorySummary(0, 0, 0, null);

        var materialGroups = GroupByMaterial(entries);

        return new JobHistorySummary(
            TotalDurationSeconds: entries.Sum(e => e.DurationSeconds),
            CompletedCount: entries.Count,
            DistinctMaterialCount: materialGroups.Count,
            MostUsedMaterial: materialGroups.Count == 0 ? null : materialGroups
                .OrderByDescending(m => m.UsageCount)
                .ThenByDescending(m => m.LastUsedUtc)
                .First().Name);
    }

    /// <summary>Distinct materials used, most-used first (ties broken by most recently used) — feeds
    /// both the "most used material" stat tile and the Home dashboard's materials list.</summary>
    public static IReadOnlyList<MaterialUsage> GroupByMaterial(IEnumerable<JobHistoryEntry> entries) => entries
        .Where(e => !string.IsNullOrWhiteSpace(e.MaterialName))
        .GroupBy(e => e.MaterialName!.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(g => new MaterialUsage(g.Key, g.Count(), g.Max(e => e.CompletedUtc)))
        .OrderByDescending(m => m.UsageCount)
        .ThenByDescending(m => m.LastUsedUtc)
        .ToList();

    /// <summary>Entries completed on the same *local* calendar day as <paramref name="nowUtc"/> —
    /// compares local dates (not UTC dates) so a job finished at 23:40 local time isn't misfiled into
    /// "yesterday" just because its stored UTC timestamp already rolled over to the next day.</summary>
    public static IEnumerable<JobHistoryEntry> Today(IEnumerable<JobHistoryEntry> entries, DateTime nowUtc) =>
        entries.Where(e => e.CompletedUtc.ToLocalTime().Date == nowUtc.ToLocalTime().Date);
}
