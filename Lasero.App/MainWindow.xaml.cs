using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Shell;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Scene;
using Serilog;

namespace Lasero.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autosaveTimer;
    private MaterialsWindow? _materialsWindow;
    private PreviewWindow? _previewWindow;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.GCode.SimulationStarted += OnSimulationStarted;
        _viewModel.Scene.TraceRasterRequested += OnTraceRasterRequested;
        _viewModel.Scene.VectorOperationRejected += OnVectorOperationRejected;
        DesignerCanvas.TextPlacementRequested += OnTextPlacementRequested;
        Loaded += OnLoaded;
        Closing += OnClosing;
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

    private void OnTransformFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox field) return;

        // TextBox bindings normally commit only after focus leaves the field. Dimension editing in
        // a desktop design tool is keyboard-driven, so Enter must commit immediately and keep the
        // field ready for another precise value.
        field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        field.SelectAll();
        e.Handled = true;
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

        _autosaveTimer.Stop();
        if (!_viewModel.IsDirty) _viewModel.DiscardRecoverySnapshot();
        _viewModel.SaveSettings();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.SignOutRequested) return;

        Hide();
        var login = new LoginWindow(_viewModel.Account) { Owner = this };
        if (login.ShowDialog() == true)
        {
            _viewModel.Chat.InitializeForCurrentAccount();
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

    private void OnTextPlacementRequested(Position position)
    {
        var dialog = new TextToolWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _viewModel.Scene.AddText(dialog.TextValue, position, dialog.HeightMm);
            _viewModel.Scene.ActiveTool = DesignerTool.Select;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create vector text");
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Text se nepodařilo vytvořit",
                "Text nelze převést na vektorové křivky. Zkuste kratší text, jiný font nebo menší velikost.",
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

    private void OnVectorOperationRejected(string message) =>
        LaseroDialogWindow.Show(this, new LaseroDialogOptions(
            "Vektory nelze bezpečně sjednotit",
            message,
            "Rozumím",
            CancelText: null,
            Tone: LaseroDialogTone.Warning));

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
        => OpenMaterials();

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

    // SceneCanvas.ZoomIn/ZoomOut/FitToView have always been public — nothing in this window ever
    // called them (the canvas element had no x:Name), so the "Zobrazenie 100 %" toolbar text has
    // always just been a hardcoded, dead label. These three handlers are the first real callers.
    private void OnZoomInClick(object sender, RoutedEventArgs e) => DesignerCanvas.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => DesignerCanvas.ZoomOut();
    private void OnFitToViewClick(object sender, RoutedEventArgs e) => DesignerCanvas.FitToView();

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
    private void RestoreWorkspaceLayout()
    {
        InspectorColumn.Width = new GridLength(_viewModel.SettingsStore.Current.Workspace.ClampedInspectorWidth);
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
