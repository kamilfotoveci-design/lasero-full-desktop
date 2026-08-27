using System.Net;
using System.Net.Http;
using System.Text;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class MaterialSyncClientTests
{
    [Fact]
    public async Task Get_SendsBearerTokenAndReadsSnapshot()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, """{"schemaVersion":1,"revision":"r1","updatedAt":"2026-08-11T10:00:00Z","items":[]}""");
        });
        var client = new MaterialSyncClient(new HttpClient(handler));

        var result = await client.GetAsync("test-token");

        Assert.Equal("r1", result.Revision);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Put_MapsConflictToTypedExceptionWithServerSnapshot()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict,
            """{"conflict":true,"error":"conflict","snapshot":{"schemaVersion":1,"revision":"server-r2","updatedAt":null,"items":[]}}"""));
        var client = new MaterialSyncClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<MaterialSyncConflictException>(() =>
            client.PutAsync("test-token", "old-r1", [], "desktop"));

        Assert.Equal("server-r2", exception.ServerSnapshot.Revision);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
