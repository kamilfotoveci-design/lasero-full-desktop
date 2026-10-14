using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>What loading could not restore faithfully (a dropped corrupt object, a missing source
    /// image). Never written to the file; filled by <see cref="ProjectFileSerializer"/> on load.</summary>
    [JsonIgnore]
    public List<string> LoadWarnings { get; } = [];
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
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ProjectShape> Shapes { get; set; } = [];
    public Position LocalPivot { get; set; }
    public BoundingBox2D LocalBounds { get; set; }
    public ObjectTransform Transform { get; set; } = ObjectTransform.Identity;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool IncludeInOutput { get; set; } = true;
    public Guid? GroupId { get; set; }
    public List<Guid> NestedGroupIds { get; set; } = [];
    public string? RasterFilePath { get; set; }
    public string? RasterAssetEntry { get; set; }
    public RasterImportOptions? RasterOptions { get; set; }

    /// <summary>Present only once background removal has run on this object — the untouched original
    /// import, packaged the same way RasterFilePath/RasterAssetEntry are so it round-trips through
    /// save/load exactly like the (possibly background-removed) file RasterFilePath points at.</summary>
    public string? OriginalRasterFilePath { get; set; }
    public string? OriginalRasterAssetEntry { get; set; }

    /// <summary>Present only for text created with the text tool. Absent in projects saved before
    /// text became editable, and absent for every other kind of object, so the field is a plain
    /// addition: an older reader ignores it and an older project simply has no text to restore.</summary>
    public TextSource? Text { get; set; }

    /// <summary>Curve-preserving source for paths created by the pen tool. Older projects omit this
    /// and continue to load their flattened Shapes; keeping both mirrors editable Text plus its
    /// cached outlines and lets rendering/toolpath consumers retain the existing shape contract.</summary>
    public VectorPath? VectorPath { get; set; }
    public OffsetLink? OffsetLink { get; set; }
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

    /// <summary>Material recipe the numbers came from. Added after v6 files shipped; absent means
    /// the layer predates the field or was set by hand, and both read as "Vlastní nastavení".</summary>
    public string? MaterialLabel { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool IsRaster { get; set; }
    /// <summary>Implicit operation layer removed with its last object. Absent in older projects:
    /// false keeps existing user-created and imported empty layers intact.</summary>
    public bool RemoveWhenEmpty { get; set; }
}

public static class ProjectFileSerializer
{
    public const int CurrentVersion = 7;
    private const string ManifestEntryName = "project.json";
    private const string AssetCacheMarkerName = "project.txt";

    /// <summary>A leftover temp file from a crashed save is removed once it is this old; a younger
    /// one may belong to a save that is still running.</summary>
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromMinutes(10);

    /// <summary>Asset cache folders nobody has opened for this long are removed.</summary>
    private static readonly TimeSpan StaleAssetCacheAge = TimeSpan.FromDays(30);

    private static readonly Regex SafeExtension = new(@"^\.[a-z0-9]{1,8}$", RegexOptions.Compiled);

    // NaN/Infinity are written as named literals instead of failing the whole save: one poisoned
    // coordinate must not make Save and autosave impossible. Loading refuses such geometry (see
    // DropNonFiniteObjects), so it can never reach a toolpath.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(LaseroProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>Writes the project atomically (temp file in the same folder, then replace). With
    /// <paramref name="keepBackup"/> the previous readable file is first kept as "name.bak".
    /// Returns operator-facing notes about anything that could not be embedded; a missing source
    /// image never fails the save — the project is still written and keeps the image path.</summary>
    public static IReadOnlyList<string> Save(string path, LaseroProjectFile project, bool keepBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Projekt nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);
        RemoveStaleTemporaryFiles(directory, Path.GetFileName(fullPath));

        var warnings = new List<string>();
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var persistedProject = CreateArchiveSnapshot(project, archive, warnings);
                    var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                    using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                    writer.Write(Serialize(persistedProject));
                }

                stream.Flush(flushToDisk: true);
            }

            if (keepBackup && File.Exists(fullPath) && IsReadableProject(fullPath))
                File.Copy(fullPath, fullPath + ".bak", overwrite: true);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return warnings;
    }

    /// <summary>True when the file is a project container this version can open, so a damaged file
    /// never replaces a good backup.</summary>
    private static bool IsReadableProject(string fullPath)
    {
        try
        {
            if (LooksLikeJson(fullPath)) return true;
            using var archive = ZipFile.OpenRead(fullPath);
            return archive.GetEntry(ManifestEntryName) is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Existing project file is not a readable backup candidate: {ex.Message}");
            return false;
        }
    }

    private static void RemoveStaleTemporaryFiles(string directory, string projectFileName)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, $".{projectFileName}.*.tmp"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) < StaleTemporaryFileAge) continue;
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not remove stale temporary project file '{stale}': {ex.Message}");
            }
        }
    }

    public static LaseroProjectFile Load(string path, string? assetCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        if (LooksLikeJson(fullPath))
        {
            var legacy = Deserialize(File.ReadAllText(fullPath));
            NoteMissingRasterFiles(legacy);
            return legacy;
        }

        using var archive = ZipFile.OpenRead(fullPath);
        var manifest = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Balíček neobsahuje project.json.");
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var project = Deserialize(reader.ReadToEnd());

        var cacheDirectory = assetCacheDirectory ?? GetDefaultAssetCacheDirectory(fullPath);
        ExtractRasterAssets(project, archive, cacheDirectory);
        NoteMissingRasterFiles(project);
        if (assetCacheDirectory is null)
            PruneAssetCache(cacheDirectory, fullPath);
        return project;
    }

    /// <summary>Parses and validates a project. Objects whose geometry contains NaN/Infinity are dropped
    /// and reported in <see cref="LaseroProjectFile.LoadWarnings"/> instead of poisoning toolpaths or
    /// rejecting the whole file; layers with non-finite settings are reset to the mode defaults.</summary>
    public static LaseroProjectFile Deserialize(string json)
    {
        var project = DeserializeRaw(json);
        HealNonFiniteSettings(project);
        DropNonFiniteObjects(project);
        return project;
    }

    private static LaseroProjectFile DeserializeRaw(string json)
    {
        var project = JsonSerializer.Deserialize<LaseroProjectFile>(json, Options)
            ?? throw new InvalidDataException("Projekt je prázdný nebo poškozený.");
        if (project.Version > CurrentVersion)
            throw new InvalidDataException("Projekt byl vytvořen novější verzí aplikace LASERO.");
        MigrateLayerIds(project);
        project.Version = CurrentVersion;
        return project;
    }

    private static void HealNonFiniteSettings(LaseroProjectFile project)
    {
        foreach (var layer in project.Layers.Where(layer => layer is not null))
        {
            if (double.IsFinite(layer.Speed) && double.IsFinite(layer.Power) && double.IsFinite(layer.FillLineIntervalMm))
                continue;
            var cut = layer.Mode == LayerMode.Cut;
            if (!double.IsFinite(layer.Speed)) layer.Speed = cut ? 350 : 3000;
            if (!double.IsFinite(layer.Power)) layer.Power = cut ? 95 : 30;
            if (!double.IsFinite(layer.FillLineIntervalMm)) layer.FillLineIntervalMm = 25.4 / 254;
            project.LoadWarnings.Add($"Vrstva „{layer.Name}“ měla neplatnou rychlost, výkon nebo rozestup čar; hodnoty byly vráceny na výchozí.");
        }
    }

    private static void DropNonFiniteObjects(LaseroProjectFile project)
    {
        for (var index = project.Objects.Count - 1; index >= 0; index--)
        {
            var item = project.Objects[index];
            if (item is null || HasOnlyFiniteGeometry(item)) continue;
            project.LoadWarnings.Add($"Objekt „{item.Name}“ obsahuje neplatné souřadnice (NaN nebo nekonečno) a nebyl načten.");
            project.Objects.RemoveAt(index);
        }
    }

    private static bool HasOnlyFiniteGeometry(ProjectObject item) =>
        IsFinite(item.LocalPivot) && IsFinite(item.LocalBounds) && IsFinite(item.Transform)
        && (item.Shapes ?? []).All(shape => shape is null || (shape.Points ?? []).All(IsFinite))
        && IsFinite(item.VectorPath);

    private static bool IsFinite(Position p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static bool IsFinite(BoundingBox2D b) =>
        double.IsFinite(b.MinX) && double.IsFinite(b.MinY) && double.IsFinite(b.MaxX) && double.IsFinite(b.MaxY);

    private static bool IsFinite(ObjectTransform t) =>
        double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.RotationDeg)
        && double.IsFinite(t.ScaleX) && double.IsFinite(t.ScaleY);

    private static bool IsFinite(VectorPath? path) =>
        path is null || (path.Subpaths ?? []).All(subpath => subpath is null || (subpath.Nodes ?? []).All(node =>
            IsFinite(node.Anchor)
            && (node.HandleIn is not { } handleIn || IsFinite(handleIn))
            && (node.HandleOut is not { } handleOut || IsFinite(handleOut))));

    private static LaseroProjectFile CreateArchiveSnapshot(LaseroProjectFile source, ZipArchive archive, List<string> warnings)
    {
        // Raw copy: a poisoned coordinate is written as it is, never silently dropped from the file.
        var snapshot = DeserializeRaw(Serialize(source));
        snapshot.Version = CurrentVersion;

        for (var index = 0; index < snapshot.Objects.Count; index++)
        {
            var item = snapshot.Objects[index];
            var entry = ArchiveRasterFile(archive, item.Name, item.RasterFilePath, $"assets/raster-{index:D4}", warnings);
            item.RasterAssetEntry = entry;
            // A file that could not be embedded keeps its path, so it is found again if it reappears.
            if (entry is not null) item.RasterFilePath = null;

            var originalEntry = ArchiveRasterFile(archive, item.Name, item.OriginalRasterFilePath, $"assets/raster-original-{index:D4}", warnings);
            item.OriginalRasterAssetEntry = originalEntry;
            if (originalEntry is not null) item.OriginalRasterFilePath = null;
        }

        return snapshot;
    }

    /// <summary>Packages one raster file (the current RasterFilePath, or the preserved
    /// OriginalRasterFilePath) into the archive under entryPrefix + its extension. Returns
    /// null (and archives nothing) when path is absent, which is the normal case for
    /// OriginalRasterFilePath on objects that have never had their background removed, and when the
    /// file no longer exists — that is reported through <paramref name="warnings"/>, never by failing the save.</summary>
    private static string? ArchiveRasterFile(ZipArchive archive, string objectName, string? path, string entryPrefix, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            warnings.Add($"Zdrojový obrázek objektu „{objectName}“ nebyl nalezen, a proto není součástí projektu.");
            return null;
        }

        var entryName = $"{entryPrefix}{DetermineAssetExtension(fullPath)}";
        var assetEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = assetEntry.Open())
            input.CopyTo(output);

        return entryName;
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
            item.RasterFilePath = ExtractRasterAsset(archive, cacheDirectory, item.RasterAssetEntry, item.Name, project.LoadWarnings) ?? item.RasterFilePath;
            item.OriginalRasterFilePath = ExtractRasterAsset(archive, cacheDirectory, item.OriginalRasterAssetEntry, item.Name, project.LoadWarnings) ?? item.OriginalRasterFilePath;
        }
    }

    /// <summary>Raster images the project refers to but that are neither in the package nor on disk.
    /// The project still opens; the operator is told which objects have lost their picture.</summary>
    private static void NoteMissingRasterFiles(LaseroProjectFile project)
    {
        foreach (var item in project.Objects.Where(item => item is not null))
        {
            if (!string.IsNullOrWhiteSpace(item.RasterFilePath) && !File.Exists(item.RasterFilePath))
                project.LoadWarnings.Add($"Obrázek objektu „{item.Name}“ chybí na disku: {item.RasterFilePath}");
        }
    }

    private static string? ExtractRasterAsset(ZipArchive archive, string cacheDirectory, string? assetEntryName, string objectName, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(assetEntryName))
            return null;

        ValidateAssetEntryName(assetEntryName);
        var entry = archive.GetEntry(assetEntryName);
        if (entry is null)
        {
            warnings.Add($"Balíček projektu neobsahuje obrázek objektu „{objectName}“ ({assetEntryName}).");
            return null;
        }

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

        return destination;
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

    /// <summary>Every save of a project changes its cache key (a loaded image must never be overwritten
    /// under a running scene), so earlier folders pile up. The folder is stamped with its project and
    /// the previous folders of the same project, and any folder untouched for a month, are deleted.
    /// A folder still held open by a running scene is simply retried at the next load.</summary>
    private static void PruneAssetCache(string currentDirectory, string projectFullPath)
    {
        if (!Directory.Exists(currentDirectory)) return;
        var current = Path.GetFullPath(currentDirectory);
        var root = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
        if (root is null || !Directory.Exists(root)) return;

        try
        {
            File.WriteAllText(Path.Combine(current, AssetCacheMarkerName), projectFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not stamp the asset cache folder '{current}': {ex.Message}");
        }

        foreach (var sibling in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFullPath(sibling).TrimEnd(Path.DirectorySeparatorChar), current.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var marker = Path.Combine(sibling, AssetCacheMarkerName);
                var sameProject = File.Exists(marker)
                    && string.Equals(File.ReadAllText(marker).Trim(), projectFullPath, StringComparison.OrdinalIgnoreCase);
                var stale = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(sibling) > StaleAssetCacheAge;
                if (sameProject || stale)
                    Directory.Delete(sibling, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not prune asset cache folder '{sibling}': {ex.Message}");
            }
        }
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

    /// <summary>The entry keeps the file's own extension when it is a plain short one; otherwise the type
    /// is recognised from the first bytes, and only an unrecognisable file falls back to ".bin".</summary>
    private static string DetermineAssetExtension(string fullPath)
    {
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (SafeExtension.IsMatch(extension)) return extension;

        var header = new byte[12];
        int read;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = stream.Read(header, 0, header.Length);

        static bool Starts(byte[] data, int length, params byte[] prefix) =>
            length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

        if (Starts(header, read, 0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Starts(header, read, 0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Starts(header, read, 0x42, 0x4D)) return ".bmp";
        if (Starts(header, read, 0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (Starts(header, read, 0x52, 0x49, 0x46, 0x46) && read >= 12
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return ".webp";
        return ".bin";
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
