using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lasero.Core.LaseroApi;

/// <summary>
/// Calls lasero.net's existing Netlify functions — same entitlement/licensing
/// backend the web app already uses. Nothing here is desktop-specific; this
/// deliberately reuses the exact contract check-premium.js / redeem-license.js
/// already expose, so no new backend work is needed for sign-in + license
/// entitlement (unlike saved-project import, which does need a new endpoint —
/// see README).
/// </summary>
public sealed class LaseroAccountClient
{
    private const string BaseUrl = "https://lasero.net";
    private readonly HttpClient _http;

    public LaseroAccountClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PremiumStatus> CheckPremiumAsync(string uid, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{BaseUrl}/.netlify/functions/check-premium?uid={Uri.EscapeDataString(uid)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PremiumStatus>(cancellationToken: ct)
            ?? new PremiumStatus(false, 0, null, null, null);
    }

    public async Task<LicenseRedemptionResult> RedeemLicenseAsync(string idToken, string licenseCode, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/.netlify/functions/redeem-license")
        {
            Content = JsonContent.Create(new { code = licenseCode }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        var response = await _http.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<LicenseRedemptionResult>(cancellationToken: ct);
        return result ?? new LicenseRedemptionResult(false, null, null, null, "Neznámá chyba serveru.");
    }
}

public sealed record LicenseRedemptionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("expiresAt")] long? ExpiresAt,
    [property: JsonPropertyName("daysLeft")] int? DaysLeft,
    [property: JsonPropertyName("error")] string? Error);
