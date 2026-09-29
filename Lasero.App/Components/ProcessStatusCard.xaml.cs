using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lasero.App.Components;

/// <summary>
/// One surface for every long-running machine operation: connecting, reading the controller,
/// measuring the bed, estimating a job, framing, sending, engraving, and each of their outcomes.
/// <para>
/// It answers the five questions an operator has about a machine that is busy — what is happening,
/// how far along, is it all right, what do I do next, can I stop it — and it answers them the same
/// way everywhere, so the answer does not have to be relearned per screen.
/// </para>
/// <para>
/// It has no state of its own. Everything is bound, including progress, so the card cannot claim the
/// machine got further than it did.
/// </para>
/// </summary>
public partial class ProcessStatusCard : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(ProcessStatus), typeof(ProcessStatusCard),
        new PropertyMetadata(ProcessStatus.Idle, OnStatusChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ProcessStatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(ProcessStatusCard), new PropertyMetadata(null));

    /// <summary>Percent complete, or null when the app genuinely cannot tell. Null shows an
    /// indeterminate bar rather than a fabricated number.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double?), typeof(ProcessStatusCard),
        new PropertyMetadata(null, OnProgressShapeChanged));

    /// <summary>Show a bar at all. A card reporting an outcome does not need one.</summary>
    public static readonly DependencyProperty ShowsProgressProperty = DependencyProperty.Register(
        nameof(ShowsProgress), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(ProcessStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty MetaProperty = DependencyProperty.Register(
        nameof(Meta), typeof(object), typeof(ProcessStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel), typeof(string), typeof(ProcessStatusCard),
        new PropertyMetadata(null, OnActionsChanged));

    public static readonly DependencyProperty PrimaryCommandProperty = DependencyProperty.Register(
        nameof(PrimaryCommand), typeof(ICommand), typeof(ProcessStatusCard), new PropertyMetadata(null));

    /// <summary>Paints the primary action red. For stopping or aborting a running machine, and
    /// nothing else — this is the app's one licence to use red on a button.</summary>
    public static readonly DependencyProperty PrimaryIsDangerProperty = DependencyProperty.Register(
        nameof(PrimaryIsDanger), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(false));

    public static readonly DependencyProperty SecondaryLabelProperty = DependencyProperty.Register(
        nameof(SecondaryLabel), typeof(string), typeof(ProcessStatusCard),
        new PropertyMetadata(null, OnActionsChanged));

    public static readonly DependencyProperty SecondaryCommandProperty = DependencyProperty.Register(
        nameof(SecondaryCommand), typeof(ICommand), typeof(ProcessStatusCard), new PropertyMetadata(null));

    /// <summary>Outlines the secondary action in red. Stopping a running machine is destructive but it
    /// is not the dominant action while the job is going well — pausing is — so it needs to read as
    /// dangerous from the quiet slot.</summary>
    public static readonly DependencyProperty SecondaryIsDangerProperty = DependencyProperty.Register(
        nameof(SecondaryIsDanger), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(false));

    public static readonly DependencyProperty DismissCommandProperty = DependencyProperty.Register(
        nameof(DismissCommand), typeof(ICommand), typeof(ProcessStatusCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ProgressValuePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ProgressValue), typeof(double), typeof(ProcessStatusCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty ProgressValueProperty = ProgressValuePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsProgressIndeterminatePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsProgressIndeterminate), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(true));

    public static readonly DependencyProperty IsProgressIndeterminateProperty =
        IsProgressIndeterminatePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasActionsPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasActions), typeof(bool), typeof(ProcessStatusCard), new PropertyMetadata(false));

    public static readonly DependencyProperty HasActionsProperty = HasActionsPropertyKey.DependencyProperty;

    private readonly SolidColorBrush _chipBrush = new(Color.FromRgb(0xF3, 0xF4, 0xF2));
    private readonly SolidColorBrush _glyphBrush = new(Color.FromRgb(0x92, 0x97, 0x93));
    private bool _colorsInitialized;

    public ProcessStatusCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Same reduced-motion check already established by KamilAssistantHost and
    /// DeviceWizardOverlay - applied here too rather than inventing a second mechanism.</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation
        && RenderCapability.Tier > 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChipSurface.Background = _chipBrush;
        StatusGlyphIcon.Foreground = _glyphBrush;
        _colorsInitialized = true;
        ApplyStatusColors(Status, animate: false);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProcessStatusCard card || !card._colorsInitialized) return;
        card.ApplyStatusColors((ProcessStatus)e.NewValue, animate: true);
    }

    /// <summary>
    /// The chip tint and glyph tint move together in a short colour transition instead of the instant
    /// swap the old per-status Background/Foreground triggers produced - a calm cue for "the machine's
    /// situation just changed" rather than a flash. Runs on a local, per-instance SolidColorBrush (see
    /// the class-level fields) because animating a shared DynamicResource brush's Color would leak
    /// across every other consumer of that brush, the same reasoning already documented on the Button
    /// template in SharedUiStyles.xaml.
    ///
    /// The colours mirror Brush.Field/InfoMuted/SuccessMuted/WarningMuted/DangerMuted and
    /// Brush.TextMuted/AccentText/Success/Warning/Danger in LaseroTheme.xaml. There is no runtime
    /// theme switcher in this app today; if one is added later this mapping needs to move to a
    /// resource lookup instead of these literals.
    /// </summary>
    private void ApplyStatusColors(ProcessStatus status, bool animate)
    {
        var (chip, glyph) = status switch
        {
            ProcessStatus.Progress => (Color.FromRgb(0xEF, 0xF6, 0xFF), Color.FromRgb(0x25, 0x63, 0xEB)),
            ProcessStatus.Success => (Color.FromRgb(0xF0, 0xFD, 0xF4), Color.FromRgb(0x15, 0x80, 0x3D)),
            ProcessStatus.Warning => (Color.FromRgb(0xFF, 0xFB, 0xEB), Color.FromRgb(0xD9, 0x77, 0x06)),
            ProcessStatus.Error => (Color.FromRgb(0xFE, 0xF2, 0xF2), Color.FromRgb(0xDC, 0x26, 0x26)),
            _ => (Color.FromRgb(0xF3, 0xF4, 0xF2), Color.FromRgb(0x92, 0x97, 0x93)),
        };

        if (!animate || !AnimationsEnabled)
        {
            _chipBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _glyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _chipBrush.Color = chip;
            _glyphBrush.Color = glyph;
        }
        else
        {
            var duration = (Duration)FindResource("Motion.Base");
            var ease = (CubicEase)FindResource("Ease.Out");
            _chipBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = chip, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
            });
            _glyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = glyph, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
            });
        }

        AnimateContentCrossfade(animate);
    }

    /// <summary>A soft reveal on the title/description, not a real hide-then-show - the bound text
    /// has already changed underneath by the time this runs, so dropping opacity to 0 would just be a
    /// blank flash. Dipping partway and easing back up is the crossfade cue the spec asks for without
    /// ever showing an empty state mid-transition (state must always be visible, per the discipline
    /// KamilAssistantHost.ApplyState already follows).</summary>
    private void AnimateContentCrossfade(bool animate)
    {
        if (!animate || !AnimationsEnabled)
        {
            ContentStack.BeginAnimation(OpacityProperty, null);
            ContentStack.Opacity = 1;
            return;
        }

        var ease = (CubicEase)FindResource("Ease.Out");
        ContentStack.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = (Duration)FindResource("Motion.Base"),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
    public ProcessStatus Status
    {
        get => (ProcessStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public double? Progress
    {
        get => (double?)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool ShowsProgress
    {
        get => (bool)GetValue(ShowsProgressProperty);
        set => SetValue(ShowsProgressProperty, value);
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public object? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public string? PrimaryLabel
    {
        get => (string?)GetValue(PrimaryLabelProperty);
        set => SetValue(PrimaryLabelProperty, value);
    }

    public ICommand? PrimaryCommand
    {
        get => (ICommand?)GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public bool PrimaryIsDanger
    {
        get => (bool)GetValue(PrimaryIsDangerProperty);
        set => SetValue(PrimaryIsDangerProperty, value);
    }

    public string? SecondaryLabel
    {
        get => (string?)GetValue(SecondaryLabelProperty);
        set => SetValue(SecondaryLabelProperty, value);
    }

    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    public bool SecondaryIsDanger
    {
        get => (bool)GetValue(SecondaryIsDangerProperty);
        set => SetValue(SecondaryIsDangerProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public double ProgressValue => (double)GetValue(ProgressValueProperty);
    public bool IsProgressIndeterminate => (bool)GetValue(IsProgressIndeterminateProperty);
    public bool HasActions => (bool)GetValue(HasActionsProperty);

    private static void OnProgressShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ProcessStatusCard)d;
        var progress = (double?)e.NewValue;
        var known = progress is { } value && double.IsFinite(value);
        card.SetValue(IsProgressIndeterminatePropertyKey, !known);
        card.SetValue(ProgressValuePropertyKey, known ? Math.Clamp(progress!.Value, 0, 100) : 0d);
    }

    private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ProcessStatusCard)d;
        card.SetValue(HasActionsPropertyKey,
            !string.IsNullOrWhiteSpace(card.PrimaryLabel) || !string.IsNullOrWhiteSpace(card.SecondaryLabel));
    }
}
