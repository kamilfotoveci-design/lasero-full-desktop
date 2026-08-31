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
        // Pan is deliberately not a toolbar tool: it is always available on the middle mouse button
        // and on space+drag, so a mode you had to enter and leave to do the same thing was only a
        // state to get stuck in. DesignerTool.Pan itself still exists and still works.
        Assert.DoesNotContain("ConverterParameter=Pan", xaml, StringComparison.Ordinal);
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

        Assert.Contains("Text=\"Zpracování\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Čára\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Cut", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Výplň\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Fill", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Výplň + čára\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=FillAndCut", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding Mode, Converter={StaticResource EnumToBool}, ConverterParameter=Cut, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding Mode, Converter={StaticResource EnumToBool}, ConverterParameter=Fill, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Řezání\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Gravírování\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectEditingLivesAboveTheInspectorTabsInsteadOfInTheCanvasToolbar()
    {
        var root = FindRepositoryRoot();
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));

        // Not a third tab competing with Vrstvy and Stroj, and not a second copy of the layer list's
        // own naming: one section, sitting above the switch so it survives either tab.
        Assert.DoesNotContain("PropertiesTabRadio", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Objekt\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Vybraný objekt\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Vrstvy\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Stroj\"", inspector, StringComparison.Ordinal);

        var section = inspector.IndexOf("Text=\"Objekt\"", StringComparison.Ordinal);
        var tabStrip = inspector.IndexOf("x:Name=\"LayersTabRadio\"", StringComparison.Ordinal);
        Assert.True(section >= 0, "The inspector has no Objekt section.");
        Assert.True(section < tabStrip, "The Objekt section must sit above the Vrstvy / Stroj switch.");

        Assert.Contains("Header=\"Zobrazit na plátně\" IsCheckable=\"True\" IsChecked=\"{Binding Scene.Selected.IsVisible}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Header=\"Zahrnout do úlohy\" IsCheckable=\"True\" IsChecked=\"{Binding Scene.Selected.IncludeInOutput}\"", inspector, StringComparison.Ordinal);

        // The canvas toolbar is a tool strip only. Selection properties there needed roughly 1500px
        // of a column that offers 1082px at a 1600px window, so the row wrapped at every size.
        Assert.DoesNotContain("Scene.SelectedWidth", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.SelectedRotation", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.LockAspectRatio", mainWindow, StringComparison.Ordinal);
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
