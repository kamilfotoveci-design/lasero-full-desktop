using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class RasterImportWindow : Window
{
    private bool _isPanningPreview;
    private FrameworkElement? _panViewport;
    private Point _panStartPointer;
    private double _panStartX;
    private double _panStartY;

    public RasterImportViewModel ViewModel { get; }

    public RasterImportWindow(RasterImportViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryValidate(out var message))
        {
            ViewModel.StatusMessage = message;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnPreviewZoomMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (DataContext is not RasterImportViewModel viewModel || sender is not FrameworkElement viewport) return;
        var point = e.GetPosition(viewport);
        var origin = new System.Windows.Point(
            Math.Clamp(point.X / Math.Max(1, viewport.ActualWidth), 0, 1),
            Math.Clamp(point.Y / Math.Max(1, viewport.ActualHeight), 0, 1));
        OriginalPreviewImage.RenderTransformOrigin = origin;
        ProcessedPreviewImage.RenderTransformOrigin = origin;
        viewModel.PreviewZoom = Math.Clamp(viewModel.PreviewZoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 8);
        e.Handled = true;
    }

    private void OnPreviewPanMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement viewport) return;

        _panViewport = viewport;
        _panStartPointer = e.GetPosition(viewport);
        var translation = GetTranslation(OriginalPreviewImage);
        _panStartX = translation.X;
        _panStartY = translation.Y;
        _isPanningPreview = viewport.CaptureMouse();
        e.Handled = _isPanningPreview;
    }

    private void OnPreviewPanMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningPreview || !ReferenceEquals(sender, _panViewport) || _panViewport is null) return;

        var pointer = e.GetPosition(_panViewport);
        var delta = pointer - _panStartPointer;
        SetTranslation(OriginalPreviewImage, _panStartX + delta.X, _panStartY + delta.Y);
        SetTranslation(ProcessedPreviewImage, _panStartX + delta.X, _panStartY + delta.Y);
        e.Handled = true;
    }

    private void OnPreviewPanMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !ReferenceEquals(sender, _panViewport)) return;
        if (sender is FrameworkElement viewport) viewport.ReleaseMouseCapture();
        ClearPreviewPan();
        e.Handled = true;
    }

    private void OnPreviewLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _panViewport)) ClearPreviewPan();
    }

    private void ClearPreviewPan()
    {
        _isPanningPreview = false;
        _panViewport = null;
    }

    private static TranslateTransform GetTranslation(System.Windows.Controls.Image image) =>
        ((TransformGroup)image.RenderTransform).Children[1] as TranslateTransform
        ?? throw new InvalidOperationException("Raster preview transform is missing its pan translation.");

    private static void SetTranslation(System.Windows.Controls.Image image, double x, double y)
    {
        var translation = GetTranslation(image);
        translation.X = x;
        translation.Y = y;
    }
}
