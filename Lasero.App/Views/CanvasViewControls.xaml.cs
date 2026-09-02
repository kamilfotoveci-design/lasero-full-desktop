using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lasero.App.Controls;

namespace Lasero.App.Views;

/// <summary>
/// Floating undo/redo/pan/zoom cluster for the design canvas. Zoom is a property of the view, so it
/// is driven straight against the <see cref="SceneCanvas"/> element handed in through
/// <see cref="TargetCanvas"/>; nothing here changes scale maths, it only calls the canvas's own
/// public entry points.
/// </summary>
public partial class CanvasViewControls : UserControl
{
    public static readonly DependencyProperty TargetCanvasProperty = DependencyProperty.Register(
        nameof(TargetCanvas), typeof(SceneCanvas), typeof(CanvasViewControls), new PropertyMetadata(null));

    public SceneCanvas? TargetCanvas
    {
        get => (SceneCanvas?)GetValue(TargetCanvasProperty);
        set => SetValue(TargetCanvasProperty, value);
    }

    public CanvasViewControls()
    {
        InitializeComponent();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => TargetCanvas?.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => TargetCanvas?.ZoomOut();

    private void OnFitToViewClick(object sender, RoutedEventArgs e) => TargetCanvas?.FitToView();

    private void OnCenterClick(object sender, RoutedEventArgs e) => TargetCanvas?.CenterWorkArea();

    private void OnZoomPickerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>The preset asks for a scale; the canvas decides what it can reach and reports back
    /// through ZoomPercent, which is why the readout is bound to the canvas and not to the choice.</summary>
    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var percent))
            TargetCanvas?.SetZoomPercent(percent);
    }
}
