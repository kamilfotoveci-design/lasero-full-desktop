using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class DeviceActivationClientTests
{
    [Fact]
    public async Task RegisterAsync_SendsBearerTokenAndDeviceIdPlatformAppVersion()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DeviceActivationClient(new HttpClient(handler));

        await client.RegisterAsync("test-id-token", "11111111-1111-1111-1111-111111111111", "desktop");

        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("test-id-token", captured.Headers.Authorization?.Parameter);
        Assert.Equal("https://lasero.net/.netlify/functions/register-device", captured.RequestUri!.ToString());

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("11111111-1111-1111-1111-111111111111", root.GetProperty("deviceId").GetString());
        Assert.Equal("desktop", root.GetProperty("platform").GetString());
        Assert.True(root.TryGetProperty("appVersion", out _));
    }

    [Fact]
    public async Task RegisterAsync_ThrowsOnServerError()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new DeviceActivationClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync("test-id-token", "11111111-1111-1111-1111-111111111111", "desktop"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request);
    }
}
