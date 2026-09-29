using System.IO;
using System.Text.Json;
using Serilog;

namespace Lasero.App;

/// <summary>
/// Remembers that the operator has read the one-time notice that "Odstranit pozadí" sends the
/// selected image to a remote service. Kept in its own small file (next to settings.json) so the
/// answer survives restarts without widening the shared settings schema. A missing or corrupt file
/// simply means the notice is shown again, which is the safe direction.
/// </summary>
public sealed class BackgroundRemovalConsentStore
{
    public const int CurrentNoticeVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private bool? _accepted;

    public BackgroundRemovalConsentStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static BackgroundRemovalConsentStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "background-removal-consent.json"));

    public bool HasConsent => _accepted ??= Read();

    public void RecordConsent()
    {
        _accepted = true;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                    new ConsentFile { NoticeVersion = CurrentNoticeVersion, AcceptedUtc = DateTime.UtcNow }, JsonOptions));
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not persisting only means the notice is shown once more next session.
            Log.Warning(ex, "Background-removal consent could not be saved");
        }
    }

    private bool Read()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            var file = JsonSerializer.Deserialize<ConsentFile>(File.ReadAllText(_path), JsonOptions);
            return file is { NoticeVersion: >= CurrentNoticeVersion };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class ConsentFile
    {
        public int NoticeVersion { get; set; }
        public DateTime AcceptedUtc { get; set; }
    }
}
