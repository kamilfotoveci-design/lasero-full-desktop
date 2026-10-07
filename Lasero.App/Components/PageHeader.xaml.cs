using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>The shared screen header, see PageHeader.xaml.</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, (d, e) =>
            ((PageHeader)d).SubtitleText.Visibility = string.IsNullOrWhiteSpace((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty LeadingProperty = DependencyProperty.Register(
        nameof(Leading), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => ((PageHeader)d).LeadingHost.Margin = new Thickness(0, 0, e.NewValue is null ? 0 : 16, 0)));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) => ((PageHeader)d).ActionsHost.Margin = new Thickness(e.NewValue is null ? 0 : 24, 0, 0, 0)));

    public static readonly DependencyProperty NextStepProperty = DependencyProperty.Register(
        nameof(NextStep), typeof(string), typeof(PageHeader),
        new PropertyMetadata(null, (d, e) =>
            ((PageHeader)d).NextStepText.Visibility = string.IsNullOrWhiteSpace((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible));

    public PageHeader() => InitializeComponent();

    /// <summary>The quiet "Další krok: ..." line under the subtitle. Empty hides it. One per screen, never a tip.</summary>
    public string? NextStep { get => (string?)GetValue(NextStepProperty); set => SetValue(NextStepProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>An identity image before the title (Home only).</summary>
    public object? Leading { get => GetValue(LeadingProperty); set => SetValue(LeadingProperty, value); }

    /// <summary>The header's single primary action, plus at most one quiet companion, right aligned.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}
