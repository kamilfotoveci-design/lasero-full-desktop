using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lasero.App.Tour;

/// <summary>Shows <see cref="GuidanceService.CurrentTip"/>. It only displays: whether a tip may appear at
/// all is decided by the service. The chip hides itself after <see cref="AutoHide"/>, except while the
/// pointer rests on it.</summary>
public partial class TipChip : UserControl
{
    public static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(16);

    private readonly DispatcherTimer _timer = new() { Interval = AutoHide };
    private GuidanceService? _service;

    public TipChip()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { _timer.Stop(); _service?.DismissTip(); };
        Chip.MouseEnter += (_, _) => _timer.Stop();
        Chip.MouseLeave += (_, _) => { if (Visibility == Visibility.Visible && _service?.CurrentTip is not null) _timer.Start(); };
    }

    public bool ForceStatic { get; set; }

    private bool AnimationsEnabled =>
        !ForceStatic && SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    public void Attach(GuidanceService service)
    {
        if (_service is not null) _service.TipChanged -= Refresh;
        _service = service;
        _service.TipChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var tip = _service?.CurrentTip;
        _timer.Stop();
        if (tip is null)
        {
            Hide();
            return;
        }

        TipText.Text = tip.Text;
        Show();
        _timer.Start();
    }

    private void Show()
    {
        BeginAnimation(OpacityProperty, null);
        ChipOffset.BeginAnimation(TranslateTransform.YProperty, null);
        Visibility = Visibility.Visible;
        if (!AnimationsEnabled)
        {
            Opacity = 1;
            ChipOffset.Y = 0;
            return;
        }

        var ease = TryFindResource("Ease.Out") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TryFindResource("Motion.Spatial") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(260));
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        ChipOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }

    private void Hide()
    {
        if (Visibility != Visibility.Visible) return;
        if (!AnimationsEnabled)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var ease = TryFindResource("Ease.Out") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TryFindResource("Motion.Base") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(170));
        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (_service?.CurrentTip is null) Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => _service?.DismissTip();
}
