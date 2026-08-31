using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        new PropertyMetadata(ProcessStatus.Idle));

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

    public ProcessStatusCard() => InitializeComponent();

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
