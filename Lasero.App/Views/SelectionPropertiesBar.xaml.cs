using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.Controls;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class SelectionPropertiesBar : UserControl
{
    /// <summary>Mirrors CanvasViewControls/NodeEditToolbar's own TargetCanvas precedent — needed so
    /// the "Upravit uzly" button can call SceneCanvas.EnterNodeEditMode directly, the one-click
    /// mouse-driven entry point into Node Edit mode (the double-click-a-thin-stroke gesture stays
    /// available too, but is no longer the only way in).</summary>
    public static readonly DependencyProperty TargetCanvasProperty = DependencyProperty.Register(
        nameof(TargetCanvas), typeof(SceneCanvas), typeof(SelectionPropertiesBar), new PropertyMetadata(null));

    public SceneCanvas? TargetCanvas
    {
        get => (SceneCanvas?)GetValue(TargetCanvasProperty);
        set => SetValue(TargetCanvasProperty, value);
    }

    public SelectionPropertiesBar()
    {
        InitializeComponent();
    }

    private void OnEnterNodeEditClick(object sender, RoutedEventArgs e)
    {
        if (TargetCanvas is null || DataContext is not MainViewModel viewModel) return;
        if (viewModel.Scene.Selected is { IsVectorPath: true } selected)
            TargetCanvas.EnterNodeEditMode(selected);
    }

    // Every value field on this bar defers its binding to LostFocus, so a half-typed number never
    // reaches the scene. Enter commits it, Esc reverts it and a click selects it all: that is the
    // app-wide behaviour of InteractionBehaviors, not something this bar has to carry itself.
}
