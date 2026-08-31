using System.IO;
using System.Text.RegularExpressions;

namespace Lasero.Tests;

public sealed class MaterialsWindowResourceTests
{
    [Fact]
    public void EveryStaticResourceUsedByMaterialsWindowIsDefined()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "Lasero.App");
        var materialsXaml = File.ReadAllText(Path.Combine(appRoot, "MaterialsWindow.xaml"));
        var resourceText = materialsXaml + Environment.NewLine + string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(appRoot, "Theme"), "*.xaml")
                .Select(File.ReadAllText));

        var references = Regex.Matches(materialsXaml, @"\{StaticResource\s+([^},]+)")
            .Select(match => match.Groups[1].Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var definitions = Regex.Matches(resourceText, "x:Key=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var missing = references.Where(reference => !definitions.Contains(reference)).ToArray();

        Assert.True(missing.Length == 0, $"Missing StaticResource keys: {string.Join(", ", missing)}");
    }

    [Fact]
    public void InlineRunTextBindingsAreExplicitlyOneWay()
    {
        var materialsXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "MaterialsWindow.xaml"));
        var unsafeBindings = Regex.Matches(materialsXaml, "<Run\\s+Text=\"\\{Binding[^\"]+\"")
            .Select(match => match.Value)
            .Where(binding => !binding.Contains("Mode=OneWay", StringComparison.Ordinal))
            .ToArray();

        Assert.True(unsafeBindings.Length == 0,
            $"Run.Text bindings must be OneWay because WPF otherwise writes to computed recipe properties: {string.Join(" | ", unsafeBindings)}");
    }

    [Fact]
    public void RecommendedMaterialsUseTheVisualSwatchMatrix()
    {
        var materialsXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "MaterialsWindow.xaml"));

        Assert.Contains("ItemsSource=\"{Binding SwatchCards}\"", materialsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Cells}\"", materialsXaml, StringComparison.Ordinal);
        Assert.Contains("SelectSwatchCellCommand", materialsXaml, StringComparison.Ordinal);
        Assert.Contains("Text gravírovaného náhledu", materialsXaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lasero.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Lasero repository root was not found from the test output directory.");
    }
}
