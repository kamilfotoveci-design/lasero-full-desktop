using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shell;
using Lasero.App.ViewModels;
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
    private MachineControlWindow? _machineControlWindow;
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
        _viewModel.Scene.TraceRasterRequested += OnTraceRasterRequested;
        _viewModel.Scene.BackgroundRemovalRequested += OnBackgroundRemovalRequested;
        _viewModel.Scene.OffsetRequested += OnOffsetRequested;
        _viewModel.Scene.VectorOperationRejected += OnVectorOperationRejected;
        _viewModel.DeviceWizardRequested += OpenDeviceWizard;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DesignerCanvas.TextPlacementRequested += OnTextPlacementRequested;
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
        var dialog = new SettingsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.SignOutRequested) return;

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
        }
        else
        {
            Close();
        }
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

        var offsetViewModel = new OffsetPathViewModel(sources);
        var window = new OffsetPathWindow(offsetViewModel) { Owner = this };
        if (window.ShowDialog() != true) return;

        _viewModel.Scene.ApplyOffset(sources, offsetViewModel.ResultsBySource);
        if (_viewModel.GCode.RegenerateFromSceneCommand.CanExecute(null))
            _viewModel.GCode.RegenerateFromSceneCommand.Execute(null);
        _viewModel.GCode.LastMessage = "Offset byl použit. Změnu lze vrátit pomocí Ctrl+Z.";
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
        => OpenMaterials();

    /// <summary>
    /// Navigates to the Chat screen and, if Kamil was previously closed (see
    /// KamilAssistantViewModel.Close), reopens it on the Minimized badge. A single Command binding
    /// could only ever do one of the two, and Phase 1 flagged the gap this left: closing Kamil and
    /// then clicking this same rail button navigated to Chat but never brought the assistant back,
    /// leaving no way to reopen it short of an app restart. ShowCommand is already guarded by CanShow
    /// (State == Hidden), so firing it unconditionally here is safe whether or not there is anything
    /// to reopen.
    /// </summary>
    private void OnLaseroChatClick(object sender, RoutedEventArgs e)
    {
        OpenKamilInDesigner();
    }

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

    /// <summary>The Designer inspector's "Ovládání stroje" route. One instance, reactivated rather
    /// than reopened, and non-modal so the operator can jog while watching the artwork.</summary>
    public void OpenMachineControl()
    {
        if (_machineControlWindow is not null)
        {
            _machineControlWindow.Activate();
            return;
        }

        _machineControlWindow = new MachineControlWindow(_viewModel) { Owner = this };
        _machineControlWindow.Closed += (_, _) => _machineControlWindow = null;
        _machineControlWindow.PositionBeside(this);
        _machineControlWindow.Show();
    }

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
        if (e.PropertyName == nameof(MainViewModel.IsNavCollapsed))
        {
            ApplyNavRailWidth(_viewModel.IsNavCollapsed, animate: true);
            return;
        }

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
        // Restored without animating: the saved posture is where the window starts, not something
        // the operator just asked for.
        _viewModel.IsNavCollapsed = _viewModel.SettingsStore.Current.Workspace.IsNavCollapsed;
        ApplyNavRailWidth(_viewModel.IsNavCollapsed, animate: false);
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
    /// Animates the navigation rail between its two widths.
    ///
    /// The animation is on the Border's Width and the column is Auto, because GridLength has no
    /// built-in animation and writing a GridLengthAnimation to avoid one layout pass per frame is not
    /// worth it for one panel over 180ms.
    ///
    /// 180ms with a cubic ease is inside the 150-300ms band where a transition reads as the panel
    /// moving rather than as the app stalling; ease-out on the way open and ease-in on the way shut so
    /// the motion settles where the eye is going to end up. Windows' own animation setting is
    /// honoured: with it off the rail simply arrives at the new width, which is also what happens on
    /// the very first layout so the saved state does not animate in at startup.
    /// </summary>
    /// <summary>
    /// Swaps the left column between the full application sidebar and the Designer's 56px rail.
    ///
    /// Deliberately not folded into IsNavCollapsed. That flag is the operator's own posture, saved
    /// across sessions; the editor's narrow rail is a property of the screen. Overloading one onto
    /// the other would mean opening the editor silently rewrote a preference, and leaving it would
    /// then collapse Home too.
    ///
    /// The two rails are separate controls and exactly one is visible, so nothing about the sidebar's
    /// collapse, animation or peek behaviour runs while Designer is showing.
    /// </summary>
    private void ApplyWorkspaceMode()
    {
        var designer = _viewModel.CurrentScreen == AppScreen.Designer;

        NavRailHost.Visibility = designer ? Visibility.Collapsed : Visibility.Visible;
        DesignerRail.Visibility = designer ? Visibility.Visible : Visibility.Collapsed;

        if (designer)
        {
            // No animation between screens: switching to the editor is a navigation, and sliding the
            // rail would make it read as a panel opening rather than a different screen arriving.
            NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
            NavColumn.Width = new GridLength(DesignerRailWidth);
            return;
        }

        ApplyNavRailWidth(_viewModel.IsNavCollapsed, animate: false);
    }

    /// <summary>Matches DesignerToolRail's own Width. The rail is a fixed strip, not a resizable
    /// panel, so the number lives in exactly these two places and nowhere else.</summary>
    private const double DesignerRailWidth = 56;

    private void ApplyNavRailWidth(bool collapsed, bool animate)
    {
        // Designer owns the column while it is showing; a posture change made from a dialog must not
        // reach in and resize the editor's rail underneath it.
        if (_viewModel.CurrentScreen == AppScreen.Designer) return;

        // The column is set rather than animated: it defines the canvas's slot, and animating both
        // would have the workspace relayout on every frame for no visible gain. The rail's own width is
        // what the eye follows.
        NavColumn.Width = new GridLength(collapsed
            ? WorkspacePreferences.CollapsedNavWidth
            : WorkspacePreferences.ExpandedNavWidth);

        var target = collapsed ? WorkspacePreferences.CollapsedNavWidth : WorkspacePreferences.ExpandedNavWidth;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
            NavRail.Width = target;
            return;
        }

        var slide = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = collapsed ? EasingMode.EaseIn : EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        NavRail.BeginAnimation(FrameworkElement.WidthProperty, slide);
    }

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RestoreWorkspaceLayout();

        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(800, workArea.Width - 16));
        MinHeight = Math.Min(MinHeight, Math.Max(560, workArea.Height - 16));
        if (Width > workArea.Width) Width = workArea.Width;
        if (Height > workArea.Height) Height = workArea.Height;

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
}
