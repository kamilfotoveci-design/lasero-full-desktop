using System.IO.Compression;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Lasero.Avalonia.Contracts;

namespace Lasero.Avalonia.Services;

public sealed class ProjectPackageInspector : IProjectInspector
{
    private const string ManifestEntryName = "project.json";

    public Result<ProjectSummary> Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<ProjectSummary>("Nebyla zadána cesta k projektu.");

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return Result.Failure<ProjectSummary>("Vybraný projekt už na tomto místě není.");

            using var stream = File.OpenRead(fullPath);
            var isPackaged = IsZip(stream);
            stream.Position = 0;

            using var document = isPackaged
                ? ReadPackaged(stream)
                : JsonDocument.Parse(stream);

            var root = document.RootElement;
            var name = ReadString(root, "Name") ?? Path.GetFileNameWithoutExtension(fullPath);
            return Result.Success(new ProjectSummary(
                name,
                fullPath,
                ReadInt(root, "Version"),
                ReadArrayCount(root, "Objects"),
                ReadArrayCount(root, "Layers"),
                isPackaged));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException)
        {
            return Result.Failure<ProjectSummary>($"Projekt se nepodařilo přečíst: {exception.Message}");
        }
    }

    private static bool IsZip(Stream stream)
    {
        Span<byte> signature = stackalloc byte[2];
        return stream.Read(signature) == signature.Length && signature[0] == (byte)'P' && signature[1] == (byte)'K';
    }

    private static JsonDocument ReadPackaged(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Balíček neobsahuje project.json.");
        using var manifestStream = manifest.Open();
        return JsonDocument.Parse(manifestStream);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static int ReadArrayCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
}
