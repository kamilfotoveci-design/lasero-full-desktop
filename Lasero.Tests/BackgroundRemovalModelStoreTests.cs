using System.IO;
using Lasero.Core.BackgroundRemoval;

namespace Lasero.Tests;

public sealed class BackgroundRemovalModelStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-bgremoval-model-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsModelAvailable_FalseWhenDirectoryDoesNotExistYet()
    {
        var store = new BackgroundRemovalModelStore(_directory);

        Assert.False(store.IsModelAvailable);
    }

    [Fact]
    public void IsModelAvailable_FalseForZeroLengthFile()
    {
        Directory.CreateDirectory(_directory);
        var store = new BackgroundRemovalModelStore(_directory);
        File.WriteAllBytes(store.ModelPath, []);

        Assert.False(store.IsModelAvailable);
    }

    [Fact]
    public void IsModelAvailable_TrueOnceAFileWithContentExistsAtTheExpectedPath()
    {
        Directory.CreateDirectory(_directory);
        var store = new BackgroundRemovalModelStore(_directory);
        File.WriteAllBytes(store.ModelPath, [1, 2, 3]);

        Assert.True(store.IsModelAvailable);
    }

    [Fact]
    public void ModelPath_UsesTheDocumentedFileNameUnderTheGivenDirectory()
    {
        var store = new BackgroundRemovalModelStore(_directory);

        Assert.Equal(Path.Combine(_directory, "isnet-general-use.onnx"), store.ModelPath);
    }

    [Fact]
    public void CreateDefault_PointsUnderLocalApplicationDataLaseroModels()
    {
        var store = BackgroundRemovalModelStore.CreateDefault();

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "Models");
        Assert.StartsWith(expectedRoot, store.ModelPath);
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
            // Best effort cleanup.
        }
    }
}
