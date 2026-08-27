using System.IO;
using Lasero.App;

namespace Lasero.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-settings-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public AppSettingsStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SaveAndLoadPersistsSafetyDeviceAndMachineSettings()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Safety.RequireFramingBeforeStart = false;
        store.Current.Device.Port = "COM7";
        store.Current.Device.BaudRate = 230400;
        store.Current.Machine.WorkAreaWidthMm = 600;
        store.Save();

        var loaded = new AppSettingsStore(SettingsPath).Load();

        Assert.False(loaded.Safety.RequireFramingBeforeStart);
        Assert.Equal("COM7", loaded.Device.Port);
        Assert.Equal(230400, loaded.Device.BaudRate);
        Assert.Equal(600, loaded.Machine.WorkAreaWidthMm);
    }

    [Fact]
    public void CorruptSettingsArePreservedAndDefaultsAreReturned()
    {
        File.WriteAllText(SettingsPath, "{broken");
        var store = new AppSettingsStore(SettingsPath);

        var loaded = store.Load();

        Assert.Equal(115200, loaded.Device.BaudRate);
        Assert.True(loaded.Safety.RequireFramingBeforeStart);
        Assert.False(File.Exists(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void RepeatedSaveOverwritesWithoutTemporaryFiles()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Device.BaudRate = 9600;
        store.Save();
        store.Current.Device.BaudRate = 115200;
        store.Save();

        Assert.Equal(115200, new AppSettingsStore(SettingsPath).Load().Device.BaudRate);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void MachineWorkspaceAndLastEngravingSettingsArePersistedPerMachineProfile()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Machine.ActiveProfileId = "grbl-laser-400x400";
        store.Current.Machine.Profiles["grbl-laser-400x400"] = new MachineProfilePreferences
        {
            DisplayName = "Dílenský laser", WorkAreaWidthMm = 400, WorkAreaHeightMm = 400,
            LastPowerPercent = 20, LastSpeedMmPerMinute = 1000, LastLineIntervalMm = 0.1, LastPasses = 1,
        };
        store.Save();

        var loaded = new AppSettingsStore(SettingsPath).Load();
        var profile = loaded.Machine.Profiles["grbl-laser-400x400"];
        Assert.Equal("grbl-laser-400x400", loaded.Machine.ActiveProfileId);
        Assert.Equal(400, profile.WorkAreaWidthMm);
        Assert.Equal(400, profile.WorkAreaHeightMm);
        Assert.Equal(20, profile.LastPowerPercent);
        Assert.Equal(1000, profile.LastSpeedMmPerMinute);
        Assert.Equal(0.1, profile.LastLineIntervalMm);
        Assert.Equal(1, profile.LastPasses);
    }

    [Fact]
    public void SaveAndLoadPersistsMachineJogAndZAxisPreferences()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Machine.JogStepMm = 5;
        store.Current.Machine.JogFeedRateMmPerMinute = 4500;
        store.Current.Machine.EnableZAxis = false;
        store.Current.Machine.InvertZAxis = true;
        store.Current.Machine.ZJogStepMm = 0.5;
        store.Current.Machine.ZJogFeedRateMmPerMinute = 300;
        store.Save();

        var loaded = new AppSettingsStore(SettingsPath).Load();

        Assert.Equal(5, loaded.Machine.JogStepMm);
        Assert.Equal(4500, loaded.Machine.JogFeedRateMmPerMinute);
        Assert.False(loaded.Machine.EnableZAxis);
        Assert.True(loaded.Machine.InvertZAxis);
        Assert.Equal(0.5, loaded.Machine.ZJogStepMm);
        Assert.Equal(300, loaded.Machine.ZJogFeedRateMmPerMinute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
