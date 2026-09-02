using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>
/// Renders one <see cref="ViewModels.ParameterRecommendation"/> and offers to write it onto the
/// selected operation. The command, its tooltip and the applied confirmation are dependency
/// properties because they belong to the assistant, not to the advice being displayed.
/// </summary>
public partial class ParameterRecommendationCard : UserControl
{
    public static readonly DependencyProperty ApplyCommandProperty = DependencyProperty.Register(
        nameof(ApplyCommand), typeof(ICommand), typeof(ParameterRecommendationCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ApplyTooltipProperty = DependencyProperty.Register(
        nameof(ApplyTooltip), typeof(string), typeof(ParameterRecommendationCard), new PropertyMetadata(null));

    public static readonly DependencyProperty AppliedMessageProperty = DependencyProperty.Register(
        nameof(AppliedMessage), typeof(string), typeof(ParameterRecommendationCard), new PropertyMetadata(null));

    public ICommand? ApplyCommand
    {
        get => (ICommand?)GetValue(ApplyCommandProperty);
        set => SetValue(ApplyCommandProperty, value);
    }

    public string? ApplyTooltip
    {
        get => (string?)GetValue(ApplyTooltipProperty);
        set => SetValue(ApplyTooltipProperty, value);
    }

    public string? AppliedMessage
    {
        get => (string?)GetValue(AppliedMessageProperty);
        set => SetValue(AppliedMessageProperty, value);
    }

    public ParameterRecommendationCard()
    {
        InitializeComponent();
    }
}
