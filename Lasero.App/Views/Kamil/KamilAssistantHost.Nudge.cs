using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.Controls.Motion;
using Lasero.App.ViewModels;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// The occasional speech bubble from Kamil's head ("Pomoc je po ruce. Stačí se zeptat."). It is an offer,
/// never an interruption: only while the assistant is minimized, the window is active and no job is
/// running, a few minutes apart, at most three times per run, and it leaves by itself after a few
/// seconds. A click opens Quick Ask; any state change dismisses it.
/// </summary>
public partial class KamilAssistantHost
{
    private const int MaxNudgesPerRun = 3;
    private static readonly TimeSpan FirstNudgeDelay = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NudgeInterval = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan NudgeVisibleFor = TimeSpan.FromSeconds(9);

    private DispatcherTimer? _nudgeTimer;
    private DispatcherTimer? _nudgeHideTimer;
    private int _nudgeCount;

    private void StartNudgeTimer()
    {
        if (_nudgeTimer is not null) return;
        _nudgeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = FirstNudgeDelay };
        _nudgeTimer.Tick += OnNudgeTick;
        _nudgeTimer.Start();
    }

    private void StopNudgeTimer()
    {
        _nudgeTimer?.Stop();
        _nudgeTimer = null;
        HideNudge(immediate: true);
    }

    private void OnNudgeTick(object? sender, EventArgs e)
    {
        if (_nudgeTimer is null) return;
        _nudgeTimer.Interval = NudgeInterval;
        if (_nudgeCount >= MaxNudgesPerRun) { _nudgeTimer.Stop(); return; }
        if (!CanNudgeNow()) return;
        _nudgeCount++;
        ShowNudge();
    }

    private bool CanNudgeNow()
    {
        if (_viewModel is not { State: KamilAssistantState.Minimized } viewModel) return false;
        if (Visibility != Visibility.Visible || Window.GetWindow(this) is not { IsActive: true, WindowState: not WindowState.Minimized }) return false;
        if (viewModel.Chat.IsBusy) return false;
        // Never while a job is moving the machine or being prepared: the operator is watching that.
        var job = viewModel.ShellContextProvider?.Invoke().JobState;
        return string.IsNullOrEmpty(job) || job is "Idle" or "Ready" or "Completed" or "Cancelled" or "Failed";
    }

    private void ShowNudge()
    {
        NudgeBubble.Visibility = Visibility.Visible;
        NudgeBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PositionNudge();

        if (LaseroMotion.AnimationsEnabled)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(420);
            NudgeBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            NudgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, duration) { EasingFunction = ease });
            NudgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, duration) { EasingFunction = ease });
        }
        else
        {
            NudgeBubble.BeginAnimation(OpacityProperty, null);
            NudgeBubble.Opacity = 1;
        }

        _nudgeHideTimer?.Stop();
        _nudgeHideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = NudgeVisibleFor };
        _nudgeHideTimer.Tick += (_, _) => HideNudge(immediate: false);
        _nudgeHideTimer.Start();
    }

    /// <summary>Left of the head, vertically centred on it, kept inside the host.</summary>
    private void PositionNudge()
    {
        if (NudgeBubble.Visibility != Visibility.Visible) return;
        var left = Canvas.GetLeft(PersistentAvatarLayer);
        var top = Canvas.GetTop(PersistentAvatarLayer);
        if (double.IsNaN(left) || double.IsNaN(top)) return;
        var size = NudgeBubble.DesiredSize;
        Canvas.SetLeft(NudgeBubble, Math.Max(0, left - size.Width - 8));
        Canvas.SetTop(NudgeBubble, Math.Max(0, top + (PillHeight - size.Height) / 2));
    }

    private void HideNudge(bool immediate)
    {
        _nudgeHideTimer?.Stop();
        _nudgeHideTimer = null;
        if (NudgeBubble is null || NudgeBubble.Visibility != Visibility.Visible) return;
        if (immediate || !LaseroMotion.AnimationsEnabled)
        {
            NudgeBubble.BeginAnimation(OpacityProperty, null);
            NudgeBubble.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (_nudgeHideTimer is null) NudgeBubble.Visibility = Visibility.Collapsed;
        };
        NudgeBubble.BeginAnimation(OpacityProperty, fade);
    }

    private void OnNudgeClick(object sender, MouseButtonEventArgs e)
    {
        HideNudge(immediate: true);
        _viewModel?.OpenQuickAskCommand.Execute(null);
        e.Handled = true;
    }
}
