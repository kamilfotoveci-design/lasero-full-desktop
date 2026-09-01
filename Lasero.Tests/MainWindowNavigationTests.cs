using System.IO;

namespace Lasero.Tests;

public sealed class MainWindowNavigationTests
{
    [Fact]
    public void DeviceNavigationOpensTheDeviceWorkspaceInsteadOfDesignerMachinePanel()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml"));

        Assert.Contains("Style=\"{StaticResource NavButton.Device}\" Command=\"{Binding ShowDeviceCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Zařízení\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource NavButton.Device}\" Click=\"OnShowMachinePanelClick\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignerToolbarExposesEveryDrawingToolAndTheLayerPaletteIsWiredToTheScene()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        // These used to assert against a second, Visibility="Collapsed" tool rail that never
        // rendered. That rail has been deleted; the assertions now target the toolbar the operator
        // actually sees, which is the behaviour this test was really about.
        // The canvas column takes the space nothing else claims, and the inspector is a resizable
        // pixel column between bounds — replacing a converter that ignored its input and returned a
        // constant sidebar width.
        Assert.Contains("<ColumnDefinition Width=\"*\" MinWidth=\"420\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InspectorColumn\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AppScreenToSidebarWidth", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DesignerToolbarTool\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Compact editor rail", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Select", xaml, StringComparison.Ordinal);
        // Pan used to be kept out of the toolbar on the grounds that it duplicates the middle mouse
        // button and space+drag, and so was only a mode to get stuck in. The target design puts
        // "Posunout" in the strip, and it is the discoverable route to panning for someone who does
        // not yet know the shortcuts — the shortcuts still work either way.
        Assert.Contains("ConverterParameter=Pan", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Rectangle", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Ellipse", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Line", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Text", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Importovat grafiku\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding GCode.Layers}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding Scene.SelectedLayer, Mode=TwoWay}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Scene.LayerPalette}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Scene.ChangeSelectedLayerColorCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignerLayerInspectorOffersDirectLineFillAndCombinedModes()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        // What this test defends is the wording and the directness: all three modes named in the
        // operator's terms, each bound straight to LayerMode. The control carrying them moved from a
        // segmented radio track to the Režim row of Nastavení práce, so the assertions follow the
        // binding rather than the widget.
        Assert.Contains("Text=\"Režim\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Čára\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Cut", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Výplň\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Fill", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Výplň + čára\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=FillAndCut", xaml, StringComparison.Ordinal);
        Assert.Contains("IsSelected=\"{Binding Mode, Converter={StaticResource EnumToBool}, ConverterParameter=Cut, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsSelected=\"{Binding Mode, Converter={StaticResource EnumToBool}, ConverterParameter=Fill, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Řezání\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Gravírování\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionPropertiesLiveInOneFullWidthBarAboveTheWorkspace()
    {
        var root = FindRepositoryRoot();
        var bar = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "SelectionPropertiesBar.xaml"));
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));

        // Position, size, rotation and the type settings all live on the bar, the way LightBurn,
        // Illustrator and CorelDRAW place them.
        foreach (var property in new[]
                 {
                     "Scene.SelectedX", "Scene.SelectedY", "Scene.SelectedWidth", "Scene.SelectedHeight",
                     "Scene.SelectedRotation", "Scene.LockAspectRatio",
                     "Scene.SelectedTextValue", "Scene.SelectedTextFontFamily", "Scene.SelectedTextHeight",
                     "Scene.SelectedTextBold", "Scene.SelectedTextItalic",
                     "Scene.SelectedTextUppercase", "Scene.SelectedTextWeld",
                 })
        {
            Assert.Contains(property, bar, StringComparison.Ordinal);
        }

        // Exactly once, and nowhere else. Two places to change the same number is how the toolbar and
        // the inspector ended up disagreeing about which one owned the selection.
        Assert.DoesNotContain("Scene.SelectedWidth", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.SelectedTextValue", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.SelectedWidth", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.LockAspectRatio", mainWindow, StringComparison.Ordinal);

        // Full window width, in its own row above the workspace: the canvas column alone is 1082px at
        // a 1600px window, against roughly 1500px of controls.
        Assert.Contains("<views:SelectionPropertiesBar Grid.Row=\"1\"", mainWindow, StringComparison.Ordinal);

        // Sideways scroll rather than a clipped field, for the window's 1080px minimum with text
        // selected. A half-cut millimetre value is the one thing a precision tool must not show.
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", bar, StringComparison.Ordinal);

        // The inspector is the two tabs and nothing else - no third tab competing with them.
        Assert.DoesNotContain("PropertiesTabRadio", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Vrstvy\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Stroj\"", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximizeUsesNativeWindowsWorkAreaSoTheBottomJobStripIsNotClipped()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml.cs"));

        Assert.Contains("SystemCommands.MaximizeWindow(this)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SystemCommands.RestoreWindow(this)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void JobStopButtonBaseStyleIsDeclaredBeforeItIsReferenced()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml"));
        var baseStyle = xaml.IndexOf("x:Key=\"JobDangerButton\"", StringComparison.Ordinal);
        var derivedStyle = xaml.IndexOf(
            "x:Key=\"JobActiveStopButton\" TargetType=\"Button\" BasedOn=\"{StaticResource JobDangerButton}\"",
            StringComparison.Ordinal);

        Assert.True(baseStyle >= 0, "JobDangerButton must be declared in MainWindow resources.");
        Assert.True(derivedStyle >= 0, "JobActiveStopButton must derive from JobDangerButton.");
        Assert.True(baseStyle < derivedStyle,
            "WPF StaticResource lookup cannot resolve JobDangerButton when the base style is declared after its first use.");
    }

    [Fact]
    public void InspectorColumnCannotBeNarrowerThanThePanelItHolds()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var inspector = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var settings = File.ReadAllText(Path.Combine(root, "Lasero.App", "AppSettingsStore.cs"));

        // A Grid never shrinks a child below its MinWidth. When the column allowed 280 and the panel
        // asked for 320, the panel kept its 320 and the surplus hung off the right edge of the
        // window: X, Y, width and height were all cut through the middle of their value.
        var panelMin = ReadNumber(inspector, "MinWidth=\"", "\"");
        var columnMin = ReadNumber(mainWindow, "x:Name=\"InspectorColumn\" Width=\"336\" MinWidth=\"", "\"");
        var persistedMin = ReadNumber(settings, "MinInspectorWidth = ", ";");

        Assert.Equal(panelMin, columnMin);
        Assert.Equal(panelMin, persistedMin);
    }

    private static double ReadNumber(string text, string prefix, string terminator)
    {
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{prefix}' was not found.");
        start += prefix.Length;
        var end = text.IndexOf(terminator, start, StringComparison.Ordinal);
        return double.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture);
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
