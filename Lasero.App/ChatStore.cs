using System.IO;
using System.Text.Json;
using Lasero.Core.LaseroApi;
using Serilog;

namespace Lasero.App;

/// <summary>
/// Account-scoped, atomic local persistence for Lasero Chat conversations.
///
/// File format (version 2): <c>{ "Version": 2, "ActiveSessionId": guid|null, "Sessions": [...] }</c>.
/// Version 1 was a bare array of sessions and is still read. Only conversation text is stored — never
/// the Firebase token, API keys or anything else the chat client needs at send time.
///
/// A file that cannot be read is never silently deleted: it is copied next to itself with a
/// <c>.bad</c> suffix and the store starts empty, so a bug here can cost the operator a session but
/// never the evidence needed to recover it.
/// </summary>
public sealed class ChatStore
{
    public const int CurrentVersion = 2;
    public const int MaxSessions = 50;
    public const int MaxMessagesPerSession = 200;

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

    public IReadOnlyList<StoredChatSession> Load(string userId) => LoadState(userId).Sessions;

    public StoredChatState LoadState(string userId)
    {
        var path = PathFor(userId);
        if (!File.Exists(path)) return StoredChatState.Empty;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Chat history could not be read");
            return StoredChatState.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement sessionsElement;
            Guid? activeSessionId = null;

            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    sessionsElement = root; // version 1
                    break;
                case JsonValueKind.Object:
                    var version = root.TryGetProperty("Version", out var versionElement)
                        && versionElement.TryGetInt32(out var parsedVersion) ? parsedVersion : 0;
                    if (version is < 2 or > CurrentVersion || !root.TryGetProperty("Sessions", out sessionsElement)
                        || sessionsElement.ValueKind != JsonValueKind.Array)
                    {
                        KeepBadCopy(path, $"unsupported chat file version {version}");
                        return StoredChatState.Empty;
                    }

                    if (root.TryGetProperty("ActiveSessionId", out var activeElement)
                        && activeElement.ValueKind == JsonValueKind.String
                        && activeElement.TryGetGuid(out var activeId))
                        activeSessionId = activeId;
                    break;
                default:
                    KeepBadCopy(path, "chat file root is neither array nor object");
                    return StoredChatState.Empty;
            }

            var sessions = new List<StoredChatSession>();
            var skipped = 0;
            foreach (var element in sessionsElement.EnumerateArray())
            {
                var session = TryReadSession(element);
                if (session is null) skipped++;
                else sessions.Add(session);
            }

            if (skipped > 0) KeepBadCopy(path, $"{skipped} unreadable conversation(s) skipped");

            var pruned = Prune(sessions);
            if (activeSessionId is { } id && pruned.All(session => session.Id != id)) activeSessionId = null;
            return new StoredChatState(CurrentVersion, activeSessionId, pruned);
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "Chat history is not valid JSON");
            KeepBadCopy(path, "invalid JSON");
            return StoredChatState.Empty;
        }
    }

    public void Save(string userId, IReadOnlyCollection<StoredChatSession> sessions, Guid? activeSessionId = null)
    {
        var pruned = Prune(sessions);
        if (activeSessionId is { } id && pruned.All(session => session.Id != id)) activeSessionId = null;
        var state = new StoredChatState(CurrentVersion, activeSessionId, pruned);

        Directory.CreateDirectory(_directory);
        var path = PathFor(userId);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Newest conversations first, at most <see cref="MaxSessions"/>; each keeps only its
    /// newest <see cref="MaxMessagesPerSession"/> messages. Oldest are dropped.</summary>
    public static IReadOnlyList<StoredChatSession> Prune(IEnumerable<StoredChatSession> sessions) => sessions
        .OrderByDescending(session => session.UpdatedAt)
        .Take(MaxSessions)
        .Select(session => session.Messages.Count > MaxMessagesPerSession
            ? session with { Messages = session.Messages.Skip(session.Messages.Count - MaxMessagesPerSession).ToArray() }
            : session)
        .ToArray();

    private static StoredChatSession? TryReadSession(JsonElement element)
    {
        try
        {
            var session = element.Deserialize<StoredChatSession>(JsonOptions);
            if (session is null || session.Id == Guid.Empty) return null;

            var messages = (session.Messages ?? [])
                .Where(message => message is not null && message.Id != Guid.Empty && !string.IsNullOrEmpty(message.Text))
                .ToArray();
            var title = string.IsNullOrWhiteSpace(session.Title)
                ? messages.FirstOrDefault(message => message.Role == LaseroChatRole.User)?.Text ?? "Konverzace"
                : session.Title;
            return session with { Title = title, Messages = messages };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void KeepBadCopy(string path, string reason)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            Log.Warning("Chat history: {Reason}; a .bad copy was kept", reason);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Chat history .bad copy could not be written");
        }
    }

    private string PathFor(string userId) => Path.Combine(_directory, AccountScopedStorage.FileNameFor(userId));
}

public sealed record StoredChatState(int Version, Guid? ActiveSessionId, IReadOnlyList<StoredChatSession> Sessions)
{
    public static StoredChatState Empty { get; } = new(ChatStore.CurrentVersion, null, []);
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
