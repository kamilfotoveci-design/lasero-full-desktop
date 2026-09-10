using System.IO;
using System.Text.Json;
using Lasero.Core.LaseroApi;
using Serilog;

namespace Lasero.App;

/// <summary>Account-scoped, atomic local persistence for Lasero Chat conversations.</summary>
public sealed class ChatStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public ChatStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static ChatStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "chat"));

    public IReadOnlyList<StoredChatSession> Load(string userId)
    {
        var path = PathFor(userId);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoredChatSession>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Chat history could not be loaded");
            return [];
        }
    }

    public void Save(string userId, IReadOnlyCollection<StoredChatSession> sessions)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(userId);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(sessions, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string PathFor(string userId) => Path.Combine(_directory, AccountScopedStorage.FileNameFor(userId));
}

public sealed record StoredChatSession(
    Guid Id,
    string Title,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StoredChatMessage> Messages);

public sealed record StoredChatMessage(
    Guid Id,
    LaseroChatRole Role,
    string Text,
    DateTimeOffset CreatedAt);
