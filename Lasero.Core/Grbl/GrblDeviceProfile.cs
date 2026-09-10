using System.Globalization;

namespace Lasero.Core.Grbl;

public sealed record GrblDeviceProfile
{
    public string? FirmwareBanner { get; init; }
    public double? MaxTravelXmm { get; init; }
    public double? MaxTravelYmm { get; init; }
    public double? MaxTravelZmm { get; init; }
    public double? MaxSpindleSpeed { get; init; }
    public bool? LaserModeEnabled { get; init; }
    public IReadOnlyDictionary<int, double> NumericSettings { get; init; } = new Dictionary<int, double>();
}

public static class GrblDeviceProfileParser
{
    public static GrblDeviceProfile Parse(IEnumerable<string> settingsLines, string? firmwareBanner = null)
    {
        ArgumentNullException.ThrowIfNull(settingsLines);
        var settings = new Dictionary<int, double>();
        foreach (var line in settingsLines)
        {
            if (!TryParseSetting(line, out var number, out var value))
                continue;
            settings[number] = value;
        }

        return new GrblDeviceProfile
        {
            FirmwareBanner = firmwareBanner,
            MaxTravelXmm = GetPositive(settings, 130),
            MaxTravelYmm = GetPositive(settings, 131),
            MaxTravelZmm = GetPositive(settings, 132),
            MaxSpindleSpeed = GetPositive(settings, 30),
            LaserModeEnabled = settings.TryGetValue(32, out var laserMode) ? laserMode >= 0.5 : null,
            NumericSettings = settings,
        };
    }

    public static bool TryParseSetting(string line, out int number, out double value)
    {
        number = default;
        value = default;
        if (string.IsNullOrWhiteSpace(line) || line[0] != '$')
            return false;
        var equals = line.IndexOf('=');
        if (equals < 2)
            return false;
        var valueEnd = line.IndexOfAny([' ', '\t', '('], equals + 1);
        var valueText = valueEnd < 0 ? line[(equals + 1)..] : line[(equals + 1)..valueEnd];
        return int.TryParse(line.AsSpan(1, equals - 1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static double? GetPositive(IReadOnlyDictionary<int, double> settings, int number) =>
        settings.TryGetValue(number, out var value) && value > 0 ? value : null;
}
