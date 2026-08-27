using System.IO;

namespace Lasero.Tests;

public sealed class LaseroDialogWindowTests
{
    [Fact]
    public void RuntimeStyledDialogButtonsUseAnExplicitVisibleTextTemplate()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "LaseroDialogWindow.xaml"));

        Assert.Contains("x:Key=\"DialogButtonLabelTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(xaml, "ContentTemplate=\"{StaticResource DialogButtonLabelTemplate}\""));
        Assert.Contains("RelativeSource AncestorType=Button", xaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0; index += fragment.Length)
            count++;
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Lasero repository root was not found.");
    }
}
