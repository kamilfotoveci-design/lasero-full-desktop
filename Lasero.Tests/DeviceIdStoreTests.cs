using System.IO;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

public sealed class DeviceIdStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-device-id-tests", Guid.NewGuid().ToString("N"));

    public DeviceIdStoreTests() => Directory.CreateDirectory(_directory);

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void NewInstall_GeneratesOneValidGuid()
    {
        var store = new DeviceIdStore(PathFor("device-id.txt"));

        var id = store.GetOrCreate();

        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void RepeatedCall_ReusesSameIdWithoutRewriting()
    {
        var store = new DeviceIdStore(PathFor("device-id.txt"));

        var first = store.GetOrCreate();
        var second = store.GetOrCreate();
        var third = store.GetOrCreate();

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Id_PersistsAcrossNewStoreInstances()
    {
        var path = PathFor("device-id.txt");
        var first = new DeviceIdStore(path);
        var id = first.GetOrCreate();

        var second = new DeviceIdStore(path);

        Assert.Equal(id, second.GetOrCreate());
    }

    [Fact]
    public void CorruptOrInvalidFile_RegeneratesAValidId()
    {
        var path = PathFor("device-id.txt");
        File.WriteAllText(path, "not-a-guid");

        var store = new DeviceIdStore(path);
        var id = store.GetOrCreate();

        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void Id_IsNotDerivedFromMachineOrUserIdentity()
    {
        var store = new DeviceIdStore(PathFor("device-id.txt"));

        var id = store.GetOrCreate();

        Assert.DoesNotContain(Environment.MachineName, id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, id, StringComparison.OrdinalIgnoreCase);
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
