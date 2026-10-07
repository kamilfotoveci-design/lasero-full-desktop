using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Views.DeviceSetup;

/// <summary>
/// Hosts the device connection flow as an in-window overlay, replacing the old modal
/// <c>DeviceWizardWindow</c>. Follows the same architectural pattern
/// <see cref="Lasero.App.Views.Kamil.KamilAssistantHost"/> established: one persistent surface
/// declared in MainWindow.xaml, with every animation state → geometry decision made here in
/// code-behind rather than in the view-model, and none of the connection logic duplicated — every
/// command this view raises belongs to the real <see cref="DeviceWizardViewModel"/>.
/// </summary>
public partial class DeviceWizardOverlay : UserControl
{
    private static readonly TimeSpan ShapeDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LayerDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LayerBeginTimeIn = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan CheckPopDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CapabilityRowDuration = TimeSpan.FromMilliseconds(180);
    private const double CapabilityRowStaggerMs = 30;

    private DeviceWizardViewModel? _viewModel;
    private IInputElement? _focusBeforeOpening;

    /// <summary>Raised when the operator asks for device settings from the Ready state.
    /// MainWindow owns the actual settings window, the same way it owns every other dialog this
    /// overlay has no business knowing about.</summary>
    public event EventHandler? SettingsRequested;

    public DeviceWizardOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Honours the system's "show animations in Windows" setting and the graphics tier,
    /// exactly like <see cref="Lasero.App.Views.Kamil.KamilAssistantHost"/> does — the state change
    /// itself is identical either way, only the motion is skipped.</summary>
    private static bool AnimationsEnabled => Lasero.App.Controls.Motion.LaseroMotion.AnimationsEnabled;

    /// <summary>Opens the overlay with a fresh wizard instance. A new <see cref="DeviceWizardViewModel"/>
    /// per run is deliberate — see <c>MainViewModel.CreateDeviceWizard</c> — so reopening always starts
    /// over rather than resuming wherever the operator left it.</summary>
    public void Show(DeviceWizardViewModel viewModel)
    {
        DetachViewModel();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _focusBeforeOpening ??= Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        ApplyStep(_viewModel.Step, animate: false);
        AnimateOpen();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Focus()));
        if (viewModel.AutoStartRequested)
        {
            viewModel.AutoStartRequested = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (ReferenceEquals(_viewModel, viewModel) && viewModel.AutoConnectCommand.CanExecute(null))
                    viewModel.AutoConnectCommand.Execute(null);
            }));
        }
    }

    /// <summary>Esc, and the close button, both funnel through here. If a scan or a connection
    /// attempt is genuinely in flight it is cancelled first — reusing the wizard's own
    /// <c>CancelScanCommand</c> and the connection's own <c>DisconnectCommand</c> — rather than
    /// leaving a port open behind a closed overlay.</summary>
    private void RequestClose()
    {
        if (_viewModel is null)
        {
            HideImmediate();
            return;
        }

        if (_viewModel.IsScanning && _viewModel.CancelScanCommand.CanExecute(null))
            _viewModel.CancelScanCommand.Execute(null);

        if (_viewModel.UseSelectedMachineCommand.IsRunning && _viewModel.Connection.DisconnectCommand.CanExecute(null))
            _viewModel.Connection.DisconnectCommand.Execute(null);

        AnimateClose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => RequestClose();

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnWorkAreaEdited(object sender, RoutedEventArgs e) => _viewModel?.NotifyWorkAreaEdited();

    /// <summary>Esc steps out of the overlay entirely rather than one step back — unlike Kamil's
    /// three shapes, there is no shallower "minimized" form to fall back to here.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || Visibility != Visibility.Visible) return;
        e.Handled = true;
        RequestClose();
    }

    private void DetachViewModel()
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceWizardViewModel.Step) || _viewModel is null) return;
        ApplyStep(_viewModel.Step, animate: true);
    }

    // ------------------------------------------------------------------
    // Whole-sheet open/close
    // ------------------------------------------------------------------

    private void AnimateOpen()
    {
        if (!AnimationsEnabled)
        {
            ClearSheetAnimations();
            Scrim.Opacity = 1;
            Sheet.Opacity = 1;
            SheetScale.ScaleX = 1;
            SheetScale.ScaleY = 1;
            SheetOffset.Y = 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        Sheet.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.98,
            To = 1,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.98,
            To = 1,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        SheetOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private void AnimateClose()
    {
        if (!AnimationsEnabled)
        {
            HideImmediate();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var sheetFade = new DoubleAnimation { To = 0, Duration = CloseDuration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        sheetFade.Completed += (_, _) => HideImmediate();

        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0, Duration = CloseDuration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        Sheet.BeginAnimation(OpacityProperty, sheetFade);
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation { To = 0.98, Duration = CloseDuration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation { To = 0.98, Duration = CloseDuration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    private void ClearSheetAnimations()
    {
        Scrim.BeginAnimation(OpacityProperty, null);
        Sheet.BeginAnimation(OpacityProperty, null);
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        SheetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        SheetOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
    }

    /// <summary>The concentric rings around the magnifier breathe while the port search and GRBL
    /// handshake are running, and stop the moment the step changes (found, not found, cancelled) or the
    /// overlay closes. This is real activity, which is what the Motion.Breathe token is reserved for, and
    /// an intentional exception to the app's no-decorative-animation rule. With system animations off
    /// the rings stay as static rings.</summary>
    private void UpdateScanPulse(bool scanning)
    {
        if (scanning && AnimationsEnabled)
        {
            var duration = (Duration)FindResource("Motion.Breathe");
            var ease = (IEasingFunction)FindResource("Ease.Breathe");
            ScanRingOuter.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.35, To = 0.08, Duration = duration, EasingFunction = ease,
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
            ScanRingInner.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.5, To = 0.15, Duration = duration, EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(300),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            ScanRingOuter.BeginAnimation(OpacityProperty, null);
            ScanRingInner.BeginAnimation(OpacityProperty, null);
        }
    }

    private void HideImmediate()
    {
        UpdateScanPulse(false);
        Visibility = Visibility.Collapsed;
        DetachViewModel();
        DataContext = null;
        RestoreFocus();
    }

    private void RestoreFocus()
    {
        var target = _focusBeforeOpening;
        _focusBeforeOpening = null;
        if (target is not UIElement { IsVisible: true }) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(target)));
    }

    // ------------------------------------------------------------------
    // Step → layer crossfade
    // ------------------------------------------------------------------

    private void ApplyStep(DeviceWizardStep step, bool animate)
    {
        SetLayer(IntroLayer, IntroOffset, step == DeviceWizardStep.Intro, animate);
        SetLayer(ScanningLayer, ScanningOffset, step == DeviceWizardStep.Scanning, animate);
        SetLayer(ResultsLayer, ResultsOffset, step == DeviceWizardStep.Results, animate);
        SetLayer(SetupLayer, SetupOffset, step == DeviceWizardStep.Setup, animate);
        SetLayer(DoneLayer, DoneOffset, step == DeviceWizardStep.Done, animate);
        UpdateScanPulse(step == DeviceWizardStep.Scanning);

        if (step == DeviceWizardStep.Setup)
        {
            AnimateCheckPop(SetupCheckScale, animate);
            // After the layer's own opacity/measure pass has settled — see the identical reasoning
            // in KamilAssistantHost.RepositionForLayout for why Loaded priority is used here.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => StaggerCapabilityRows(animate)));
        }
        else if (step == DeviceWizardStep.Done)
        {
            AnimateCheckPop(DoneCheckScale, animate);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Focus()));
    }

    /// <summary>Crossfades one step's content into or out of view. Every call replaces whatever
    /// animation was already running on these two properties via <c>BeginAnimation</c>, so a state
    /// change mid-transition wins immediately rather than leaving a stale Storyboard fighting the
    /// new one — the same discipline <c>KamilAssistantHost.ApplyState</c> uses.</summary>
    private static void SetLayer(FrameworkElement layer, System.Windows.Media.TranslateTransform offset, bool visible, bool animate)
    {
        if (!animate || !AnimationsEnabled)
        {
            layer.BeginAnimation(OpacityProperty, null);
            offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            layer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            layer.Opacity = visible ? 1 : 0;
            offset.Y = 0;
            return;
        }

        if (visible) layer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var opacityAnimation = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = LayerDuration,
            BeginTime = visible ? LayerBeginTimeIn : TimeSpan.Zero,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (!visible)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (layer.Opacity <= 0.01) layer.Visibility = Visibility.Collapsed;
            };
        }
        layer.BeginAnimation(OpacityProperty, opacityAnimation);

        offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = visible ? 8 : 0,
            To = visible ? 0 : -4,
            Duration = LayerDuration,
            BeginTime = visible ? LayerBeginTimeIn : TimeSpan.Zero,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    /// <summary>The small "connected"/"ready" check mark's one entrance: a scale draw-in from half
    /// size, never a bounce. Used for both the inline Setup confirmation and the larger Done glyph.</summary>
    private static void AnimateCheckPop(System.Windows.Media.ScaleTransform scale, bool animate)
    {
        if (!animate || !AnimationsEnabled)
        {
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = CheckPopDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = CheckPopDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    /// <summary>A light stagger reveal for whichever confirmed-capability rows are actually visible
    /// (Firmware is conditional — see the row's own Visibility binding in XAML). Only real,
    /// already-detected values ever reach these rows; this only decides the order they fade in.</summary>
    private void StaggerCapabilityRows(bool animate)
    {
        var rows = new[] { CapabilityRowMachine, CapabilityRowWorkArea, CapabilityRowFirmware };
        var delayMs = 0d;

        foreach (var row in rows)
        {
            if (row.Visibility != Visibility.Visible) continue;

            row.BeginAnimation(OpacityProperty, null);
            if (!animate || !AnimationsEnabled)
            {
                row.Opacity = 1;
                continue;
            }

            row.Opacity = 0;
            var offset = new System.Windows.Media.TranslateTransform();
            row.RenderTransform = offset;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var beginTime = TimeSpan.FromMilliseconds(delayMs);

            row.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = CapabilityRowDuration,
                BeginTime = beginTime,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            });
            offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 6,
                To = 0,
                Duration = CapabilityRowDuration,
                BeginTime = beginTime,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            });
            delayMs += CapabilityRowStaggerMs;
        }
    }
}
