using System.IO;
using System.Text.Json;
using Lasero.App;
using Lasero.Core.History;

namespace Lasero.Tests;

public sealed class JobHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-job-history-tests", Guid.NewGuid().ToString("N"));

    public JobHistoryStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SwitchingAccountsShowsOnlyThatAccountsHistory()
    {
        var store = new JobHistoryStore(Path.Combine(_directory, "job-history.json"));
        store.SwitchAccount("account-a");
        store.Append(Entry("První účet"));

        store.SwitchAccount("account-b");
        Assert.Empty(store.Entries);
        store.Append(Entry("Druhý účet"));

        store.SwitchAccount("account-a");
        Assert.Equal("První účet", Assert.Single(store.Entries).Name);
        store.SwitchAccount(null);
        Assert.Empty(store.Entries);
        store.SwitchAccount("account-b");
        Assert.Equal("Druhý účet", Assert.Single(store.Entries).Name);
    }

    [Fact]
    public void LegacySharedHistoryIsPreservedWithoutAssigningItToAnAccount()
    {
        var legacyPath = Path.Combine(_directory, "job-history.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new[] { Entry("Starší historie") }));
        var store = new JobHistoryStore(legacyPath);

        store.SwitchAccount("account-a");
        Assert.Empty(store.Entries);
        store.Append(Entry("Nová historie"));

        Assert.Equal("Starší historie", Assert.Single(JsonSerializer.Deserialize<List<JobHistoryEntry>>(
            File.ReadAllText(legacyPath))!).Name);
        store.SwitchAccount(null);
        store.Append(Entry("Bez účtu"));
        Assert.Empty(store.Entries);
    }

    private static JobHistoryEntry Entry(string name) => new(name, null, 60, DateTime.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
