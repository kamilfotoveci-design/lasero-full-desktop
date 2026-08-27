using System.IO;
using Lasero.App;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class ChatStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-chat-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HistoryIsSeparatedByAccountAndRoundTripsUnicode()
    {
        var store = new ChatStore(_directory);
        var session = new StoredChatSession(
            Guid.NewGuid(),
            "Překližka a výkon",
            DateTimeOffset.UtcNow,
            [new StoredChatMessage(Guid.NewGuid(), LaseroChatRole.User, "Jak gravírovat kůži?", DateTimeOffset.UtcNow)]);

        store.Save("account-a", [session]);

        var loaded = Assert.Single(store.Load("account-a"));
        Assert.Equal("Překližka a výkon", loaded.Title);
        Assert.Equal("Jak gravírovat kůži?", Assert.Single(loaded.Messages).Text);
        Assert.Empty(store.Load("account-b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
