using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>Icon + title + description + optional CTA button — replaces the 3 duplicated inline
/// empty-state StackPanels that were hand-written once each on the Home dashboard (recent projects,
/// today's jobs, materials). Leave CtaText empty/null to omit the button entirely.</summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty IconPrimaryDataProperty = DependencyProperty.Register(
        nameof(IconPrimaryData), typeof(Geometry), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty IconAccentDataProperty = DependencyProperty.Register(
        nameof(IconAccentData), typeof(Geometry), typeof(EmptyState), new PropertyMetadata(null));

    /// <summary>Passthrough to the primary icon's own IsFilled/GridSize — needed for a filled
    /// silhouette icon (e.g. Glyph.Fill.Materials) rather than the default hand-authored 24x24 stroke.</summary>
    public static readonly DependencyProperty IsIconFilledProperty = DependencyProperty.Register(
        nameof(IsIconFilled), typeof(bool), typeof(EmptyState), new PropertyMetadata(false));

    public static readonly DependencyProperty IconGridSizeProperty = DependencyProperty.Register(
        nameof(IconGridSize), typeof(double), typeof(EmptyState), new PropertyMetadata(24.0));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CtaTextProperty = DependencyProperty.Register(
        nameof(CtaText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty CtaCommandProperty = DependencyProperty.Register(
        nameof(CtaCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty CtaCommandParameterProperty = DependencyProperty.Register(
        nameof(CtaCommandParameter), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    public EmptyState()
    {
        InitializeComponent();
    }

    public Geometry? IconPrimaryData
    {
        get => (Geometry?)GetValue(IconPrimaryDataProperty);
        set => SetValue(IconPrimaryDataProperty, value);
    }

    public Geometry? IconAccentData
    {
        get => (Geometry?)GetValue(IconAccentDataProperty);
        set => SetValue(IconAccentDataProperty, value);
    }

    public bool IsIconFilled
    {
        get => (bool)GetValue(IsIconFilledProperty);
        set => SetValue(IsIconFilledProperty, value);
    }

    public double IconGridSize
    {
        get => (double)GetValue(IconGridSizeProperty);
        set => SetValue(IconGridSizeProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? CtaText
    {
        get => (string?)GetValue(CtaTextProperty);
        set => SetValue(CtaTextProperty, value);
    }

    public ICommand? CtaCommand
    {
        get => (ICommand?)GetValue(CtaCommandProperty);
        set => SetValue(CtaCommandProperty, value);
    }

    public object? CtaCommandParameter
    {
        get => GetValue(CtaCommandParameterProperty);
        set => SetValue(CtaCommandParameterProperty, value);
    }
}
