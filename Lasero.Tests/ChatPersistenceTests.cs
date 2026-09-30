using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

/// <summary>KAMIL conversations survive closing the panel and restarting the app: versioned file,
/// active conversation, caps, and a damaged file that is kept as .bad instead of being lost.</summary>
public sealed class ChatPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-chat-persist", Guid.NewGuid().ToString("N"));

    private static StoredChatSession Session(string title, DateTimeOffset updated, int messages = 1) => new(
        Guid.NewGuid(), title, updated,
        Enumerable.Range(0, messages)
            .Select(i => new StoredChatMessage(Guid.NewGuid(), i % 2 == 0 ? LaseroChatRole.User : LaseroChatRole.Assistant, $"zpráva {i}", updated))
            .ToArray());

    private string ChatFile(string directory) => Directory.GetFiles(directory)
        .Single(f => !f.EndsWith(".bad", StringComparison.Ordinal) && !f.Contains(".tmp-", StringComparison.Ordinal));

    [Fact]
    public void ActiveConversationAndVersionRoundTrip()
    {
        var store = new ChatStore(_directory);
        var older = Session("Starší", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = Session("Novější", DateTimeOffset.UtcNow);

        store.Save("u", [newer, older], older.Id);
        var state = store.LoadState("u");

        Assert.Equal(ChatStore.CurrentVersion, state.Version);
        Assert.Equal(older.Id, state.ActiveSessionId);
        Assert.Equal(new[] { "Novější", "Starší" }, state.Sessions.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void LegacyVersionOneArrayIsStillRead()
    {
        var store = new ChatStore(_directory);
        store.Save("u", [Session("Nová", DateTimeOffset.UtcNow)]);
        var path = ChatFile(_directory);
        var sessions = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)).GetProperty("Sessions");
        File.WriteAllText(path, sessions.GetRawText());

        var state = store.LoadState("u");

        Assert.Equal("Nová", Assert.Single(state.Sessions).Title);
        Assert.Null(state.ActiveSessionId);
    }

    [Fact]
    public void CorruptFileIsKeptAsBadAndTheStoreStartsEmpty()
    {
        var store = new ChatStore(_directory);
        store.Save("u", [Session("A", DateTimeOffset.UtcNow)]);
        var path = ChatFile(_directory);
        File.WriteAllText(path, "{ not json");

        Assert.Empty(store.Load("u"));

        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        store.Save("u", [Session("B", DateTimeOffset.UtcNow)]);
        Assert.Equal("B", Assert.Single(store.Load("u")).Title);
    }

    [Fact]
    public void OneUnreadableConversationIsSkippedAndTheRestSurvive()
    {
        var store = new ChatStore(_directory);
        store.Save("u", [Session("Dobrá", DateTimeOffset.UtcNow)]);
        var path = ChatFile(_directory);
        var json = File.ReadAllText(path).Replace("\"Sessions\": [", "\"Sessions\": [ { \"Id\": \"neni guid\" },");
        File.WriteAllText(path, json);

        var loaded = store.Load("u");

        Assert.Equal("Dobrá", Assert.Single(loaded).Title);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void FutureVersionIsKeptAsBadNotSilentlyDropped()
    {
        var store = new ChatStore(_directory);
        store.Save("u", [Session("A", DateTimeOffset.UtcNow)]);
        var path = ChatFile(_directory);
        File.WriteAllText(path, "{\"Version\": 99, \"Sessions\": []}");

        Assert.Empty(store.Load("u"));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void OldestConversationsAndMessagesArePruned()
    {
        var store = new ChatStore(_directory);
        var now = DateTimeOffset.UtcNow;
        var sessions = Enumerable.Range(0, ChatStore.MaxSessions + 5)
            .Select(i => Session($"K{i}", now.AddMinutes(-i), messages: i == 0 ? ChatStore.MaxMessagesPerSession + 30 : 1))
            .ToArray();

        store.Save("u", sessions);
        var loaded = store.Load("u");

        Assert.Equal(ChatStore.MaxSessions, loaded.Count);
        Assert.Equal("K0", loaded[0].Title);
        Assert.DoesNotContain(loaded, s => s.Title == $"K{ChatStore.MaxSessions + 4}");
        Assert.Equal(ChatStore.MaxMessagesPerSession, loaded[0].Messages.Count);
        Assert.Equal("zpráva 30", loaded[0].Messages[0].Text);
    }

    [Fact]
    public void StoredFileHoldsConversationTextOnly()
    {
        var store = new ChatStore(_directory);
        store.Save("u", [Session("A", DateTimeOffset.UtcNow)]);
        var json = File.ReadAllText(ChatFile(_directory));

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- view model ---------------------------------------------------

    private (ChatViewModel Chat, AccountViewModel Account) CreateChat(ChatStore store)
    {
        var account = new AccountViewModel(
            new LaseroAuthClient(new HttpClient()),
            new LaseroAccountClient(new HttpClient()),
            new SessionStore(Path.Combine(_directory, $"session-{Guid.NewGuid():N}.dat")),
            new DeviceIdStore(Path.Combine(_directory, $"device-{Guid.NewGuid():N}.txt")),
            new DeviceActivationClient(new HttpClient()));
        return (new ChatViewModel(new LaseroChatClient(new HttpClient(new NoNetworkHandler())), account, store), account);
    }

    [Fact]
    public void ReopeningTheAppRestoresTheConversationTheOperatorLastHadOpen()
    {
        var store = new ChatStore(Path.Combine(_directory, "vm"));
        var older = Session("Starší", DateTimeOffset.UtcNow.AddHours(-2), messages: 2);
        var newer = Session("Novější", DateTimeOffset.UtcNow, messages: 4);
        store.Save("u", [newer, older]);

        var (first, firstAccount) = CreateChat(store);
        firstAccount.UserId = "u";
        Assert.Equal(4, first.Messages.Count);

        first.OpenChatCommand.Execute(first.Sessions.Single(s => s.Id == older.Id));
        Assert.Equal(2, first.Messages.Count);

        var (second, secondAccount) = CreateChat(store); // "restart"
        secondAccount.UserId = "u";

        Assert.Equal(2, second.Messages.Count);
        Assert.True(second.Sessions.Single(s => s.Id == older.Id).IsCurrent);
        Assert.False(second.Sessions.Single(s => s.Id == newer.Id).IsCurrent);
    }

    [Fact]
    public void DeletingNeedsConfirmationAndCancelKeepsTheConversation()
    {
        var store = new ChatStore(Path.Combine(_directory, "vm"));
        store.Save("u", [Session("Jediná", DateTimeOffset.UtcNow)]);
        var (chat, account) = CreateChat(store);
        account.UserId = "u";
        var item = chat.Sessions.Single();

        chat.RequestDeleteChatCommand.Execute(item);
        Assert.True(item.IsConfirmingDelete);
        Assert.Single(store.Load("u"));

        chat.CancelDeleteChatCommand.Execute(null);
        Assert.False(item.IsConfirmingDelete);
        Assert.Single(chat.Sessions);

        chat.DeleteChatCommand.Execute(item);
        Assert.Empty(chat.Sessions);
        Assert.Empty(chat.Messages);
        Assert.Empty(store.Load("u"));
    }

    [Fact]
    public void LongAnswerFromTheModelIsShortenedWithAnExpander()
    {
        var sentence = "Nastav výkon na šedesát procent a rychlost na tři tisíce.";
        var reply = string.Join(" ", Enumerable.Repeat(sentence, 14));
        var item = new ChatMessageItem(Guid.NewGuid(), LaseroChatRole.Assistant, reply, DateTimeOffset.Now);

        Assert.True(item.HasMore);
        Assert.True(item.DisplayText.Length < reply.Length);
        Assert.EndsWith(".", item.DisplayText);
        Assert.Equal("Zobrazit více", item.ToggleLabel);

        item.ToggleExpandedCommand.Execute(null);

        Assert.Equal(reply, item.DisplayText);
        Assert.Equal("Zobrazit méně", item.ToggleLabel);
    }

    [Fact]
    public async Task StubbedModelReplyRunsThroughTheSameGuardAsLiveOnes()
    {
        var reply = string.Join("\\n", Enumerable.Range(1, 8)
            .Select(i => $"{i}. Krok číslo {i} je jednoduchý a nevyžaduje žádné zvláštní nářadí ani dlouhé přípravy."));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + reply + "\"}]}}]}", Encoding.UTF8, "application/json"),
        });
        var client = new LaseroChatClient(new HttpClient(handler));

        var text = await client.SendAsync("t", [new LaseroChatTurn(LaseroChatRole.User, "Poraď mi")],
            new LaseroChatContext(null, null, 20, "diode", "diodový laser", "beginner", null, "engrave"));
        var item = new ChatMessageItem(Guid.NewGuid(), LaseroChatRole.Assistant, text, DateTimeOffset.Now);

        Assert.True(item.HasMore);
        Assert.Contains("Krok číslo 3", item.DisplayText);
        Assert.DoesNotContain("Krok číslo 4", item.DisplayText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The model must never be called from a persistence test.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
