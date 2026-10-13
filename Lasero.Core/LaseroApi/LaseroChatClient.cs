using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lasero.Core.LaseroApi;

/// <summary>
/// Sends a complete, bounded conversation to Lasero's authenticated Kamil endpoint.
/// Token refresh, local history and UI state intentionally stay outside this module.
/// </summary>
public sealed class LaseroChatClient
{
    private const string ChatUrl = "https://lasero.net/.netlify/functions/gemini";
    private const string Model = "gemini-3.1-flash-lite";
    private static readonly JsonSerializerOptions WorkspaceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;

    public LaseroChatClient(HttpClient http) => _http = http;

    /// <summary>
    /// Project, layer and material names come from the user's own file, so they are text an attacker
    /// can author. They travel only as JSON string values (the encoder escapes quotes, newlines and
    /// angle brackets) inside a fence the system policy names as untrusted data; a literal fence tag
    /// is stripped as a second line of defence should the encoder ever change.
    /// </summary>
    private static string FenceWorkspace(KamilWorkspaceSnapshot workspace)
    {
        var json = JsonSerializer.Serialize(workspace, WorkspaceJsonOptions)
            .Replace(KamilResponsePolicy.WorkspaceFenceOpen, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(KamilResponsePolicy.WorkspaceFenceClose, string.Empty, StringComparison.OrdinalIgnoreCase);
        return KamilResponsePolicy.WorkspaceFenceOpen + "\n" + json + "\n" + KamilResponsePolicy.WorkspaceFenceClose;
    }

    public async Task<string> SendAsync(
        string idToken,
        IReadOnlyList<LaseroChatTurn> conversation,
        LaseroChatContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(context);

        var recentTurns = conversation
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
            .TakeLast(24)
            .ToArray();

        if (recentTurns.Length == 0)
            throw new ArgumentException("Konverzace neobsahuje žádnou zprávu.", nameof(conversation));

        var latestUserText = recentTurns.LastOrDefault(turn => turn.Role == LaseroChatRole.User)?.Text ?? string.Empty;
        var language = KamilResponsePolicy.ResolveLanguage(latestUserText, context.PreferredLanguage);
        // The legacy ctx field may be consumed by the Netlify proxy. The second, bounded copy below
        // ensures the model also receives the facts when the proxy simply forwards body unchanged.
        // It is data in a user turn, below the system policy, and never executable instructions.
        var workspaceData = context.Workspace is null ? null : FenceWorkspace(context.Workspace);
        var contents = recentTurns
            .Select((turn, index) => new
            {
                role = turn.Role == LaseroChatRole.Assistant ? "model" : "user",
                parts = index == recentTurns.Length - 1 && turn.Role == LaseroChatRole.User && workspaceData is not null
                    ? new[] { new { text = workspaceData }, new { text = turn.Text.Trim() } }
                    : new[] { new { text = turn.Text.Trim() } },
            })
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                body = new
                {
                    contents,
                    systemInstruction = new
                    {
                        parts = new[] { new { text = KamilResponsePolicy.BuildInstruction(language) } },
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 500,
                        temperature = 0.35,
                    },
                },
                ctx = new
                {
                    machineName = context.MachineName ?? string.Empty,
                    machine = context.MachineId ?? string.Empty,
                    power = context.PowerWatts,
                    type = context.LaserType,
                    laserDesc = context.LaserDescription,
                    experience = context.Experience,
                    software = "Lasero Desktop",
                    matName = context.MaterialName ?? string.Empty,
                    matDesc = string.Empty,
                    lang = language,
                    activeOp = context.Operation,
                    chatTurnCount = contents.Length,
                    workspace = context.Workspace,
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LaseroChatException(LaseroChatFailure.Authentication, "Platnost přihlášení skončila. Je potřeba se přihlásit znovu.");
        if ((int)response.StatusCode == 429)
            throw new LaseroChatException(LaseroChatFailure.RateLimited, "Kamil je nyní vytížený. Zprávu lze odeslat za chvíli.");

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LaseroChatException(LaseroChatFailure.Server, "Lasero Chat nyní neodpovídá. Lze to zkusit znovu.");

        try
        {
            using var document = JsonDocument.Parse(payload);
            var text = document.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : throw new LaseroChatException(LaseroChatFailure.InvalidResponse, "Kamil vrátil prázdnou odpověď. Otázku lze položit znovu.");
        }
        catch (LaseroChatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LaseroChatException(LaseroChatFailure.InvalidResponse, "Odpověď Lasero Chatu se nepodařilo přečíst.", exception);
        }
    }
}

public enum LaseroChatRole
{
    User,
    Assistant,
}

public sealed record LaseroChatTurn(LaseroChatRole Role, string Text);

public sealed record LaseroChatContext(
    string? MachineName,
    string? MachineId,
    int? PowerWatts,
    string? LaserType,
    string? LaserDescription,
    string? Experience,
    string? MaterialName,
    string? Operation,
    KamilWorkspaceSnapshot? Workspace = null,
    string? PreferredLanguage = null);

public enum LaseroChatFailure
{
    Authentication,
    RateLimited,
    Server,
    InvalidResponse,
}

public sealed class LaseroChatException : Exception
{
    public LaseroChatException(LaseroChatFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public LaseroChatFailure Failure { get; }
}
