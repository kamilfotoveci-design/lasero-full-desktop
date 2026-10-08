using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lasero.Core.LaseroApi;

/// <summary>
/// Sends a complete, bounded conversation to Lasero's authenticated Kamil endpoint.
/// Token refresh, local history and UI state intentionally stay outside this module.
/// </summary>
public sealed class LaseroChatClient
{
    private const string ChatUrl = "https://lasero.net/.netlify/functions/gemini";
    private const string Model = "gemini-3.1-flash-lite";
    /// <summary>
    /// Written for a complete beginner (the operator may be a grandparent with a first laser): the
    /// answer is the recommendation, not an essay. The hard limits here are backed by
    /// <c>ChatResponseNormalizer.Condense</c> on the client, which trims a runaway answer gracefully,
    /// so the model missing the limit costs a "Zobrazit více" link and not a wall of text.
    /// Brand rules stay: Czech, no question or exclamation marks, one short line about testing on a scrap piece.
    /// </summary>
    private const string AssistantInstruction =
        "Jsi Kamil, vestavěný laserový asistent aplikace LASERO. Mluvíš s úplným začátečníkem. "
        + "Odpovídej česky, obyčejnými slovy a co nejkratší. "
        + "Pravidla: "
        + "1. Nejvýše 60 slov. "
        + "2. Nejvýše 3 krátké kroky nebo odrážky, jinak jedna až dvě věty. "
        + "3. Žádný úvod ani opakování dotazu, začni rovnou odpovědí. "
        + "4. Když se ptá na nastavení, dej jedno konkrétní doporučení s přesnými čísly, každé na vlastním řádku ve tvaru Výkon: 60 %, Rychlost: 3000 mm/min, Průchody: 2. "
        + "5. Odborné slovo vždy vysvětli dvěma slovy v závorce, například průchod (jedno přejetí). "
        + "6. Bezpečnost připomeň jen jednou krátkou větou a jen když se hodí, například Nejdřív vyzkoušet na odřezku. Varování neopakuj. "
        + "7. Nepoužívej Markdown nadpisy, tabulky, tučné písmo ani vodorovné čáry. "
        + "8. Kód a G-code vždy ponech v samostatném kódovém bloku. "
        + "9. Rozveď odpověď jen na vyžádání. "
        + "10. Nepoužívej otazníky ani vykřičníky; doplňující otázky formuluj jako výzvy, například Stačí napsat, jaký je materiál. "
        + "11. Neutrální forma: nikdy netykej ani nevykej. Nepoužívej slova ty, vy, ti, tě, tvůj, váš ani rozkazovací tvary jako nastav, zkus, napiš. "
        + "Piš věcně bez oslovení, rozkazy nahraď podstatným jménem nebo infinitivem. "
        + "Špatně: Rád ti pomohu, nastav výkon na 60 %. Správně: Doporučené nastavení: výkon 60 %.";
    private readonly HttpClient _http;

    public LaseroChatClient(HttpClient http) => _http = http;

    public async Task<string> SendAsync(
        string idToken,
        IReadOnlyList<LaseroChatTurn> conversation,
        LaseroChatContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(context);

        var contents = conversation
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
            .TakeLast(24)
            .Select(turn => new
            {
                role = turn.Role == LaseroChatRole.Assistant ? "model" : "user",
                parts = new[] { new { text = turn.Text.Trim() } },
            })
            .ToArray();

        if (contents.Length == 0)
            throw new ArgumentException("Konverzace neobsahuje žádnou zprávu.", nameof(conversation));

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
                        parts = new[] { new { text = AssistantInstruction } },
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
                    lang = "cs",
                    activeOp = context.Operation,
                    chatTurnCount = contents.Length,
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
    int PowerWatts,
    string LaserType,
    string LaserDescription,
    string Experience,
    string? MaterialName,
    string? Operation);

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
