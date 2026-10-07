using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lasero.App.Controls.Motion;

namespace Lasero.App.Components;

/// <summary>The Home "Tip dne" card: shows <see cref="Tip"/> and runs <see cref="NextCommand"/> from its
/// quiet "Další tip" action. It owns only the presentation, including the change animation.</summary>
public partial class TipOfDayCard : UserControl
{
    private const double SlideDistance = 6;

    public static readonly DependencyProperty TipProperty = DependencyProperty.Register(
        nameof(Tip), typeof(string), typeof(TipOfDayCard),
        new PropertyMetadata(string.Empty, (d, e) => ((TipOfDayCard)d).OnTipChanged((string?)e.OldValue, (string?)e.NewValue)));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(int), typeof(TipOfDayCard), new PropertyMetadata(0, (d, _) => ((TipOfDayCard)d).UpdateProgress()));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(TipOfDayCard), new PropertyMetadata(0, (d, _) => ((TipOfDayCard)d).UpdateProgress()));

    public static readonly DependencyProperty NextCommandProperty = DependencyProperty.Register(
        nameof(NextCommand), typeof(ICommand), typeof(TipOfDayCard), new PropertyMetadata(null));

    public TipOfDayCard() => InitializeComponent();

    public string Tip
    {
        get => (string)GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    /// <summary>1-based place of the tip in the rotation; with <see cref="Count"/> it reads "3 z 12".</summary>
    public int Position { get => (int)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    private void UpdateProgress()
    {
        var show = Position > 0 && Count > 1;
        Progress.Text = show ? $"{Position} z {Count}" : string.Empty;
        Progress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public ICommand? NextCommand
    {
        get => (ICommand?)GetValue(NextCommandProperty);
        set => SetValue(NextCommandProperty, value);
    }

    private void OnTipChanged(string? oldTip, string? newTip)
    {
        EndAnimation();
        TipText.Text = newTip ?? string.Empty;
        if (!IsLoaded || !LaseroMotion.AnimationsEnabled || string.IsNullOrEmpty(oldTip)) return;

        var duration = (Duration)FindResource("Motion.Base");
        var ease = (IEasingFunction)FindResource("Ease.Out");

        TipOut.Text = oldTip;
        TipOut.Visibility = Visibility.Visible;
        var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) => TipOut.Visibility = Visibility.Collapsed;
        TipOut.BeginAnimation(OpacityProperty, fadeOut);

        TipText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        TipOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(SlideDistance, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void EndAnimation()
    {
        TipOut.BeginAnimation(OpacityProperty, null);
        TipText.BeginAnimation(OpacityProperty, null);
        TipOffset.BeginAnimation(TranslateTransform.YProperty, null);
        TipOut.Visibility = Visibility.Collapsed;
    }
}
