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
    public void DesignerConnectsInPlaceAndOffersOnlyMomentaryPositioningLaser()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var mainViewModel = File.ReadAllText(Path.Combine(root, "Lasero.App", "ViewModels", "MainViewModel.cs"));
        var machinePanel = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "MachinePanelView.xaml"));

        Assert.Contains("Command=\"{Binding Connection.SmartConnectCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CurrentScreen != AppScreen.Designer", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Text=\"Polohovací paprsek\"", machinePanel, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"OnPositioningLaserPressed\"", machinePanel, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp=\"OnPositioningLaserReleased\"", machinePanel, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection.SaveWorkspaceAsDefaultCommand", machinePanel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The drawing tools moved from the command strip above the canvas into the Designer's 56px rail.
    /// What this test defends is unchanged: every tool the scene supports is reachable from a visible
    /// control, none of them silently disappeared with the strip, and each icon-only entry says what
    /// it is to a screen reader.
    /// </summary>
    [Fact]
    public void DesignerRailExposesEveryDrawingToolAndImport()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var rail = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerToolRail.xaml"));

        Assert.Contains("<ColumnDefinition Width=\"*\" MinWidth=\"420\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InspectorColumn\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AppScreenToSidebarWidth", xaml, StringComparison.Ordinal);

        // The strip is gone, along with every style that only it used.
        Assert.DoesNotContain("x:Key=\"DesignerToolbarTool\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"Toolbar.Strip\"", xaml, StringComparison.Ordinal);

        // Select, Text and Line are radio entries; the closed shapes sit behind the rail's shape-tool
        // button and its press-and-hold picker (checked below), not a text dropdown any more.
        Assert.Contains("ConverterParameter=Select", rail, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Text", rail, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Line", rail, StringComparison.Ordinal);

        // Pan is not a button anywhere any more. It is the middle mouse button, space+drag and the H
        // shortcut — all of which work without leaving the tool you are in. A pan *mode* in the rail
        // was a state to get stuck in for something already reachable three ways, so what the cluster
        // carries instead is the thing that was actually missing: a way back to the work area.
        var cluster = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "CanvasViewControls.xaml"));
        var shortcuts = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("ConverterParameter=Pan", rail, StringComparison.Ordinal);
        Assert.DoesNotContain("ConverterParameter=Pan", cluster, StringComparison.Ordinal);
        Assert.Contains("Key.H => DesignerTool.Pan", shortcuts, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCenterClick\"", cluster, StringComparison.Ordinal);
        foreach (var tool in new[]
                 {
                     "Rectangle", "Ellipse", "Triangle", "Pentagon", "Hexagon", "Octagon", "Star", "DoubleStar",
                 })
        {
            // Each shape is a picker cell carrying the tool via Tag, activated from OnShapePickerItemClick
            // rather than a Command binding - the button also has to decide click-vs-long-press first.
            Assert.Contains($"Tag=\"{{x:Static vm:DesignerTool.{tool}}}\"", rail, StringComparison.Ordinal);
        }

        Assert.Contains("Click=\"OnShapePickerItemClick\"", rail, StringComparison.Ordinal);
        Assert.Contains("MouseRightButtonUp=\"OnShapeButtonMouseRightButtonUp\"", rail, StringComparison.Ordinal);

        Assert.Contains("Command=\"{Binding GCode.LoadFileCommand}\"", rail, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Importovat grafiku\"", rail, StringComparison.Ordinal);

        // An icon-only rail is unusable without these, so the count has to match the entries.
        Assert.Equal(
            CountOccurrences(rail, "AutomationProperties.Name="),
            CountOccurrences(rail, "ToolTip="));
    }

    /// <summary>
    /// New, Open, Save and Save As must stay discoverable by pointer. They left the command strip
    /// with everything else, so the project menu in the title bar is now the only visible route to
    /// them — a keyboard shortcut is not discoverability for a first-time owner.
    /// </summary>
    [Fact]
    public void FileCommandsRemainReachableWithoutAKeyboardShortcut()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding NewProjectCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenProjectCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveProjectCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveProjectAsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ProjectNameButton\"", xaml, StringComparison.Ordinal);

        // The menu sits in the caption area, which swallows clicks unless the chrome is told not to.
        Assert.Contains("shell:WindowChrome.IsHitTestVisibleInChrome=\"True\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layer palette stays in the status strip, on the operator's call: assigning a production
    /// colour happens while working on the canvas, and routing it through the inspector's operation
    /// settings was a detour. It lives in exactly one place, though — not in both.
    /// </summary>
    [Fact]
    public void LayerColourPaletteLivesInTheStatusStripAndNowhereElse()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var inspector = File.ReadAllText(Path.Combine(
            root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding Scene.LayerPalette}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Scene.ChangeSelectedLayerColorCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.LayerPalette", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.ChangeSelectedLayerColorCommand", inspector, StringComparison.Ordinal);

        // The operation stack still owns everything else about an operation.
        Assert.Contains("ItemsSource=\"{Binding GCode.Layers}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding Scene.SelectedLayer, Mode=TwoWay}\"", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignerLayerInspectorOffersDirectLineFillAndCombinedModes()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        // What this test defends is the wording and the directness: all three modes named in the
        // operator's terms, each bound straight to LayerMode. The control carrying them has been a
        // combo and is now a segmented track again, so the assertions follow the binding rather than
        // the widget.
        Assert.Contains("Text=\"Režim zpracování\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Čára\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Výplň\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Obojí\"", xaml, StringComparison.Ordinal);
        foreach (var mode in new[] { "Cut", "Fill", "FillAndCut" })
        {
            Assert.Contains(
                $"IsChecked=\"{{Binding Mode, Converter={{StaticResource EnumToBool}}, ConverterParameter={mode}, Mode=TwoWay}}\"",
                xaml,
                StringComparison.Ordinal);
        }

        // Mark is not a LayerMode. The approved mockup shows a third "Značit" segment; adding it
        // would need a Core enum and a toolpath that does not exist, so the UI must not offer it.
        Assert.DoesNotContain("Značit", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Řezání\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBoxItem Content=\"Gravírování\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Power and speed gained sliders. The numeric field is what a production job is actually set by,
    /// so it has to survive alongside them — a slider alone cannot express 3000 mm/min exactly.
    /// </summary>
    [Fact]
    public void ProcessParametersKeepDirectNumericEntryAlongsideTheirSliders()
    {
        var root = FindRepositoryRoot();
        var inspector = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var slider = File.ReadAllText(Path.Combine(root, "Lasero.App", "Components", "ParameterSlider.xaml"));

        Assert.Contains("Label=\"Výkon\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Label=\"Rychlost\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Power, Mode=TwoWay}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Speed, Mode=TwoWay}\"", inspector, StringComparison.Ordinal);

        // Both halves of the control write the same property.
        Assert.Contains("<TextBox x:Name=\"ValueField\"", slider, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Value, ElementName=Root, Mode=TwoWay", slider, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Value, ElementName=Root, Mode=TwoWay}\"", slider, StringComparison.Ordinal);

        // Passes and the fill interval stay typed values; neither belongs on a slider.
        Assert.Contains("Text=\"{Binding Passes, StringFormat=0}\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding FillLineIntervalMm", inspector, StringComparison.Ordinal);
    }

    /// <summary>
    /// The selection's properties used to own a full-width row two lines tall, present whenever the
    /// editor was. They are now a floating bar over the canvas that exists only while something is
    /// selected. What the test still defends is that they live in exactly one place.
    /// </summary>
    [Fact]
    public void SelectionPropertiesLiveInOneContextualBarOverTheCanvas()
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

        // Floating over the canvas rather than owning a layout row, so it costs the workspace nothing
        // when nothing is selected — which is most of the time.
        Assert.Contains("<views:SelectionPropertiesBar HorizontalAlignment=\"Center\" VerticalAlignment=\"Top\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:SelectionPropertiesBar Grid.Row=", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding Scene.HasSelection", bar, StringComparison.Ordinal);

        // Node Edit mode swaps this bar out for NodeEditToolbar rather than showing both — the edited
        // object stays in SceneViewModel.SelectedObjects the whole time node-edit is active, so without
        // this the whole-object X/Y/W/H bar and the per-node bar would float over the canvas together.
        Assert.Contains("<views:NodeEditToolbar HorizontalAlignment=\"Center\" VerticalAlignment=\"Top\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsNodeEditActive, ElementName=DesignerCanvas, Converter={StaticResource InverseBoolToVisibility}}\"", mainWindow, StringComparison.Ordinal);

        // Sideways scroll rather than a clipped field, for the window's 1080px minimum with text
        // selected. A half-cut millimetre value is the one thing a precision tool must not show.
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", bar, StringComparison.Ordinal);

        // The inspector is one flat column now — no tabs, and no card nested inside a card.
        Assert.DoesNotContain("PropertiesTabRadio", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("LayersTabRadio", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("MachineTabRadio", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Operace\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nastavení operace\"", inspector, StringComparison.Ordinal);

        // The default view is a read-only summary; "Ovládání stroje" switches the SAME inspector to the
        // full manual panel (MachinePanelView — the one implementation, also used by the Device screen)
        // instead of opening a separate window, and a back control returns to the operation editor
        // without tearing either panel down (so selection/scroll/settings survive the round trip).
        Assert.Contains("<views:MachinePanelView", inspector, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnMachineControlClick\"", inspector, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnMachineControlBackClick\"", inspector, StringComparison.Ordinal);
        Assert.Contains("MachineStatus.DisplayState", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("MachineControlWindow", inspector, StringComparison.Ordinal);
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
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Lasero.App", "Theme", "SharedUiStyles.xaml"));
        var baseStyle = xaml.IndexOf("x:Key=\"JobDangerButton\"", StringComparison.Ordinal);
        var derivedStyle = xaml.IndexOf(
            "x:Key=\"JobActiveStopButton\" TargetType=\"Button\" BasedOn=\"{StaticResource JobDangerButton}\"",
            StringComparison.Ordinal);

        Assert.True(baseStyle >= 0, "JobDangerButton must be declared in shared UI resources.");
        Assert.True(derivedStyle >= 0, "JobActiveStopButton must derive from JobDangerButton.");
        Assert.True(baseStyle < derivedStyle,
            "WPF StaticResource lookup cannot resolve JobDangerButton when the base style is declared after its first use.");
    }

    [Fact]
    public void BottomStripOwnsJobActionsOnEveryScreen()
    {
        var root = FindRepositoryRoot();
        var inspector = File.ReadAllText(Path.Combine(root, "Lasero.App", "Views", "DesignerInspectorView.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var sharedStyles = File.ReadAllText(Path.Combine(root, "Lasero.App", "Theme", "SharedUiStyles.xaml"));

        Assert.DoesNotContain("Command=\"{Binding GCode.RunFramingCommand}\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding GCode.RunJobCommand}\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding GCode.PauseResumeCommand}\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding GCode.AbortCommand}\"", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("GCode.EstimatedTimeLabel", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireFramingBeforeStart", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Náhled rámování", inspector, StringComparison.Ordinal);

        Assert.Contains("Command=\"{Binding GCode.RunFramingCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding GCode.RunJobCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding GCode.PauseResumeCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding GCode.AbortCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene.SelectedLayer.MaterialDisplayLabel", mainWindow, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"JobStartActionButton\" TargetType=\"Button\" BasedOn=\"{StaticResource JobDangerButton}\"", sharedStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("FramingPreviewDocument", mainWindow, StringComparison.Ordinal);
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
        var columnMin = ReadNumber(mainWindow, "x:Name=\"InspectorColumn\" Width=\"360\" MinWidth=\"", "\"");
        var persistedMin = ReadNumber(settings, "MinInspectorWidth = ", ";");

        Assert.Equal(panelMin, columnMin);
        Assert.Equal(panelMin, persistedMin);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
