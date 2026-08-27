using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lasero.Core.Layers;

namespace Lasero.Core.LaseroApi;

public sealed class MaterialSyncClient
{
    private const string Endpoint = "https://lasero.net/.netlify/functions/sync-materials";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly HttpClient _http;

    public MaterialSyncClient(HttpClient http) => _http = http;

    public async Task<MaterialSyncSnapshot> GetAsync(string idToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, idToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MaterialSyncSnapshot>(JsonOptions, cancellationToken)
            ?? MaterialSyncSnapshot.Empty;
    }

    public async Task<MaterialSyncSnapshot> PutAsync(string idToken, string? baseRevision,
        IReadOnlyCollection<MaterialSyncItem> items, string clientId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, idToken);
        request.Content = JsonContent.Create(new MaterialSyncWriteRequest(1, baseRevision, clientId, items), options: JsonOptions);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<MaterialSyncConflict>(JsonOptions, cancellationToken);
            throw new MaterialSyncConflictException(conflict?.Snapshot ?? MaterialSyncSnapshot.Empty);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MaterialSyncSnapshot>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Server nevrátil synchronizovaný vzorník.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string idToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        var request = new HttpRequestMessage(method, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        return request;
    }
}

public sealed record MaterialSyncSnapshot(int SchemaVersion, string? Revision, DateTimeOffset? UpdatedAt, IReadOnlyList<MaterialSyncItem> Items)
{
    public static MaterialSyncSnapshot Empty { get; } = new(1, null, null, []);
}

public sealed record MaterialSyncItem(Guid Id, string Name, LayerMode Mode, double Speed, double Power, int Passes, double FillLineIntervalMm);
public sealed record MaterialSyncWriteRequest(int SchemaVersion, string? BaseRevision, string ClientId, IReadOnlyCollection<MaterialSyncItem> Items);
public sealed record MaterialSyncConflict(bool Conflict, MaterialSyncSnapshot Snapshot, string? Error);
public sealed class MaterialSyncConflictException(MaterialSyncSnapshot serverSnapshot) : Exception("Vzorník byl změněn na jiném zařízení.")
{
    public MaterialSyncSnapshot ServerSnapshot { get; } = serverSnapshot;
}
