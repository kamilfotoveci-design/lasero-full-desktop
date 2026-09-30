using System.IO;
using System.Xml.Linq;
using Lasero.App.ViewModels;
using Lasero.Core.Machines;
using Xunit;

namespace Lasero.Tests;

/// <summary>A ComboBox over records printed "JoinTypeOption { Value = Round, Label = ... }" in its closed
/// box (twice now). Three guards: the theme template must use ContentSource so DisplayMemberPath reaches
/// the closed box; every ComboBox over a bound list of objects must declare how to display them; and
/// every item type must have a ToString that is not the compiler's record dump.</summary>
public sealed class ComboBoxDisplayTests
{
    // Bound collections whose items are primitives (string/int/double), which render fine unaided.
    private static readonly string[] PrimitiveSources =
    [
        "AvailablePorts", "CommonBaudRates", "StepSizePresets", "PowerClasses", "SimulationSpeedPresets",
    ];

    [Fact]
    public void ThemeComboBoxTemplateHonoursDisplayMemberPathInTheClosedBox()
    {
        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        Assert.Contains("ContentTemplateSelector=\"{TemplateBinding ItemTemplateSelector}\"", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryComboBoxOverObjectsDeclaresDisplayMemberPathOrItemTemplate()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "Lasero.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var doc = XDocument.Load(file);
            foreach (var combo in doc.Descendants().Where(e => e.Name.LocalName == "ComboBox"))
            {
                var items = combo.Attribute("ItemsSource")?.Value;
                if (items is null) continue; // static ComboBoxItem children
                var hasDisplay = combo.Attribute("DisplayMemberPath") is not null
                    || combo.Attribute("ItemTemplate") is not null
                    || combo.Elements().Any(e => e.Name.LocalName == "ComboBox.ItemTemplate");
                var primitive = PrimitiveSources.Any(p => items.Contains(p, StringComparison.Ordinal));
                if (!hasDisplay && !primitive)
                    offenders.Add($"{Path.GetFileName(file)}: {items}");
            }
        }
        Assert.True(offenders.Count == 0, "ComboBox without display template: " + string.Join("; ", offenders));
    }

    [Fact]
    public void ComboBoxItemTypesNeverRenderAsARecordDump()
    {
        var texts = new List<string>();
        texts.AddRange(new OffsetPathViewModel([new Lasero.Core.Scene.SceneObject { LocalShapes = [], LocalPivot = Lasero.Core.Grbl.Position.Zero, LocalBounds = Lasero.Core.GCode.BoundingBox2D.Empty, Name = "t" }]).JoinTypeOptions.Select(o => o.ToString()));
        texts.AddRange(RasterImportViewModel.DitheringChoices.Select(o => o.ToString()));
        texts.AddRange(MachineCompatibilityCatalog.All.Select(o => o.ToString()));

        Assert.NotEmpty(texts);
        foreach (var t in texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.DoesNotContain("{", t, StringComparison.Ordinal);
            Assert.DoesNotContain("Value =", t, StringComparison.Ordinal);
        }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "Lasero.App"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
