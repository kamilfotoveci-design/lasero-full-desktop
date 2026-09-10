using System.IO;
using Lasero.App;

namespace Lasero.Tests;

public sealed class RecentProjectsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-recent-projects-tests", Guid.NewGuid().ToString("N"));

    public RecentProjectsStoreTests() => Directory.CreateDirectory(_directory);

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void FirstRun_StartsWithEmptyList()
    {
        var store = new RecentProjectsStore(PathFor("recent-projects.json"));

        Assert.Empty(store.Recent);
    }

    [Fact]
    public void Touch_PersistsAcrossNewStoreInstances()
    {
        var path = PathFor("recent-projects.json");
        var store = new RecentProjectsStore(path);
        store.Touch(@"C:\projects\a.lasero", "Projekt A", null);

        var reloaded = new RecentProjectsStore(path);

        Assert.Contains(reloaded.Recent, e => e.Name == "Projekt A");
    }

    [Fact]
    public void SwitchAccount_IsolatesTwoDistinctAccountsFromEachOther()
    {
        var store = new RecentProjectsStore(PathFor("recent-projects.json"));

        store.SwitchAccount("account-a");
        store.Touch(@"C:\projects\a.lasero", "Projekt A", null);

        store.SwitchAccount("account-b");
        Assert.Empty(store.Recent);
        store.Touch(@"C:\projects\b.lasero", "Projekt B", null);

        store.SwitchAccount("account-a");
        Assert.Single(store.Recent);
        Assert.Equal("Projekt A", store.Recent[0].Name);

        store.SwitchAccount("account-b");
        Assert.Single(store.Recent);
        Assert.Equal("Projekt B", store.Recent[0].Name);
    }

    [Fact]
    public void SwitchAccount_ReloadsFromDiskAcrossStoreInstances()
    {
        var path = PathFor("recent-projects.json");
        var first = new RecentProjectsStore(path);
        first.SwitchAccount("account-a");
        first.Touch(@"C:\projects\persist.lasero", "Perzistentní projekt", null);

        var second = new RecentProjectsStore(path);
        second.SwitchAccount("account-a");

        Assert.Contains(second.Recent, e => e.Name == "Perzistentní projekt");
    }

    [Fact]
    public void SwitchAccount_FiresChangedSoSubscribersRefresh()
    {
        var store = new RecentProjectsStore(PathFor("recent-projects.json"));
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.SwitchAccount("account-a");

        Assert.Equal(1, fireCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SwitchAccount_NullOrEmptyUserId_ClearsStateWithoutThrowing(string? userId)
    {
        var store = new RecentProjectsStore(PathFor("recent-projects.json"));
        store.SwitchAccount("account-a");
        store.Touch(@"C:\projects\a.lasero", "Projekt A", null);

        store.SwitchAccount(userId);
        Assert.Empty(store.Recent);

        // Must not throw and must not persist anywhere with no account active.
        store.Touch(@"C:\projects\anonymous.lasero", "Bez účtu", null);
        Assert.Single(store.Recent);

        store.SwitchAccount("account-a");
        Assert.Single(store.Recent);
        Assert.Equal("Projekt A", store.Recent[0].Name);
    }

    [Fact]
    public void SwitchAccount_NeverReadsOrWritesTheLegacySharedFile()
    {
        var legacyPath = PathFor("recent-projects.json");
        File.WriteAllText(legacyPath, """[{"Path":"C:\\projects\\legacy.lasero","Name":"Legacy sdílený","LastOpenedUtc":"2026-01-01T00:00:00Z","ThumbnailPath":null}]""");
        var legacyContentsBefore = File.ReadAllText(legacyPath);

        var store = new RecentProjectsStore(legacyPath);
        store.SwitchAccount("account-a");

        Assert.Empty(store.Recent);
        Assert.Equal(legacyContentsBefore, File.ReadAllText(legacyPath));

        store.Touch(@"C:\projects\new-for-a.lasero", "Nový u A", null);
        Assert.Equal(legacyContentsBefore, File.ReadAllText(legacyPath));
    }

    [Fact]
    public void CorruptFile_FallsBackToEmptyListWithoutThrowing()
    {
        var path = PathFor("recent-projects.json");
        File.WriteAllText(path, "{ not valid json");

        var store = new RecentProjectsStore(path);

        Assert.Empty(store.Recent);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
