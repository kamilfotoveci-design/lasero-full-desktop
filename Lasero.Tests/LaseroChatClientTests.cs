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

    [Fact]
    public async Task SendsBoundedWorkspaceFactsAsDataAndRepliesInTheUsersLanguage()
    {
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"Jasné."}]}}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var workspace = new KamilWorkspaceSnapshot(
            2, DateTimeOffset.UtcNow, "Designer", "mm", null, null,
            1, 1, 0,
            [new KamilSelectedObject(Guid.NewGuid(), "raster", new KamilBounds(1, 2, 30, 20), false, true, true, [], null)],
            0, 0, [], new KamilMachine("disconnected", null, null, null, null, null),
            null, null, null);
        var context = Context() with { Workspace = workspace };
        var client = new LaseroChatClient(new HttpClient(handler));

        await client.SendAsync("token", [new LaseroChatTurn(LaseroChatRole.User, "Ako pripravím toto logo na rezanie?")], context);

        using var json = JsonDocument.Parse(body!);
        var requestBody = json.RootElement.GetProperty("body");
        Assert.Contains("Reply in Slovak", requestBody.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var parts = requestBody.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Contains("\"selectedCount\":1", parts[0].GetProperty("text").GetString());
        Assert.Equal("Ako pripravím toto logo na rezanie?", parts[1].GetProperty("text").GetString());
        var ctx = json.RootElement.GetProperty("ctx");
        Assert.Equal("sk", ctx.GetProperty("lang").GetString());
        Assert.True(ctx.GetProperty("power").ValueKind == JsonValueKind.Null);
        Assert.Equal(1, ctx.GetProperty("workspace").GetProperty("selectedCount").GetInt32());
    }

    private static LaseroChatContext Context() => new(null, null, null, null, null, null, null, null);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
