using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Shell;
using Lasero.App.Input;
using Lasero.App.ViewModels;
using Lasero.App.Views.Kamil;
using Lasero.App.Views.DeviceSetup;
using Lasero.Core.BackgroundRemoval;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Scene;
using Serilog;

namespace Lasero.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly BackgroundRemovalCoordinator _backgroundRemoval;
    private readonly BackgroundRemovalConsentStore _backgroundRemovalConsent;
    private MaterialsWindow? _materialsWindow;
    private PreviewWindow? _previewWindow;

    public MainWindow(
        MainViewModel viewModel,
        BackgroundRemovalCoordinator backgroundRemoval,
        BackgroundRemovalConsentStore backgroundRemovalConsent)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _backgroundRemoval = backgroundRemoval;
        _backgroundRemovalConsent = backgroundRemovalConsent;
        DataContext = viewModel;
        _viewModel.GCode.SimulationStarted += OnSimulationStarted;
        _viewModel.GCode.StartBlocked += OnStartBlocked;
        _viewModel.Scene.TraceRasterRequested += OnTraceRasterRequested;
        _viewModel.Scene.BackgroundRemovalRequested += OnBackgroundRemovalRequested;
        _viewModel.Scene.OffsetRequested += OnOffsetRequested;
        _viewModel.Scene.VectorOperationRejected += OnVectorOperationRejected;
        _viewModel.DeviceWizardRequested += OpenDeviceWizard;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DesignerCanvas.TextPlacementRequested += OnTextPlacementRequested;
        KamilHost.AssistantResized += OnAssistantResized;
        DeviceWizardOverlayHost.SettingsRequested += (_, _) => OpenSettings();
        Loaded += OnLoaded;
        Closing += OnClosing;
        // Without this the maximized window is inflated by the resize border, which pushed the
        // bottom job strip — Frame and Start — off the bottom of the screen.
        MaximizeWorkAreaHook.Attach(this);
        // Windows 11 rounds every framed window and draws a hairline around it. A borderless window
        // opts out of both, so the app had square corners and no visible edge; DWM gives them back.
        WindowFrameHook.Attach(this, TryFindResource("Brush.PanelBorderStrong") as System.Windows.Media.Brush);
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        UpdateMaximizeGlyph();
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => _viewModel.SaveRecoverySnapshot();
        _autosaveTimer.Start();
    }

    // "F" fits the view, matching LightBurn/Illustrator/Photoshop convention — deliberately a
    // PreviewKeyDown check, not a declarative KeyBinding, since an unmodified single-letter binding
    // would fire even while a TextBox has keyboard focus (e.g. typing "feed" into a field).
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isTextInput = Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox;

        // Shortcuts that act on the design belong to the Designer screen. The bindings in
        // MainWindow.xaml are window-wide, so without this Delete, Ctrl+D or Ctrl+Z on Home, Device
        // or Chat changed a design the user could not see. A text field keeps its own editing keys.
        var shortcutKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var onDesigner = _viewModel.CurrentScreen == AppScreen.Designer;
        if (InteractionRules.SuppressSceneShortcut(onDesigner, shortcutKey, Keyboard.Modifiers, isTextInput))
        {
            e.Handled = true;
            return;
        }

        // And never mid-gesture: Delete during a move would commit a transform for deleted objects.
        if (onDesigner && DesignerCanvas.IsPointerGestureActive &&
            InteractionRules.IsMutatingSceneShortcut(shortcutKey, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (isTextInput) return;

        var modifiers = Keyboard.Modifiers;
        if (_viewModel.CurrentScreen == AppScreen.Designer && e.Key == Key.G && modifiers == ModifierKeys.Control &&
            _viewModel.Scene.GroupSelectionCommand.CanExecute(null))
        {
            _viewModel.Scene.GroupSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.CurrentScreen == AppScreen.Designer && e.Key == Key.G &&
                 modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
                 _viewModel.Scene.UngroupSelectionCommand.CanExecute(null))
        {
            _viewModel.Scene.UngroupSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.CurrentScreen == AppScreen.Designer && modifiers == ModifierKeys.None &&
            TryActivateDesignerTool(e.Key))
        {
            e.Handled = true;
        }
        else if ((e.Key == Key.F && modifiers == ModifierKeys.None) ||
            (e.Key == Key.D0 && modifiers == ModifierKeys.Control) ||
            (e.Key == Key.NumPad0 && modifiers == ModifierKeys.Control))
        {
            DesignerCanvas.FitToView();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add)
        {
            DesignerCanvas.ZoomIn();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract)
        {
            DesignerCanvas.ZoomOut();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            OpenKeyboardShortcuts();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Esc that nothing closer claimed (docs/interaction-rules.md 1.1). It runs on the bubbling event
    /// so an open dropdown, a field with an edit to revert and the assistant's own composer all get
    /// first refusal, and it is what makes Esc work when focus is on a button or the inspector
    /// instead of the canvas.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled) return;

        var action = InteractionRules.ForWindowEscape(
            designerScreen: _viewModel.CurrentScreen == AppScreen.Designer,
            focusInTextInput: Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox,
            canvasHasEscapableState: DesignerCanvas.HasEscapableState,
            assistantOpen: _viewModel.Kamil.State is not (KamilAssistantState.Minimized or KamilAssistantState.Hidden));

        switch (action)
        {
            case WindowEscapeAction.LeaveTextField:
                DesignerCanvas.Focus();
                e.Handled = true;
                break;
            case WindowEscapeAction.CanvasLayers:
                e.Handled = DesignerCanvas.HandleEscape();
                break;
            case WindowEscapeAction.MinimizeAssistant:
                _viewModel.Kamil.StepBack();
                e.Handled = true;
                break;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        // Let Windows calculate the maximized work area instead of assigning WindowState directly.
        // This matters for a borderless WindowChrome window: the native system command respects the
        // taskbar and the current monitor's usable bounds, so the persistent job strip stays visible.
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    // Native Windows title bars swap the maximize glyph for a restore (overlapping-rectangles) glyph
    // once the window is maximized — Settings, Terminal, and Explorer all do this — so a single static
    // square is a visible tell that the chrome is hand-rolled rather than following the platform convention.
    private void UpdateMaximizeGlyph()
    {
        MaximizeGlyph.IconData = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "Glyph.Restore" : "Glyph.Maximize");
        MaximizeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            WindowState == WindowState.Maximized ? "Obnovit" : "Maximalizovat");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.GCode.JobState is JobRunState.Running or JobRunState.Paused or JobRunState.Framing)
        {
            var stop = LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Probíhající úloha",
                "Laser právě zpracovává úlohu. Před ukončením aplikace je nutné úlohu bezpečně zastavit.",
                "Zastavit a ukončit",
                CancelText: "Zůstat v aplikaci",
                Tone: LaseroDialogTone.Danger,
                DestructivePrimary: true));
            if (stop != LaseroDialogChoice.Primary)
            {
                e.Cancel = true;
                return;
            }

            _viewModel.GCode.AbortCommand.Execute(null);
        }

        if (_viewModel.IsDirty)
        {
            var save = LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Neuložené změny",
                "Projekt obsahuje změny, které ještě nejsou uložené. Uložte je, abyste o svou práci nepřišli.",
                "Uložit projekt",
                SecondaryText: "Neukládat",
                CancelText: "Zrušit",
                Tone: LaseroDialogTone.Warning));
            if (save == LaseroDialogChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (save == LaseroDialogChoice.Primary && !_viewModel.TrySaveProject())
            {
                e.Cancel = true;
                return;
            }

            if (save == LaseroDialogChoice.Secondary)
                _viewModel.DiscardRecoverySnapshot();
        }

        _backgroundRemoval.Cancel();
        _autosaveTimer.Stop();
        if (!_viewModel.IsDirty) _viewModel.DiscardRecoverySnapshot();
        _viewModel.SaveSettings();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Public so the Designer rail can reach it — same route the sidebar's own entry takes.</summary>
    public void OpenSettings()
    {
        var dialog = new SettingsWindow(_viewModel, PrepareForSignOut) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.SignOutRequested) return;

        _viewModel.Connection.CancelDetection();
        if (_viewModel.Connection.DisconnectCommand.CanExecute(null))
            _viewModel.Connection.DisconnectCommand.Execute(null);
        // These non-modal windows can retain the previous account's project or material data.
        _previewWindow?.Close();
        _materialsWindow?.Close();
        _viewModel.ClearWorkspaceForAccountSwitch();
        Hide();
        // No explicit reload call needed here, for either branch: SignOutCommand (run inside
        // SettingsWindow, before dialog.ShowDialog() above returned) already cleared every
        // account-scoped store the moment Account.UserId became null, and a successful sign-in
        // below reloads them again the moment SignInCommand sets UserId to the new account — both
        // driven by AccountViewModel.PropertyChanged, not by this window's own show/hide ordering.
        var login = new LoginWindow(_viewModel.Account) { Owner = this };
        if (login.ShowDialog() == true)
        {
            Show();
            Activate();
            PromptRecoverySnapshot();
        }
        else
        {
            Close();
        }
    }

    private bool PrepareForSignOut(Window owner)
    {
        if (_viewModel.Connection.IsConnecting)
        {
            LaseroDialogWindow.Show(owner, new LaseroDialogOptions(
                "Připojování zařízení",
                "Počkejte na dokončení připojování a odhlášení opakujte.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Warning));
            return false;
        }
        if (_viewModel.GCode.IsJobActive)
        {
            LaseroDialogWindow.Show(owner, new LaseroDialogOptions(
                "Probíhající úloha",
                "Před odhlášením bezpečně dokončete nebo zastavte probíhající úlohu.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Warning));
            return false;
        }

        if (!_viewModel.IsDirty) return true;
        var decision = LaseroDialogWindow.Show(owner, new LaseroDialogOptions(
            "Neuložené změny",
            "Před odhlášením uložte projekt, jinak budou neuložené změny zahozeny.",
            "Uložit projekt",
            SecondaryText: "Zahodit změny",
            CancelText: "Zrušit odhlášení",
            Tone: LaseroDialogTone.Warning));
        if (decision == LaseroDialogChoice.Primary)
            return _viewModel.TrySaveProject();
        if (decision != LaseroDialogChoice.Secondary)
            return false;

        _viewModel.DiscardRecoverySnapshot();
        return true;
    }

    private bool TryActivateDesignerTool(Key key)
    {
        var tool = key switch
        {
            Key.V => DesignerTool.Select,
            Key.H => DesignerTool.Pan,
            Key.R => DesignerTool.Rectangle,
            Key.E => DesignerTool.Ellipse,
            Key.L => DesignerTool.Line,
            Key.T => DesignerTool.Text,
            _ => (DesignerTool?)null,
        };

        if (tool is null) return false;
        _viewModel.Scene.ActiveTool = tool.Value;
        DesignerCanvas.Focus();
        return true;
    }

    /// <summary>
    /// Creates a text object instantly at the click point with placeholder wording and the default
    /// style — no modal dialog in between. Replaces the old flow that opened TextToolWindow on every
    /// click; the tool deliberately does NOT revert to Select afterwards (AddText below only adds and
    /// selects, unlike PlaceAndAdd), so another click keeps placing more text, matching how the shape
    /// tools already behave. Wording/font/style stay editable afterwards — inline on the canvas via
    /// SceneCanvas's inline text editor, or from the selection bar's own text controls.
    /// </summary>
    private void OnTextPlacementRequested(Position position)
    {
        const string placeholderText = "TEXT";
        try
        {
            _viewModel.Scene.AddText(placeholderText, position, TextSource.DefaultHeightMm, VectorTextStyle.Default);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create vector text");
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Text se nepodařilo vytvořit",
                "Text nelze převést na vektorové křivky. Zkuste jiný font nebo menší velikost.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    private void OnStartBlocked(string title, string reason)
    {
        Dispatcher.BeginInvoke(() => LaseroDialogWindow.Show(this, new LaseroDialogOptions(
            title,
            reason,
            "Rozumím",
            CancelText: null,
            Tone: LaseroDialogTone.Warning)));
    }

    private void OnSimulationStarted()
    {
        Dispatcher.Invoke(() =>
        {
            if (_previewWindow is not null)
            {
                if (_previewWindow.WindowState == WindowState.Minimized)
                    SystemCommands.RestoreWindow(_previewWindow);
                _previewWindow.Activate();
                return;
            }

            _previewWindow = new PreviewWindow(_viewModel) { Owner = this };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            _previewWindow.Show();
        });
    }

    private void OnTraceRasterRequested(SceneObject source)
    {
        if (string.IsNullOrWhiteSpace(source.RasterFilePath)) return;

        try
        {
            var traceViewModel = new BitmapTraceViewModel(source.RasterFilePath, source.LocalBounds.Width);
            var window = new BitmapTraceWindow(traceViewModel) { Owner = this };
            if (window.ShowDialog() != true || traceViewModel.Result is not { } result) return;

            _viewModel.Scene.ReplaceRasterWithTrace(source, result);
            if (_viewModel.GCode.RegenerateFromSceneCommand.CanExecute(null))
                _viewModel.GCode.RegenerateFromSceneCommand.Execute(null);
            _viewModel.GCode.LastMessage = $"Bitmapa byla převedena na {result.ContourCount} vektorových obrysů. Změnu lze vrátit pomocí Ctrl+Z.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to trace bitmap {RasterPath}", source.RasterFilePath);
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Bitmapu nelze trasovat",
                "Obrázek se nepodařilo převést na vektorové obrysy. Zkuste jiný formát nebo zkontrolujte, zda je soubor dostupný.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    /// <summary>Shows the one-time notice that the image leaves the computer, then runs the removal.
    /// The result is committed as a single undoable replacement by the coordinator; every failure ends
    /// as a short message in the inspector, never as silence or a crash.</summary>
    private async void OnBackgroundRemovalRequested(SceneObject source)
    {
        try
        {
            if (!_backgroundRemovalConsent.HasConsent)
            {
                var consent = LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                    "Odstranění pozadí online",
                    "Vybraný obrázek se odešle přes zabezpečené připojení službě Lasero a zpracuje se modelem Gemini. Obrázek opustí váš počítač. Toto upozornění se zobrazí jen jednou. Pokračujte jen pokud s odesláním souhlasíte.",
                    "Odeslat a zpracovat",
                    CancelText: "Zrušit"));
                if (consent != LaseroDialogChoice.Primary) return;
                _backgroundRemovalConsent.RecordConsent();
            }

            var outcome = await _backgroundRemoval.RunAsync(source);
            if (outcome != BackgroundRemovalOutcome.Committed) return;
            if (_viewModel.GCode.RegenerateFromSceneCommand.CanExecute(null))
                _viewModel.GCode.RegenerateFromSceneCommand.Execute(null);
            _viewModel.GCode.LastMessage = "Pozadí bylo odstraněno. Změnu lze vrátit pomocí Ctrl+Z.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure while removing background");
            _viewModel.Scene.BackgroundRemovalError = BackgroundRemovalCoordinator.GenericFailureMessage;
        }
    }

    private void OnVectorOperationRejected(string message) =>
        LaseroDialogWindow.Show(this, new LaseroDialogOptions(
            "Vektory nelze bezpečně sjednotit",
            message,
            "Rozumím",
            CancelText: null,
            Tone: LaseroDialogTone.Warning));

    /// <summary>Opens OffsetPathWindow for the selection SceneViewModel.OffsetSelectionCommand
    /// captured, and — on OK — hands the dialog's computed result back to Scene.ApplyOffset. Mirrors
    /// OnTraceRasterRequested's open/result-handling shape.</summary>
    private void OnOffsetRequested(IReadOnlyList<SceneObject> sources)
    {
        if (sources.Count == 0) return;

        var offsetViewModel = new OffsetPathViewModel(
            sources, keepOriginal: _viewModel.SettingsStore.Current.Workspace.OffsetKeepOriginal);
        var window = new OffsetPathWindow(offsetViewModel) { Owner = this };
        if (window.ShowDialog() != true) return;

        _viewModel.Scene.ApplyOffset(sources, offsetViewModel.ResultsBySource, offsetViewModel.KeepOriginal);
        try
        {
            _viewModel.SettingsStore.Current.Workspace.OffsetKeepOriginal = offsetViewModel.KeepOriginal;
            _viewModel.SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist offset keep-original preference");
        }
        if (_viewModel.GCode.RegenerateFromSceneCommand.CanExecute(null))
            _viewModel.GCode.RegenerateFromSceneCommand.Execute(null);
        _viewModel.GCode.LastMessage = "Offset byl použit. Změnu lze vrátit pomocí Ctrl+Z.";
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
        => OpenMaterials();

    /// <summary>Opens Kamil only in the Designer workspace, at its fixed bottom-right canvas anchor.</summary>
    public void OpenKamilInDesigner()
    {
        if (_viewModel.ShowDesignerCommand.CanExecute(null))
            _viewModel.ShowDesignerCommand.Execute(null);

        if (_viewModel.Kamil.ShowCommand.CanExecute(null))
            _viewModel.Kamil.ShowCommand.Execute(null);
        else if (_viewModel.Kamil.ExpandCommand.CanExecute(null))
            _viewModel.Kamil.ExpandCommand.Execute(null);
    }

    /// <summary>Opens the in-window device connection overlay (DeviceWizardOverlay, declared once
    /// in MainWindow.xaml) with a fresh DeviceWizardViewModel, rather than a modal window — the
    /// wizard still opens and closes the serial ports the rest of the app would otherwise be using,
    /// but the overlay's own RequestClose already cancels an in-flight scan/connect safely first.</summary>
    public void OpenDeviceWizard()
    {
        try
        {
            DeviceWizardOverlayHost.Show(_viewModel.CreateDeviceWizard());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open the device wizard");
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Průvodce nelze otevřít",
                "Průvodce zařízením se nepodařilo načíst. Zařízení lze připojit ručně v sekci Zařízení.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    /// <summary>Public so the Designer rail's shape picker can return focus to the canvas after
    /// activating a tool, the same way MainWindow's own keyboard shortcuts already do.</summary>
    public void FocusCanvas() => DesignerCanvas.Focus();

    public void OpenMaterials()
    {
        if (_materialsWindow is not null)
        {
            _materialsWindow.Activate();
            return;
        }

        try
        {
            _materialsWindow = new MaterialsWindow(_viewModel) { Owner = this };
            _materialsWindow.Closed += (_, _) => _materialsWindow = null;
            _materialsWindow.Show();
        }
        catch (Exception ex)
        {
            _materialsWindow = null;
            Log.Error(ex, "Failed to open the materials catalog");
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Vzorník nelze otevřít",
                "Vzorník materiálů se nepodařilo načíst. Podrobnosti byly uloženy do diagnostického protokolu.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    private DateTime _projectMenuClosedAt = DateTime.MinValue;

    /// <summary>
    /// The whole project-name button (name, unsaved dot, chevron) opens the file menu. A press on the
    /// button while the menu is open first closes the menu through WPF's outside-click handling, and
    /// the click that follows would reopen it; the timestamp makes that click a plain close.
    /// </summary>
    private void OnProjectNameButtonClick(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _projectMenuClosedAt < TimeSpan.FromMilliseconds(400))
        {
            ProjectNameButton.IsChecked = false;
            return;
        }

        OpenProjectMenu();
    }

    /// <summary>Alt+Down and F4 open the menu like on a combo box. Enter and Space click natively.</summary>
    private void OnProjectNameButtonKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var altDown = key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (altDown || key == Key.F4)
        {
            OpenProjectMenu();
            e.Handled = true;
        }
    }

    private void OpenProjectMenu()
    {
        ProjectMenu.PlacementTarget = ProjectNameButton;
        ProjectMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ProjectMenu.IsOpen = true;
    }

    private void OnProjectMenuOpened(object sender, RoutedEventArgs e) => ProjectNameButton.IsChecked = true;

    private void OnProjectMenuClosed(object sender, RoutedEventArgs e)
    {
        _projectMenuClosedAt = DateTime.UtcNow;
        ProjectNameButton.IsChecked = false;
    }

    /// <summary>
    /// Opens device settings. This used to switch to the Designer screen and flip the inspector to
    /// its machine tab — which, if the operator was already there, changed nothing visible and read
    /// as a dead button. The header now does what its label says.
    /// </summary>
    private void OnDeviceSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceSettingsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>The canvas is collapsed while another screen is showing, so it has no size to fit
    /// against and lands on whatever scale it last had. Fitting once it is actually visible is why
    /// the bed fills the view when the operator arrives in the editor, at Background priority so the
    /// layout pass that gave it a size has finished first.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentScreen)) return;

        ApplyWorkspaceMode();

        if (_viewModel.CurrentScreen != AppScreen.Designer) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_viewModel.CurrentScreen == AppScreen.Designer) DesignerCanvas.FitToView();
        }));
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        LaseroDialogWindow.Show(this, new LaseroDialogOptions(
            "LASERO Desktop 1.2.0",
            "Profesionální editor návrhů a bezpečné ovládání laserových gravírek GRBL.",
            "Hotovo",
            CancelText: null));

    private void OnKeyboardShortcutsClick(object sender, RoutedEventArgs e) => OpenKeyboardShortcuts();

    private void OpenKeyboardShortcuts()
    {
        var dialog = new KeyboardShortcutsWindow { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Restores the operator's inspector width. Clamped by the store, so a stale or hand-edited
    /// settings file cannot open the app with the workspace crushed or the panel off-screen.
    /// </summary>
    /// <summary>
    /// A width remembered on a wide screen is wrong on a narrow one: 560px of inspector leaves the
    /// canvas 640px at 1366, which is where the toolbar starts having to scroll and the drawing area
    /// stops being the thing that dominates the window. The saved value is honoured up to a third of
    /// the window and never below the panel's own minimum, so the preference survives without the
    /// canvas paying for it. Nothing is written back — the operator's number stays on disk.
    /// </summary>
    private void RestoreWorkspaceLayout()
    {
        ApplyWorkspaceMode();

        // Starting straight into the editor skips the screen change that normally triggers the fit,
        // so the canvas would keep whatever scale it measured before the rail and inspector had their
        // final widths. Background priority: after this layout pass, not during it.
        if (_viewModel.CurrentScreen == AppScreen.Designer)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => DesignerCanvas.FitToView()));

        var saved = _viewModel.SettingsStore.Current.Workspace.ClampedInspectorWidth;
        var window = ActualWidth > 0 ? ActualWidth : Width;
        var ceiling = Math.Max(InspectorColumn.MinWidth, window / 3);
        InspectorColumn.Width = new GridLength(Math.Min(saved, ceiling));
    }

    /// <summary>
    /// Swaps the left column between the full application sidebar and the Designer's 56px rail.
    /// The application sidebar is always expanded; only the editor substitutes its dedicated compact
    /// tool rail so navigation never changes posture behind the operator's back.
    /// </summary>
    private void ApplyWorkspaceMode()
    {
        var designer = _viewModel.CurrentScreen == AppScreen.Designer;

        NavRailHost.Visibility = designer ? Visibility.Collapsed : Visibility.Visible;
        DesignerRail.Visibility = designer ? Visibility.Visible : Visibility.Collapsed;
        // Kamil owns the visibility of its inner layers while switching between minimized,
        // quick-ask and expanded states. Keep the host itself scoped to the Designer so that a
        // stale inner state can never leave the avatar visible on Home, Device or Chat.
        KamilHost.Visibility = designer ? Visibility.Visible : Visibility.Collapsed;

        if (designer)
        {
            NavColumn.Width = new GridLength(DesignerRailWidth);
            return;
        }

        NavColumn.Width = new GridLength(NavigationRailWidth);
    }

    private const double NavigationRailWidth = 164;

    /// <summary>Matches DesignerToolRail's own Width. The rail is a fixed strip, not a resizable
    /// panel, so the number lives in exactly these two places and nowhere else.</summary>
    private const double DesignerRailWidth = 56;

    private void OnInspectorSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        try
        {
            _viewModel.SettingsStore.Current.Workspace.InspectorWidth = InspectorColumn.ActualWidth;
            _viewModel.SettingsStore.Save();
        }
        catch (Exception ex)
        {
            // Panel sizing is a convenience; failing to remember it must never interrupt the session.
            Log.Warning(ex, "Failed to persist inspector width");
        }
    }

    private void OnAssistantResized(object? sender, AssistantSizeChangedEventArgs e)
    {
        try
        {
            var workspace = _viewModel.SettingsStore.Current.Workspace;
            workspace.AssistantWidth = e.Width;
            workspace.AssistantHeight = e.Height;
            _viewModel.SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist Kamil assistant size");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RestoreWorkspaceLayout();

        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(800, workArea.Width - 16));
        MinHeight = Math.Min(MinHeight, Math.Max(560, workArea.Height - 16));
        if (Width > workArea.Width) Width = workArea.Width;
        if (Height > workArea.Height) Height = workArea.Height;

        PromptRecoverySnapshot();

        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero", "onboarding-seen");
        if (File.Exists(marker)) return;

        var onboarding = new OnboardingWindow { Owner = this };
        onboarding.ShowDialog();
        if (onboarding.DontShowAgainChecked)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
    }

    private void PromptRecoverySnapshot()
    {
        if (_viewModel.HasRecoverySnapshot)
        {
            var restored = LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Nalezena záloha projektu",
                "LASERO našlo automatickou zálohu neuložené práce. Můžete pokračovat tam, kde jste skončili.",
                "Obnovit projekt",
                SecondaryText: "Zahodit zálohu",
                CancelText: null,
                Tone: LaseroDialogTone.Information));
            if (restored == LaseroDialogChoice.Primary)
                _viewModel.TryRestoreRecoverySnapshot();
            else if (restored == LaseroDialogChoice.Secondary)
                _viewModel.DiscardRecoverySnapshot();
        }
    }
}
