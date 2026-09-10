using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Lasero.Core.LaseroApi;

/// <summary>
/// Passive device-activation telemetry only — this call never gates sign-in, entitlement, or any
/// feature. It reuses the exact authenticated-endpoint pattern sync-materials.js already proves:
/// Bearer Firebase ID token, uid derived server-side from the verified token, never trusted from the
/// request body. See docs/account-architecture-audit.md's Phase 2 section for the full design and
/// why this exists (grandfathering PRO users onto desktop with zero enforcement, but wanting real
/// usage data before any future device-cap decision).
/// </summary>
public sealed class DeviceActivationClient
{
    private const string Endpoint = "https://lasero.net/.netlify/functions/register-device";
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private readonly HttpClient _http;

    public DeviceActivationClient(HttpClient http) => _http = http;

    public async Task RegisterAsync(string idToken, string deviceId, string platform, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new DeviceActivationRequest(deviceId, platform, AppVersion)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private sealed record DeviceActivationRequest(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("appVersion")] string AppVersion);
}
