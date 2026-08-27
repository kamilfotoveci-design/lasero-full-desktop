using Lasero.Core.History;
using Xunit;

namespace Lasero.Tests;

public class JobHistorySummaryTests
{
    [Fact]
    public void ComputeOnEmptyListReturnsZeroedSummaryNotNull()
    {
        var summary = JobHistorySummary.Compute([]);

        Assert.Equal(0, summary.TotalDurationSeconds);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.DistinctMaterialCount);
        Assert.Null(summary.MostUsedMaterial);
    }

    [Fact]
    public void ComputeSumsDurationAndCountsEntriesIgnoringMissingMaterial()
    {
        var entries = new[]
        {
            new JobHistoryEntry("Hory a jazero", "Preglejka 3 mm", 120, DateTime.UtcNow),
            new JobHistoryEntry("Logo", null, 45, DateTime.UtcNow),
        };

        var summary = JobHistorySummary.Compute(entries);

        Assert.Equal(165, summary.TotalDurationSeconds);
        Assert.Equal(2, summary.CompletedCount);
        Assert.Equal(1, summary.DistinctMaterialCount);
        Assert.Equal("Preglejka 3 mm", summary.MostUsedMaterial);
    }

    [Fact]
    public void MostUsedMaterialTieIsBrokenByMostRecentlyUsed()
    {
        var older = DateTime.UtcNow.AddDays(-2);
        var newer = DateTime.UtcNow;
        var entries = new[]
        {
            new JobHistoryEntry("A", "Breza", 60, older),
            new JobHistoryEntry("B", "Javor", 60, newer),
        };

        var summary = JobHistorySummary.Compute(entries);

        Assert.Equal(2, summary.DistinctMaterialCount);
        Assert.Equal("Javor", summary.MostUsedMaterial);
    }

    [Fact]
    public void GroupByMaterialIsCaseInsensitiveAndTrimsWhitespace()
    {
        var entries = new[]
        {
            new JobHistoryEntry("A", "Preglejka", 60, DateTime.UtcNow),
            new JobHistoryEntry("B", " preglejka ", 60, DateTime.UtcNow),
            new JobHistoryEntry("C", "Akryl", 60, DateTime.UtcNow),
        };

        var groups = JobHistorySummary.GroupByMaterial(entries);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Preglejka", groups[0].Name);
        Assert.Equal(2, groups[0].UsageCount);
    }

    [Fact]
    public void TodayFiltersByLocalCalendarDayAcrossUtcMidnightBoundary()
    {
        // Reference "now" is 00:30 UTC. An entry completed 40 minutes earlier (23:50 UTC the previous
        // calendar day) should still count as "today" for a user whose local offset is ahead of UTC,
        // and should NOT count as "today" for a user behind UTC — this exercises the local-date
        // comparison rather than a naive UTC-date comparison, which would misfile it either way.
        var nowUtc = new DateTime(2026, 8, 5, 0, 30, 0, DateTimeKind.Utc);
        var justBeforeUtc = new DateTime(2026, 8, 4, 23, 50, 0, DateTimeKind.Utc);
        var yesterdayUtc = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        var entries = new[]
        {
            new JobHistoryEntry("Recent", null, 60, justBeforeUtc),
            new JobHistoryEntry("OldJob", null, 60, yesterdayUtc),
        };

        var today = JobHistorySummary.Today(entries, nowUtc).ToList();

        var expectedTodayLocal = nowUtc.ToLocalTime().Date == justBeforeUtc.ToLocalTime().Date;
        Assert.Equal(expectedTodayLocal, today.Any(e => e.Name == "Recent"));
        Assert.DoesNotContain(today, e => e.Name == "OldJob");
    }
}
