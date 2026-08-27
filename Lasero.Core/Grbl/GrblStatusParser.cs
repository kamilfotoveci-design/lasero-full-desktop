using System.Globalization;

namespace Lasero.Core.Grbl;

/// <summary>
/// Parses GRBL 1.1 real-time status reports of the form
/// "&lt;Idle|MPos:0.000,0.000,0.000|FS:0,0|WCO:0.000,0.000,0.000&gt;".
/// Pure/stateless: if only MPos or only WPos is present on this particular
/// line, the other is left at Position.Zero here — GrblConnection combines
/// this with the last-known WCO to fill in the gap, since WCO is only sent
/// periodically, not on every report.
/// </summary>
public static class GrblStatusParser
{
    public static bool TryParse(string line, out MachineStatus status)
    {
        status = null!;
        line = line.Trim();
        if (line.Length < 2 || line[0] != '<' || line[^1] != '>')
            return false;

        var body = line[1..^1];
        var fields = body.Split('|');
        if (fields.Length == 0)
            return false;

        var mode = ParseMode(fields[0]);
        Position mPos = Position.Zero;
        Position wPos = Position.Zero;
        Position? wco = null;
        double feed = 0;
        double spindle = 0;
        int? feedOv = null, rapidOv = null, spindleOv = null;
        string? pins = null;
        (int, int)? buffer = null;
        bool hasMPos = false, hasWPos = false;

        for (int i = 1; i < fields.Length; i++)
        {
            var field = fields[i];
            var colon = field.IndexOf(':');
            if (colon < 0) continue;

            var key = field[..colon];
            var value = field[(colon + 1)..];

            switch (key)
            {
                case "MPos":
                    mPos = ParsePosition(value);
                    hasMPos = true;
                    break;
                case "WPos":
                    wPos = ParsePosition(value);
                    hasWPos = true;
                    break;
                case "WCO":
                    wco = ParsePosition(value);
                    break;
                case "FS":
                    ParseFeedSpindle(value, out feed, out spindle);
                    break;
                case "F":
                    feed = ParseDouble(value);
                    break;
                case "Ov":
                    ParseOverrides(value, out feedOv, out rapidOv, out spindleOv);
                    break;
                case "Pn":
                    pins = value;
                    break;
                case "Bf":
                    buffer = ParseBuffer(value);
                    break;
            }
        }

        // Fill in whichever of MPos/WPos is missing using WCO, if we have it:
        // WPos = MPos - WCO  <=>  MPos = WPos + WCO
        if (hasMPos && !hasWPos && wco is { } w1)
            wPos = mPos.Minus(w1);
        else if (hasWPos && !hasMPos && wco is { } w2)
            mPos = wPos.Offset(w2);

        status = new MachineStatus
        {
            Mode = mode,
            MachinePosition = mPos,
            WorkPosition = wPos,
            WorkCoordinateOffset = wco ?? Position.Zero,
            FeedRate = feed,
            SpindleSpeed = spindle,
            FeedOverridePercent = feedOv,
            RapidOverridePercent = rapidOv,
            SpindleOverridePercent = spindleOv,
            TriggeredPins = pins,
            Buffer = buffer,
            RawLine = line,
        };
        return true;
    }

    private static GrblMachineMode ParseMode(string token)
    {
        // Mode may carry a sub-state after ':', e.g. "Hold:0", "Door:1" — we only
        // care about the top-level mode name here.
        var name = token.Split(':')[0];
        return name switch
        {
            "Idle" => GrblMachineMode.Idle,
            "Run" => GrblMachineMode.Run,
            "Hold" => GrblMachineMode.Hold,
            "Jog" => GrblMachineMode.Jog,
            "Alarm" => GrblMachineMode.Alarm,
            "Door" => GrblMachineMode.Door,
            "Check" => GrblMachineMode.Check,
            "Home" => GrblMachineMode.Home,
            "Sleep" => GrblMachineMode.Sleep,
            _ => GrblMachineMode.Unknown,
        };
    }

    private static Position ParsePosition(string value)
    {
        var parts = value.Split(',');
        double x = parts.Length > 0 ? ParseDouble(parts[0]) : 0;
        double y = parts.Length > 1 ? ParseDouble(parts[1]) : 0;
        double z = parts.Length > 2 ? ParseDouble(parts[2]) : 0;
        return new Position(x, y, z);
    }

    private static void ParseFeedSpindle(string value, out double feed, out double spindle)
    {
        var parts = value.Split(',');
        feed = parts.Length > 0 ? ParseDouble(parts[0]) : 0;
        spindle = parts.Length > 1 ? ParseDouble(parts[1]) : 0;
    }

    private static void ParseOverrides(string value, out int? feed, out int? rapid, out int? spindle)
    {
        var parts = value.Split(',');
        feed = parts.Length > 0 ? ParseInt(parts[0]) : null;
        rapid = parts.Length > 1 ? ParseInt(parts[1]) : null;
        spindle = parts.Length > 2 ? ParseInt(parts[2]) : null;
    }

    private static (int, int)? ParseBuffer(string value)
    {
        var parts = value.Split(',');
        if (parts.Length < 2) return null;
        var planner = ParseInt(parts[0]);
        var rx = ParseInt(parts[1]);
        if (planner is null || rx is null) return null;
        return (planner.Value, rx.Value);
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
