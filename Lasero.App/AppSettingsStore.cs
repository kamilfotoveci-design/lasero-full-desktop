using System.IO;
using System.Text.Json;
using Serilog;

namespace Lasero.App;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public DevicePreferences Device { get; set; } = new();
    public SafetyPreferences Safety { get; set; } = new();
    public MachinePreferences Machine { get; set; } = new();
    public WorkspacePreferences Workspace { get; set; } = new();
}

/// <summary>
/// Editor chrome the operator has arranged for themselves. Panel sizing is a preference, not project
/// data — it must never reach the document or the machine.
/// </summary>
public sealed class WorkspacePreferences
{
    /// <summary>Width of the right-hand inspector in device-independent pixels. Clamped on load to
    /// the same bounds the splitter enforces, so a hand-edited or stale settings file cannot start
    /// the app with the workspace collapsed.</summary>
    public double InspectorWidth { get; set; } = DefaultInspectorWidth;
    public double AssistantWidth { get; set; } = DefaultAssistantWidth;
    public double AssistantHeight { get; set; } = DefaultAssistantHeight;

    public const double DefaultInspectorWidth = 320;
    public const double DefaultAssistantWidth = 420;
    public const double DefaultAssistantHeight = 560;
    public const double MinAssistantWidth = 320;
    public const double MinAssistantHeight = 360;
    public const double MaxAssistantWidth = 720;
    public const double MaxAssistantHeight = 760;

    /// <summary>The lower bound has to match DesignerInspectorView's own MinWidth. When it was
    /// smaller (280 against the panel's 320), dragging the splitter narrow left the panel wider than
    /// the column holding it, and a Grid does not shrink a child below its MinWidth — so the extra
    /// 40px hung off the right edge of the window and the value fields were cut in half.</summary>
    public const double MinInspectorWidth = 320;
    public const double MaxInspectorWidth = 560;

    public double ClampedInspectorWidth => double.IsFinite(InspectorWidth)
        ? Math.Clamp(InspectorWidth, MinInspectorWidth, MaxInspectorWidth)
        : DefaultInspectorWidth;

    public double ClampedAssistantWidth => double.IsFinite(AssistantWidth)
        ? Math.Clamp(AssistantWidth, MinAssistantWidth, MaxAssistantWidth)
        : DefaultAssistantWidth;

    public double ClampedAssistantHeight => double.IsFinite(AssistantHeight)
        ? Math.Clamp(AssistantHeight, MinAssistantHeight, MaxAssistantHeight)
        : DefaultAssistantHeight;
}

public sealed class DevicePreferences
{
    public string? Port { get; set; }
    public int BaudRate { get; set; } = 115200;
}

public sealed class SafetyPreferences
{
    public bool RequireFramingBeforeStart { get; set; } = true;
    public bool ConfirmSoftReset { get; set; } = true;
    public bool ShowMachineStatusAfterConnect { get; set; }
}

public sealed class MachinePreferences
{
    public string? ActiveProfileId { get; set; }
    public double WorkAreaWidthMm { get; set; } = 500;
    public double WorkAreaHeightMm { get; set; } = 400;
    public Lasero.Core.Materials.LaserTechnology LaserTechnology { get; set; } = Lasero.Core.Materials.LaserTechnology.Diode;
    public int LaserPowerWatts { get; set; } = 20;
    public double JogStepMm { get; set; } = 10;
    public double JogFeedRateMmPerMinute { get; set; } = 2000;
    public bool EnableZAxis { get; set; } = true;
    public bool InvertZAxis { get; set; }
    public double ZJogStepMm { get; set; } = 1;
    public double ZJogFeedRateMmPerMinute { get; set; } = 600;
    public Dictionary<string, MachineProfilePreferences> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MachineProfilePreferences
{
    public string DisplayName { get; set; } = "Laserové zařízení";
    public double WorkAreaWidthMm { get; set; } = 500;
    public double WorkAreaHeightMm { get; set; } = 400;
    public double LastPowerPercent { get; set; } = 30;
    public double LastSpeedMmPerMinute { get; set; } = 3000;
    public double LastLineIntervalMm { get; set; } = 0.1;
    public int LastPasses { get; set; } = 1;
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static AppSettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "settings.json"));

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return Current = new AppSettings();

        try
        {
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            Normalize(Current);
            return Current;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Settings file is invalid; defaults will be used");
            PreserveCorruptFile();
            return Current = new AppSettings();
        }
    }

    public void Save()
    {
        Normalize(Current);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Nastavení nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void PreserveCorruptFile()
    {
        try
        {
            var corruptPath = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(_path, corruptPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Invalid settings file could not be preserved");
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Device ??= new DevicePreferences();
        settings.Safety ??= new SafetyPreferences();
        settings.Machine ??= new MachinePreferences();
        settings.Workspace ??= new WorkspacePreferences();
        settings.Workspace.InspectorWidth = settings.Workspace.ClampedInspectorWidth;
        settings.Workspace.AssistantWidth = settings.Workspace.ClampedAssistantWidth;
        settings.Workspace.AssistantHeight = settings.Workspace.ClampedAssistantHeight;
        settings.Machine.Profiles ??= new Dictionary<string, MachineProfilePreferences>(StringComparer.OrdinalIgnoreCase);
        if (settings.Device.BaudRate <= 0) settings.Device.BaudRate = 115200;
        if (settings.Machine.WorkAreaWidthMm <= 0) settings.Machine.WorkAreaWidthMm = 500;
        if (settings.Machine.WorkAreaHeightMm <= 0) settings.Machine.WorkAreaHeightMm = 400;
        if (!double.IsFinite(settings.Machine.JogStepMm) || settings.Machine.JogStepMm <= 0) settings.Machine.JogStepMm = 10;
        if (!double.IsFinite(settings.Machine.JogFeedRateMmPerMinute) || settings.Machine.JogFeedRateMmPerMinute <= 0) settings.Machine.JogFeedRateMmPerMinute = 2000;
        if (!double.IsFinite(settings.Machine.ZJogStepMm) || settings.Machine.ZJogStepMm <= 0) settings.Machine.ZJogStepMm = 1;
        if (!double.IsFinite(settings.Machine.ZJogFeedRateMmPerMinute) || settings.Machine.ZJogFeedRateMmPerMinute <= 0) settings.Machine.ZJogFeedRateMmPerMinute = 600;
        if (!Lasero.Core.Materials.MaterialCatalog.PowerClasses.Contains(settings.Machine.LaserPowerWatts)) settings.Machine.LaserPowerWatts = 20;
        foreach (var profile in settings.Machine.Profiles.Values)
        {
            if (!double.IsFinite(profile.WorkAreaWidthMm) || profile.WorkAreaWidthMm <= 0) profile.WorkAreaWidthMm = 500;
            if (!double.IsFinite(profile.WorkAreaHeightMm) || profile.WorkAreaHeightMm <= 0) profile.WorkAreaHeightMm = 400;
            if (!double.IsFinite(profile.LastPowerPercent) || profile.LastPowerPercent is <= 0 or > 100) profile.LastPowerPercent = 30;
            if (!double.IsFinite(profile.LastSpeedMmPerMinute) || profile.LastSpeedMmPerMinute <= 0) profile.LastSpeedMmPerMinute = 3000;
            if (!double.IsFinite(profile.LastLineIntervalMm) || profile.LastLineIntervalMm <= 0) profile.LastLineIntervalMm = 0.1;
            if (profile.LastPasses < 1) profile.LastPasses = 1;
        }
    }
}
