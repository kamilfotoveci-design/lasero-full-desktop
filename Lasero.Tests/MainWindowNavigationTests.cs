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
    public void DesignerUsesCompactWorkingToolRailAndRealLayerPalette()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        Assert.Contains("x:Key=\"DesignerRailTool\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AppScreenToSidebarWidth", xaml, StringComparison.Ordinal);
        Assert.Contains("<!-- Compact editor rail: only creation tools remain beside the canvas. -->", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Select", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Pan", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Rectangle", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Ellipse", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Line", xaml, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Text", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Importovat\"", xaml, StringComparison.Ordinal);
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

        Assert.Contains("Text=\"Způsob zpracování\"", xaml, StringComparison.Ordinal);
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
    public void ObjectEditingLivesInTheCanvasToolbarInsteadOfADuplicateInspectorTab()
    {
        var root = FindRepositoryRoot();
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));

        Assert.DoesNotContain("PropertiesTabRadio", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Objekt\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Vybraný objekt\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Vrstvy\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Stroj\"", inspector, StringComparison.Ordinal);

        Assert.Contains("Header=\"Zobrazit na plátně\" IsCheckable=\"True\" IsChecked=\"{Binding Scene.Selected.IsVisible}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Header=\"Zahrnout do úlohy\" IsCheckable=\"True\" IsChecked=\"{Binding Scene.Selected.IncludeInOutput}\"", mainWindow, StringComparison.Ordinal);
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
