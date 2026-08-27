using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class LaseroChatClientTests
{
    [Fact]
    public async Task SendsBearerTokenBoundedHistoryAndReturnsAssistantText()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"Bezpečná odpověď"}]}}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var client = new LaseroChatClient(new HttpClient(handler));
        var turns = Enumerable.Range(0, 30)
            .Select(index => new LaseroChatTurn(index % 2 == 0 ? LaseroChatRole.User : LaseroChatRole.Assistant, $"zpráva {index}"))
            .ToArray();

        var result = await client.SendAsync("token-123", turns, Context());

        Assert.Equal("Bezpečná odpověď", result);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("token-123", captured.Headers.Authorization.Parameter);
        using var json = JsonDocument.Parse(body!);
        var sentTurns = json.RootElement.GetProperty("body").GetProperty("contents");
        Assert.Equal(24, sentTurns.GetArrayLength());
        Assert.Equal("zpráva 6", sentTurns[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnauthorizedResponseHasRecoverableAuthenticationFailure()
    {
        var client = new LaseroChatClient(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))));

        var error = await Assert.ThrowsAsync<LaseroChatException>(() => client.SendAsync(
            "expired", [new LaseroChatTurn(LaseroChatRole.User, "Ahoj")], Context()));

        Assert.Equal(LaseroChatFailure.Authentication, error.Failure);
    }

    private static LaseroChatContext Context() => new(null, null, 20, "diode", "diodový laser", "beginner", null, "engrave");

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
