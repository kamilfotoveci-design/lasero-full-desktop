using System.IO;
using System.Text.RegularExpressions;
using Lasero.App.Controls;
using Xunit;

namespace Lasero.Tests;

/// <summary>SceneCanvas resolves its theme resources when it loads, and a menu row resolves its icon
/// when it is built, so a renamed or missing key would take the designer down at runtime. These
/// checks read the XAML text (no WPF Application is created in the test host, which would leak a
/// dispatcher into every other test) and prove every key the menu depends on exists.</summary>
public class CanvasContextMenuResourceTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LaseroDesktop.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static HashSet<string> DefinedKeys()
    {
        var theme = Path.Combine(Root(), "Lasero.App", "Theme");
        return Directory.GetFiles(theme, "*.xaml")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "x:Key=\"([^\"]+)\"").Select(match => match.Groups[1].Value))
            .ToHashSet();
    }

    [Fact]
    public void SceneCanvasXamlOnlyReferencesThemeResourcesThatExist()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Controls", "SceneCanvas.xaml"));
        var local = Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();
        var defined = DefinedKeys();

        var referenced = Regex.Matches(xaml, @"\{(?:Static|Dynamic)Resource ([^}\s]+)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct();

        Assert.All(referenced, key => Assert.True(defined.Contains(key) || local.Contains(key), $"Resource '{key}' is not defined"));
        Assert.Contains("CanvasMenuItem", local);
    }

    [Fact]
    public void EveryIconTheMenuModelNamesExistsInTheIconSet()
    {
        var icons = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "Icons.xaml"));
        var defined = Regex.Matches(icons, "x:Key=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();

        var rows = new List<ContextMenuItemModel>();
        rows.AddRange(CanvasContextMenuBuilder.ForObject(new ObjectContextState
        {
            SelectionCount = 2, CanEditNodes = true, CanEditText = true, CanTrace = true, CanRemoveBackground = true,
            CanPaste = true, CanGroup = true, CanUngroup = true, CanAlign = true, CanTransform = true,
            CanBringForward = true, CanSendBackward = true,
        }));
        rows.AddRange(CanvasContextMenuBuilder.ForEmptyCanvas(new EmptyCanvasContextState
        {
            CanPaste = true, ObjectCount = 1, SelectionCount = 1, CanImport = true, HasBed = true,
        }));
        rows.AddRange(CanvasContextMenuBuilder.ForNode(new NodeContextState { CanBreak = true, CanClosePath = true }));
        rows.AddRange(CanvasContextMenuBuilder.ForSegment(new SegmentContextState { IsStraight = true }));
        rows.AddRange(CanvasContextMenuBuilder.ForNodeEditCanvas(new NodeEditCanvasContextState { NodeCount = 3 }));

        static IEnumerable<ContextMenuItemModel> Flatten(IEnumerable<ContextMenuItemModel> items) =>
            items.SelectMany(item => Flatten(item.Children).Prepend(item));

        var named = Flatten(rows).Select(row => row.Icon).Where(icon => icon is not null).Distinct().ToList();
        Assert.NotEmpty(named);
        Assert.All(named, icon => Assert.True(defined.Contains(icon!), $"Icon '{icon}' is not defined in Icons.xaml"));
    }
}
