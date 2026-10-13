using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Lasero.App;
using Lasero.App.Converters;
using Lasero.App.Views;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

/// <summary>
/// The settings that the rebuilt device window can change and that reach the machine: persistence,
/// clamping on load, the mapping into job lines, validation, and the window markup itself (four tabs,
/// no sliders, nothing that is not wired).
/// </summary>
public sealed class DeviceSettingsPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-device-settings-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public DeviceSettingsPreferencesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void NewSettingsDefaultToTheBehaviourTheAppAlreadyHad()
    {
        var machine = new AppSettingsStore(SettingsPath).Load().Machine;

        Assert.Equal(FramingOptions.VisiblePowerPercent, machine.FramingPowerPercent);
        Assert.Equal(AirAssistMode.Off, machine.AirAssist);
        Assert.False(machine.ReturnToStartAfterJob);
        Assert.True(machine.GetJobProgramOptions(JobPlacementMode.AbsoluteCoordinates, 0, 0).IsEmpty);
    }

    [Fact]
    public void FramingPowerAirAssistAndReturnSurviveSaveAndLoad()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Machine.FramingPowerPercent = 3.5;
        store.Current.Machine.AirAssist = AirAssistMode.M7;
        store.Current.Machine.ReturnToStartAfterJob = true;
        store.Save();

        var machine = new AppSettingsStore(SettingsPath).Load().Machine;

        Assert.Equal(3.5, machine.FramingPowerPercent);
        Assert.Equal(AirAssistMode.M7, machine.AirAssist);
        Assert.True(machine.ReturnToStartAfterJob);
    }

    [Fact]
    public void ASettingsFileFromBeforeTheseOptionsExistedLoadsWithDefaults()
    {
        File.WriteAllText(SettingsPath, """{ "Machine": { "WorkAreaWidthMm": 410, "WorkAreaHeightMm": 400 } }""");

        var machine = new AppSettingsStore(SettingsPath).Load().Machine;

        Assert.Equal(410, machine.WorkAreaWidthMm);
        Assert.Equal(FramingOptions.VisiblePowerPercent, machine.FramingPowerPercent);
        Assert.Equal(AirAssistMode.Off, machine.AirAssist);
        Assert.False(machine.ReturnToStartAfterJob);
    }

    [Fact]
    public void ImplausibleStoredValuesAreClampedOrResetOnLoad()
    {
        File.WriteAllText(SettingsPath, """{ "Machine": { "FramingPowerPercent": 80, "AirAssist": 9 } }""");

        var machine = new AppSettingsStore(SettingsPath).Load().Machine;

        Assert.Equal(FramingOptions.MaxVisiblePowerPercent, machine.FramingPowerPercent);
        Assert.Equal(AirAssistMode.Off, machine.AirAssist);
    }

    [Fact]
    public void FramingPowerIsClampedEvenWhenTheObjectWasFilledInCode()
    {
        var machine = new MachinePreferences { FramingPowerPercent = 99 };
        Assert.Equal(FramingOptions.MaxVisiblePowerPercent, machine.GetFramingPowerPercent());
    }

    // ------------------------------------------------------------------ job options

    [Fact]
    public void ReturnTargetIsTheWorkZeroInAbsoluteModeAndTheReferencePointFromTheCurrentPosition()
    {
        var machine = new MachinePreferences { ReturnToStartAfterJob = true, AirAssist = AirAssistMode.M8 };

        var absolute = machine.GetJobProgramOptions(JobPlacementMode.AbsoluteCoordinates, 55, 66);
        var current = machine.GetJobProgramOptions(JobPlacementMode.CurrentPosition, 55, 66);

        Assert.Equal<(double, double)?>((0d, 0d), absolute.ReturnTo);
        Assert.Equal<(double, double)?>((55d, 66d), current.ReturnTo);
        Assert.Equal(AirAssistMode.M8, absolute.AirAssist);
    }

    [Fact]
    public void NoReturnTargetWhenTheSwitchIsOff()
    {
        var options = new MachinePreferences { AirAssist = AirAssistMode.M8 }
            .GetJobProgramOptions(JobPlacementMode.CurrentPosition, 55, 66);

        Assert.Null(options.ReturnTo);
        Assert.Equal(["M8"], JobProgram.Prologue(options));
    }

    // ------------------------------------------------------------------ draft and window

    [Fact]
    public void TheGCodeTabPreviewFollowsTheDraft()
    {
        var draft = new DeviceSettingsDraft();
        Assert.Equal("G90\nG21\nM5", draft.StartLinesPreview);
        Assert.Equal("M5", draft.EndLinesPreview);

        var changed = new List<string?>();
        draft.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        draft.AirAssist = AirAssistMode.M8;
        draft.ReturnToStartAfterJob = true;

        Assert.Contains(nameof(DeviceSettingsDraft.StartLinesPreview), changed);
        Assert.Contains(nameof(DeviceSettingsDraft.EndLinesPreview), changed);
        Assert.Equal("M8\nG90\nG21\nM5", draft.StartLinesPreview);
        Assert.Equal("M5\nM9\nG90\nG0 X0 Y0\nM5", draft.EndLinesPreview);
    }

    [Fact]
    public void ValidationNamesTheTabThatHoldsTheBadField()
    {
        Assert.Null(DeviceSettingsView.Validate(new DeviceSettingsDraft()));

        Assert.Contains("Rozměry / Jednotky", DeviceSettingsView.Validate(new DeviceSettingsDraft { WorkAreaWidthMm = 0 }));
        Assert.Contains("Rozměry / Jednotky", DeviceSettingsView.Validate(new DeviceSettingsDraft { ZJogStepMm = -1 }));
        Assert.Contains("Základní nastavení", DeviceSettingsView.Validate(new DeviceSettingsDraft { FramingPowerPercent = 11 }));
        Assert.Contains("Základní nastavení", DeviceSettingsView.Validate(new DeviceSettingsDraft { FramingPowerPercent = -1 }));
        Assert.Contains("Další nastavení", DeviceSettingsView.Validate(new DeviceSettingsDraft { JogFeedRateMmPerMinute = double.NaN }));
        Assert.Null(DeviceSettingsView.Validate(new DeviceSettingsDraft { FramingPowerPercent = 0 }));
        Assert.Null(DeviceSettingsView.Validate(new DeviceSettingsDraft { FramingPowerPercent = FramingOptions.MaxVisiblePowerPercent }));
    }

    [Fact]
    public void ReadoutShowsOnlyWhatTheControllerReported()
    {
        var unknown = DeviceReadout.From(null);
        Assert.Equal(unknown.MaxSpindle, unknown.LaserMode);
        Assert.Equal(unknown.MaxSpindle, unknown.Travel);

        var reported = DeviceReadout.From(GrblDeviceProfileParser.Parse(["$30=1000", "$32=1", "$130=400", "$131=410"]));
        Assert.Equal(1000d.ToString("0.###", CultureInfo.CurrentCulture), reported.MaxSpindle);
        Assert.Equal("Zapnutý ($32=1)", reported.LaserMode);
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"{400d:0.###} × {410d:0.###} mm"), reported.Travel);

        var off = DeviceReadout.From(GrblDeviceProfileParser.Parse(["$32=0"]));
        Assert.Equal("Vypnutý ($32=0)", off.LaserMode);
        Assert.Equal(unknown.MaxSpindle, off.MaxSpindle);
    }

    // ------------------------------------------------------------------ typed numbers

    [Theory]
    [InlineData("0,5", 0.5)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 1 250 ", 1250)]
    [InlineData("-3", -3)]
    [InlineData("2000", 2000)]
    public void TypedNumbersAcceptEitherDecimalSeparator(string text, double expected)
    {
        Assert.True(DecimalTextConverter.TryParse(text, out var value));
        Assert.Equal(expected, value);
        Assert.Equal(expected, new DecimalTextConverter().ConvertBack(text, typeof(double), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("NaN")]
    [InlineData("∞")]
    public void ATypedValueThatIsNotANumberIsAnErrorNotASilentZero(string text)
    {
        Assert.False(DecimalTextConverter.TryParse(text, out _));
        Assert.Throws<FormatException>(() => new DecimalTextConverter().ConvertBack(text, typeof(double), null!, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ markup

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private static string Markup(params string[] parts) =>
        Regex.Replace(File.ReadAllText(Path.Combine([Root(), "Lasero.App", .. parts])), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void TheScreenKeepsTheConnectionPageAndTheFourLightBurnTabsInOneStripInOrder()
    {
        var xaml = Markup("Views", "DeviceView.xaml");
        var headers = Regex.Matches(xaml, @"<TabItem\b[^>]*\bHeader=""([^""]+)""").Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(["Připojení a ovládání", "Rozměry / Jednotky", "Základní nastavení", "G-code", "Další nastavení"], headers);
        Assert.Equal(1, Regex.Matches(xaml, "<TabControl\b").Count);
        // The strip and the settings pages are driven by the one enum, declared in the same order as the tabs.
        Assert.Equal(["Connection", "Dimensions", "Basic", "GCode", "More"], Enum.GetNames<Lasero.App.ViewModels.DeviceScreenTab>());
        Assert.Contains("<views:DeviceSettingsView", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceSettingsAreNoLongerAWindow()
    {
        Assert.False(File.Exists(Path.Combine(Root(), "Lasero.App", "DeviceSettingsWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(Root(), "Lasero.App", "DeviceSettingsWindow.xaml.cs")));
        Assert.DoesNotContain("<Window", Markup("Views", "DeviceSettingsView.xaml"), StringComparison.Ordinal);

        // No title bar, page header or wizard button opens a settings dialog any more.
        Assert.DoesNotContain("Nastavení zařízení", Markup("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeviceSettingsClick", Markup("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Nastavení zařízení", Markup("Views", "DeviceView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("OnOpenSettingsClick", Markup("Views", "DeviceSetup", "DeviceWizardOverlay.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceSettingsWindow", File.ReadAllText(Path.Combine(Root(), "Lasero.App", "MainWindow.xaml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void NoValueOnThePagesIsASliderAndEveryNumberIsTyped()
    {
        var xaml = Markup("Views", "DeviceSettingsView.xaml");

        Assert.DoesNotContain("<Slider", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ParameterSlider", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox", xaml, StringComparison.Ordinal);
        Assert.True(Regex.Matches(xaml, "<components:NumericField ").Count >= 7);
    }

    [Fact]
    public void EveryToggleIsBoundToAKnownDraftProperty()
    {
        var xaml = Markup("Views", "DeviceSettingsView.xaml");
        var bound = Regex.Matches(xaml, @"SettingsSwitch\}""\s+IsChecked=""\{Binding Draft\.(\w+)\}""").Select(m => m.Groups[1].Value).ToList();
        var properties = typeof(DeviceSettingsDraft).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.NotEmpty(bound);
        Assert.Equal(Regex.Matches(xaml, "<ToggleButton").Count, bound.Count);
        foreach (var name in bound) Assert.Contains(name, properties);
    }

    [Fact]
    public void EveryDraftValueThePagesBindIsWrittenBackOnSave()
    {
        var xaml = Markup("Views", "DeviceSettingsView.xaml");
        var code = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "DeviceSettingsView.xaml.cs"));
        var bound = Regex.Matches(xaml, @"Draft\.(\w+)").Select(m => m.Groups[1].Value).Distinct()
            // Previews are derived from the other draft values and never stored.
            .Where(name => !name.EndsWith("Preview", StringComparison.Ordinal));

        foreach (var name in bound)
            Assert.True(Regex.IsMatch(code, @"=\s*Draft\." + name + @"\b"), $"Draft.{name} is shown on the page but never saved");
    }

    [Fact]
    public void EveryDraftValueIsEditedOnAPageOrDerived()
    {
        // A value in the draft that no page shows would be written back on save without the operator ever seeing it.
        var xaml = Markup("Views", "DeviceSettingsView.xaml");
        var shown = Regex.Matches(xaml, @"Draft\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
        var derived = new[] { nameof(DeviceSettingsDraft.StartLinesPreview), nameof(DeviceSettingsDraft.EndLinesPreview) };
        var properties = typeof(DeviceSettingsDraft).GetProperties()
            .Where(p => p.DeclaringType == typeof(DeviceSettingsDraft)).Select(p => p.Name);

        foreach (var name in properties.Except(derived)) Assert.Contains(name, shown);
    }

    [Fact]
    public void ThePortAndBaudRateAreChosenOnTheConnectionPageOnlyAndNeverOverwrittenBySave()
    {
        var page = Markup("Views", "DeviceSettingsView.xaml");
        var code = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "DeviceSettingsView.xaml.cs"));
        Assert.DoesNotContain("Draft.Port", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Draft.BaudRate", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedPort", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BaudRate", code, StringComparison.Ordinal);
        Assert.Contains("Connection.SelectedPortOption", Markup("Views", "DeviceView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeWalkRemembersWhatItHasVisited()
    {
        // Walking the logical and the visual tree without a visited set froze the app on Uložit.
        var code = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "DeviceSettingsView.xaml.cs"));
        Assert.Contains("HashSet<DependencyObject>", code, StringComparison.Ordinal);
        Assert.Contains("visited.Add(node)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolsGlyphStillExistsForTheNavigationAndIsNotTheCog()
    {
        var icons = Markup("Theme", "FluentIcons.xaml");
        Assert.Contains("x:Key=\"Glyph.Tools\"", icons, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph.Settings", Markup("Views", "DeviceView.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph.Settings", Markup("Views", "DeviceSettingsView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void DraftComparisonSeesEveryValueChange()
    {
        var baseline = new DeviceSettingsDraft();
        Assert.True(baseline.ValuesEqual(new DeviceSettingsDraft()));

        var edits = new Action<DeviceSettingsDraft>[]
        {
            d => d.WorkAreaWidthMm += 1, d => d.WorkAreaHeightMm += 1, d => d.JogStepMm += 1, d => d.JogFeedRateMmPerMinute += 1,
            d => d.ZJogStepMm += 1, d => d.ZJogFeedRateMmPerMinute += 1, d => d.FramingPowerPercent += 1,
            d => d.EnableZAxis = !d.EnableZAxis, d => d.InvertZAxis = !d.InvertZAxis, d => d.RequireFramingBeforeStart = !d.RequireFramingBeforeStart,
            d => d.ConfirmSoftReset = !d.ConfirmSoftReset, d => d.ShowMachineStatusAfterConnect = !d.ShowMachineStatusAfterConnect,
            d => d.ReturnToStartAfterJob = !d.ReturnToStartAfterJob, d => d.AirAssist = AirAssistMode.M8,
            d => d.LaserPowerWatts += 1, d => d.LaserTechnology = Lasero.Core.Materials.LaserTechnology.Co2,
        };
        foreach (var edit in edits)
        {
            var changed = new DeviceSettingsDraft();
            edit(changed);
            Assert.False(baseline.ValuesEqual(changed));
        }
    }
}
