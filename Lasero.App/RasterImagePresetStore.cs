using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasero.Core.Raster;

namespace Lasero.App;

/// <summary>A user's image-tone recipe. This type intentionally has no geometry or laser-job
/// parameters: applying a saved look must never change image size, DPI, speed, power, or passes.</summary>
public sealed record RasterImagePreset
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public double Gamma { get; init; } = 1;
    public double Exposure { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double BlackPoint { get; init; }
    public double WhitePoint { get; init; } = 255;
    public bool Invert { get; init; }
    public double NoiseReduction { get; init; }
    public double Sharpen { get; init; }
    public int SharpenRadius { get; init; } = 1;
    public double EdgeEnhance { get; init; }
    public bool UseDithering { get; init; }
    public DitheringAlgorithm DitheringAlgorithm { get; init; } = DitheringAlgorithm.Stucki;
    public bool UseThreshold { get; init; }
    public byte ThresholdValue { get; init; } = 128;

    public ImageProcessingOptions ToOptions() => new()
    {
        Gamma = Gamma,
        Exposure = Exposure,
        Brightness = Brightness,
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        BlackPoint = BlackPoint,
        WhitePoint = WhitePoint,
        Invert = Invert,
        NoiseReduction = NoiseReduction,
        Sharpen = Sharpen,
        SharpenRadius = SharpenRadius,
        EdgeEnhance = EdgeEnhance,
        UseDithering = UseDithering,
        DitheringAlgorithm = DitheringAlgorithm,
        UseThreshold = UseThreshold,
        ThresholdValue = ThresholdValue,
    };

    internal static RasterImagePreset From(string name, ImageProcessingOptions options, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name.Trim(),
        Gamma = options.Gamma,
        Exposure = options.Exposure,
        Brightness = options.Brightness,
        Contrast = options.Contrast,
        Highlights = options.Highlights,
        Shadows = options.Shadows,
        BlackPoint = options.BlackPoint,
        WhitePoint = options.WhitePoint,
        Invert = options.Invert,
        NoiseReduction = options.NoiseReduction,
        Sharpen = options.Sharpen,
        SharpenRadius = options.SharpenRadius,
        EdgeEnhance = options.EdgeEnhance,
        UseDithering = options.UseDithering,
        DitheringAlgorithm = options.DitheringAlgorithm,
        UseThreshold = options.UseThreshold,
        ThresholdValue = options.ThresholdValue,
    };
}

/// <summary>Persists user-created image-tone presets. The built-in recommendations are owned by
/// the image workflow and are deliberately not stored here.</summary>
public sealed class RasterImagePresetStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private List<RasterImagePreset> _presets;

    public RasterImagePresetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _presets = ReadFromDisk();
    }

    public static RasterImagePresetStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "raster-image-presets.json"));

    public IReadOnlyList<RasterImagePreset> List() => _presets.ToArray();

    public RasterImagePreset? Load(Guid id) => _presets.FirstOrDefault(preset => preset.Id == id);

    /// <summary>Adds a preset or updates the existing preset with the supplied id.</summary>
    public RasterImagePreset Save(string name, ImageProcessingOptions options, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var preset = RasterImagePreset.From(name, options, id);
        Validate(preset);

        var updated = _presets.ToList();
        var index = updated.FindIndex(item => item.Id == preset.Id);
        if (index < 0) updated.Add(preset);
        else updated[index] = preset;
        WriteAtomically(updated);
        _presets = updated;
        return preset;
    }

    public bool Delete(Guid id)
    {
        var updated = _presets.Where(preset => preset.Id != id).ToList();
        if (updated.Count == _presets.Count) return false;
        WriteAtomically(updated);
        _presets = updated;
        return true;
    }

    /// <summary>Exports one preset in the same versioned format accepted by <see cref="Import"/>.</summary>
    public string Export(Guid id)
    {
        var preset = Load(id) ?? throw new KeyNotFoundException("Obrazový preset nebyl nalezen.");
        return JsonSerializer.Serialize(new PresetDocument(CurrentVersion, [preset]), JsonOptions);
    }

    /// <summary>Imports one validated preset. An existing id is rejected to prevent accidental overwrite.</summary>
    public RasterImagePreset Import(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PresetDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PresetDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Soubor s obrazovým presetem je prázdný.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Soubor s obrazovým presetem nemá platný formát.", ex);
        }

        ValidateDocument(document);
        var preset = document.Items[0];
        if (_presets.Any(item => item.Id == preset.Id))
            throw new InvalidDataException("Obrazový preset se stejným ID už existuje.");

        var updated = _presets.Append(preset).ToList();
        WriteAtomically(updated);
        _presets = updated;
        return preset;
    }

    private List<RasterImagePreset> ReadFromDisk()
    {
        if (!File.Exists(_path)) return [];

        PresetDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("Katalog obrazových presetů je prázdný.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Katalog obrazových presetů nemá platný formát.", ex);
        }

        ValidateDocument(document);
        return document.Items.ToList();
    }

    private void WriteAtomically(List<RasterImagePreset> presets)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Katalog obrazových presetů nemá cílovou složku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(new PresetDocument(CurrentVersion, presets), JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ValidateDocument(PresetDocument document)
    {
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Nepodporovaná verze katalogu obrazových presetů: {document.Version}.");
        if (document.Items is null)
            throw new InvalidDataException("Katalog obrazových presetů neobsahuje seznam položek.");
        if (document.Items.Any(item => item is null))
            throw new InvalidDataException("Katalog obrazových presetů obsahuje prázdnou položku.");
        if (document.Items.Select(item => item.Id).Distinct().Count() != document.Items.Count)
            throw new InvalidDataException("Katalog obrazových presetů obsahuje duplicitní ID.");
        foreach (var preset in document.Items) Validate(preset);
    }

    private static void Validate(RasterImagePreset preset)
    {
        if (preset.Id == Guid.Empty)
            throw new InvalidDataException("Obrazový preset nemá platné ID.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 60)
            throw new InvalidDataException("Název obrazového presetu musí mít 1 až 60 znaků.");
        RequireRange(preset.Gamma, 0.1, 3, nameof(preset.Gamma));
        RequireRange(preset.Exposure, -100, 100, nameof(preset.Exposure));
        RequireRange(preset.Brightness, -100, 100, nameof(preset.Brightness));
        RequireRange(preset.Contrast, -100, 100, nameof(preset.Contrast));
        RequireRange(preset.Highlights, -100, 100, nameof(preset.Highlights));
        RequireRange(preset.Shadows, -100, 100, nameof(preset.Shadows));
        RequireRange(preset.BlackPoint, 0, 128, nameof(preset.BlackPoint));
        RequireRange(preset.WhitePoint, 128, 255, nameof(preset.WhitePoint));
        RequireRange(preset.NoiseReduction, 0, 100, nameof(preset.NoiseReduction));
        RequireRange(preset.Sharpen, 0, 100, nameof(preset.Sharpen));
        if (preset.SharpenRadius is < 1 or > 4)
            throw new InvalidDataException("Poloměr doostření musí být v rozsahu 1 až 4 bodů.");
        RequireRange(preset.EdgeEnhance, 0, 100, nameof(preset.EdgeEnhance));
        if (!Enum.IsDefined(preset.DitheringAlgorithm))
            throw new InvalidDataException("Obrazový preset obsahuje neznámý dithering algoritmus.");
    }

    private static void RequireRange(double value, double minimum, double maximum, string propertyName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Hodnota {propertyName} musí být v rozsahu {minimum} až {maximum}.");
    }

    private sealed record PresetDocument(int Version, List<RasterImagePreset> Items);
}
