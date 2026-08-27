using System.IO;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-session-tests", Guid.NewGuid().ToString("N"));
    private string SessionPath => Path.Combine(_directory, "session.dat");

    public SessionStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SessionRoundTripsAndCanBeReplaced()
    {
        var store = new SessionStore(SessionPath);
        store.SaveRefreshToken("first-token", "user-1", "test@example.com");
        store.SaveRefreshToken("second-token", "user-1", "test@example.com");

        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal("second-token", loaded.RefreshToken);
        Assert.Equal("user-1", loaded.LocalId);
        Assert.NotEqual("second-token", File.ReadAllText(SessionPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void CorruptSessionIsQuarantined()
    {
        File.WriteAllText(SessionPath, "not-dpapi-data");
        var store = new SessionStore(SessionPath);

        Assert.Null(store.TryLoad());
        Assert.False(File.Exists(SessionPath));
        Assert.Single(Directory.GetFiles(_directory, "session.dat.corrupt-*"));
    }

    [Fact]
    public void ClearRemovesStoredSession()
    {
        var store = new SessionStore(SessionPath);
        store.SaveRefreshToken("token", "user", null);

        store.Clear();

        Assert.Null(store.TryLoad());
        Assert.False(File.Exists(SessionPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
