using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>A recent-project thumbnail card — replaces the inline ItemsControl.ItemTemplate that used
/// to live directly in HomeView.xaml. DataContext is the bound RecentProjectEntry; OpenCommand/
/// RemoveCommand are supplied by the consumer since they belong to HomeViewModel.</summary>
public partial class ProjectCard : UserControl
{
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(
        nameof(OpenCommand), typeof(ICommand), typeof(ProjectCard), new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
        nameof(RemoveCommand), typeof(ICommand), typeof(ProjectCard), new PropertyMetadata(null));

    public ProjectCard()
    {
        InitializeComponent();
    }

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
}
