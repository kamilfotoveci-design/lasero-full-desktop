using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>One recent-project card on the Home dashboard. The commands live on the owning view model
/// (HomeViewModel), so they arrive as dependency properties while the DataContext stays the row.</summary>
public partial class ProjectThumbCard : UserControl
{
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(
        nameof(OpenCommand), typeof(ICommand), typeof(ProjectThumbCard), new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
        nameof(RemoveCommand), typeof(ICommand), typeof(ProjectThumbCard), new PropertyMetadata(null));

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => (ICommand?)GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ProjectThumbCard()
    {
        InitializeComponent();
    }
}
