using System.Globalization;
using Lasero.Core.History;

namespace Lasero.App.ViewModels;

/// <summary>
/// One row in Home's "naposledy použité materiály" list. Job history is the only place the app knows
/// a material was actually used, and all it records is the name, how many jobs used it and when it
/// was last used — so that is what the row says. Speed/power belong to a layer or a preset, not to
/// this record, and inventing them here would put numbers on screen that no job ever ran with.
/// </summary>
public sealed class MaterialUsageItemViewModel
{
    public MaterialUsage Usage { get; }
    public string Name => Usage.Name;
    public string UsageLabel { get; }

    public MaterialUsageItemViewModel(MaterialUsage usage, DateTime nowLocal)
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
