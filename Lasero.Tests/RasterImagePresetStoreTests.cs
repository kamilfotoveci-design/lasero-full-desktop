using System.Text.Json;
using System.IO;
using Lasero.App;
using Lasero.Core.Raster;

namespace Lasero.Tests;

public sealed class RasterImagePresetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LaseroTests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "image-presets.json");

    [Fact]
    public void SaveAndReload_RoundTripsEveryTonalSetting()
    {
        var store = new RasterImagePresetStore(StorePath);
        var options = new ImageProcessingOptions
        {
            Gamma = 1.4,
            Exposure = -12,
            Brightness = 8,
            Contrast = 24,
            Highlights = -17,
            Shadows = 31,
            BlackPoint = 9,
            WhitePoint = 242,
            Invert = true,
            NoiseReduction = 18,
            Sharpen = 42,
            EdgeEnhance = 14,
            UseDithering = true,
            DitheringAlgorithm = DitheringAlgorithm.Atkinson,
            UseThreshold = true,
            ThresholdValue = 177,
        };

        var saved = store.Save("  Portrait  ", options);
        var reloaded = new RasterImagePresetStore(StorePath).Load(saved.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("Portrait", reloaded.Name);
        Assert.Equal(options, reloaded.ToOptions());
    }

    [Fact]
    public void PersistedPresetContainsNoMachineJobParameters()
    {
        var store = new RasterImagePresetStore(StorePath);
        var saved = store.Save("Look", new ImageProcessingOptions());
        var json = store.Export(saved.Id);
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("Items")[0];
        var names = item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Width", names);
        Assert.DoesNotContain("TargetWidthMm", names);
        Assert.DoesNotContain("Dpi", names);
        Assert.DoesNotContain("Speed", names);
        Assert.DoesNotContain("FeedRatePerMinute", names);
        Assert.DoesNotContain("Power", names);
        Assert.DoesNotContain("MaxPower", names);
        Assert.DoesNotContain("Passes", names);
        Assert.Contains("Gamma", names);
        Assert.Contains("DitheringAlgorithm", names);
    }

    [Fact]
    public void Save_RejectsOutOfRangeAndNonFiniteValues()
    {
        var store = new RasterImagePresetStore(StorePath);

        Assert.Throws<InvalidDataException>(() => store.Save("Bad gamma", new ImageProcessingOptions { Gamma = 0 }));
        Assert.Throws<InvalidDataException>(() => store.Save("Bad brightness", new ImageProcessingOptions { Brightness = 101 }));
        Assert.Throws<InvalidDataException>(() => store.Save("NaN", new ImageProcessingOptions { Contrast = double.NaN }));
        Assert.Empty(store.List());
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void ImportExport_RoundTripsAndDeleteRemovesThePreset()
    {
        var source = new RasterImagePresetStore(Path.Combine(_directory, "source.json"));
        var original = source.Save("Wood photo", new ImageProcessingOptions
        {
            Gamma = 0.9,
            Exposure = 6,
            Brightness = -4,
            Contrast = 13,
            Highlights = -22,
            Shadows = 18,
            BlackPoint = 5,
            WhitePoint = 250,
            Invert = true,
            NoiseReduction = 10,
            Sharpen = 20,
            EdgeEnhance = 5,
            UseDithering = true,
            DitheringAlgorithm = DitheringAlgorithm.Sierra,
        });
        var destination = new RasterImagePresetStore(Path.Combine(_directory, "destination.json"));

        var imported = destination.Import(source.Export(original.Id));

        Assert.Equal(original, imported);
        Assert.Equal(original, destination.List().Single());
        Assert.True(destination.Delete(imported.Id));
        Assert.False(destination.Delete(imported.Id));
        Assert.Empty(destination.List());
    }

    [Fact]
    public void Import_RejectsUnknownMachineFieldsAndUnsupportedVersion()
    {
        var store = new RasterImagePresetStore(StorePath);
        var id = Guid.NewGuid();
        var machineParameter = $$"""{"Version":1,"Items":[{"Id":"{{id}}","Name":"Unsafe","Dpi":254}]}""";
        var unknownVersion = $$"""{"Version":2,"Items":[{"Id":"{{id}}","Name":"Unsafe"}]}""";

        Assert.Throws<InvalidDataException>(() => store.Import(machineParameter));
        Assert.Throws<InvalidDataException>(() => store.Import(unknownVersion));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Import_RejectsDuplicateIdsInsteadOfOverwriting()
    {
        var source = new RasterImagePresetStore(Path.Combine(_directory, "source.json"));
        var saved = source.Save("Look", new ImageProcessingOptions());
        var target = new RasterImagePresetStore(Path.Combine(_directory, "target.json"));
        target.Import(source.Export(saved.Id));

        Assert.Throws<InvalidDataException>(() => target.Import(source.Export(saved.Id)));
        Assert.Single(target.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
