using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasero.Core.Grbl;
using Lasero.Core.GCode;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Jobs;

namespace Lasero.App;

public sealed class LaseroProjectFile
{
    public int Version { get; set; } = 7;
    public string Name { get; set; } = "Nový projekt";
    public List<ProjectObject> Objects { get; set; } = [];
    public List<ProjectLayer> Layers { get; set; } = [];
    public ProjectJobPlacement? Placement { get; set; }
}

public sealed class ProjectJobPlacement
{
    // CurrentPosition is intentionally the enum default so projects saved by
    // older versions (which only stored a captured position) keep their intent.
    public JobPlacementMode Mode { get; set; } = JobPlacementMode.CurrentPosition;
    public JobOriginAnchor Anchor { get; set; } = JobOriginAnchor.TopLeft;
    public double ReferenceX { get; set; }
    public double ReferenceY { get; set; }
}

public sealed class ProjectObject
{
    public string Name { get; set; } = string.Empty;
    public List<ProjectShape> Shapes { get; set; } = [];
    public Position LocalPivot { get; set; }
    public BoundingBox2D LocalBounds { get; set; }
    public ObjectTransform Transform { get; set; } = ObjectTransform.Identity;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool IncludeInOutput { get; set; } = true;
    public string? RasterFilePath { get; set; }
    public string? RasterAssetEntry { get; set; }
    public RasterImportOptions? RasterOptions { get; set; }
}

public sealed class ProjectShape
{
    public Guid GeometrySetId { get; set; }
    public Guid LayerId { get; set; }
    public List<Position> Points { get; set; } = [];
    public bool IsClosed { get; set; }
    public RgbColor LayerColor { get; set; }
    public LayerMode PreferredMode { get; set; }
}

public sealed class ProjectLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RgbColor Color { get; set; }
    public string Name { get; set; } = string.Empty;
    public LayerMode Mode { get; set; }
    public double Speed { get; set; }
    public double Power { get; set; }
    public int Passes { get; set; }
    public double FillLineIntervalMm { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public bool IsRaster { get; set; }
}

public static class ProjectFileSerializer
{
    private const string ManifestEntryName = "project.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(LaseroProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static void Save(string path, LaseroProjectFile project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Projekt nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var persistedProject = CreateArchiveSnapshot(project, archive);
                    var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                    using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                    writer.Write(Serialize(persistedProject));
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static LaseroProjectFile Load(string path, string? assetCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        if (LooksLikeJson(fullPath))
            return Deserialize(File.ReadAllText(fullPath));

        using var archive = ZipFile.OpenRead(fullPath);
        var manifest = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Balíček neobsahuje project.json.");
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var project = Deserialize(reader.ReadToEnd());

        var cacheDirectory = assetCacheDirectory ?? GetDefaultAssetCacheDirectory(fullPath);
        ExtractRasterAssets(project, archive, cacheDirectory);
        return project;
    }

    public static LaseroProjectFile Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<LaseroProjectFile>(json, Options)
            ?? throw new InvalidDataException("Projekt je prázdný nebo poškozený.");
        MigrateLayerIds(project);
        project.Version = 6;
        return project;
    }

    private static LaseroProjectFile CreateArchiveSnapshot(LaseroProjectFile source, ZipArchive archive)
    {
        var snapshot = Deserialize(Serialize(source));
        snapshot.Version = 6;

        for (var index = 0; index < snapshot.Objects.Count; index++)
        {
            var item = snapshot.Objects[index];
            if (string.IsNullOrWhiteSpace(item.RasterFilePath))
                continue;

            var rasterPath = Path.GetFullPath(item.RasterFilePath);
            if (!File.Exists(rasterPath))
                throw new FileNotFoundException($"Zdrojový obrázek objektu „{item.Name}“ nebyl nalezen.", rasterPath);

            var extension = NormalizeAssetExtension(Path.GetExtension(rasterPath));
            var entryName = $"assets/raster-{index:D4}{extension}";
            var assetEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var input = new FileStream(rasterPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = assetEntry.Open())
                input.CopyTo(output);

            item.RasterAssetEntry = entryName;
            item.RasterFilePath = null;
        }

        return snapshot;
    }

    private static void MigrateLayerIds(LaseroProjectFile project)
    {
        foreach (var layer in project.Layers.Where(layer => layer.Id == Guid.Empty))
            layer.Id = Guid.NewGuid();

        foreach (var item in project.Objects)
        foreach (var shape in item.Shapes)
        {
            if (shape.LayerId != Guid.Empty && project.Layers.Any(layer => layer.Id == shape.LayerId))
                continue;

            var matchingLayer = project.Layers.FirstOrDefault(layer => layer.Color.IsApproximately(shape.LayerColor));
            if (matchingLayer is not null)
                shape.LayerId = matchingLayer.Id;
        }
    }

    private static void ExtractRasterAssets(LaseroProjectFile project, ZipArchive archive, string cacheDirectory)
    {
        foreach (var item in project.Objects)
        {
            if (string.IsNullOrWhiteSpace(item.RasterAssetEntry))
                continue;

            ValidateAssetEntryName(item.RasterAssetEntry);
            var entry = archive.GetEntry(item.RasterAssetEntry)
                ?? throw new InvalidDataException($"Projekt neobsahuje rastrový asset „{item.RasterAssetEntry}“.");

            Directory.CreateDirectory(cacheDirectory);
            var destination = Path.Combine(cacheDirectory, Path.GetFileName(entry.FullName));
            var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            item.RasterFilePath = destination;
        }
    }

    private static string GetDefaultAssetCacheDirectory(string projectPath)
    {
        var file = new FileInfo(projectPath);
        var identity = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lasero", "project-assets", hash);
    }

    private static bool LooksLikeJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int value;
        do
        {
            value = stream.ReadByte();
        } while (value >= 0 && char.IsWhiteSpace((char)value));
        return value == '{';
    }

    private static string NormalizeAssetExtension(string extension)
    {
        var normalized = extension.ToLowerInvariant();
        return normalized is ".png" or ".jpg" or ".jpeg" or ".bmp" ? normalized : ".bin";
    }

    private static void ValidateAssetEntryName(string entryName)
    {
        if (!entryName.StartsWith("assets/", StringComparison.Ordinal)
            || entryName.Contains("..", StringComparison.Ordinal)
            || entryName.Contains('\\'))
        {
            throw new InvalidDataException("Projekt obsahuje neplatnou cestu rastrového assetu.");
        }
    }
}
