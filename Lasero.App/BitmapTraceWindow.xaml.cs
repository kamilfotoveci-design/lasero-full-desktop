using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class BitmapTraceWindow : Window
{
    private const double MinPreviewZoom = 0.25;
    private const double MaxPreviewZoom = 40;
    // Screen-space sizes: a hairline for the traced curve and a small dot for every node.
    private const double TraceLineScreenPx = 1.25;
    private const double PointDotScreenPx = 6;

    private double _previewZoom = 1;
    private double _previewOffsetX;
    private double _previewOffsetY;
    private double _basePreviewScale;
    private bool _isPanning;
    private Point _lastPanPoint;

    public BitmapTraceViewModel ViewModel { get; }

    public BitmapTraceWindow(BitmapTraceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => UpdatePreviewStrokeThickness();
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void OnPreviewViewboxSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewStrokeThickness();

    private void UpdatePreviewStrokeThickness()
    {
        if (ViewModel is null || PreviewViewbox is null || TracePreviewPath is null) return;

        // The Viewbox scales the drawing in millimetres to fit the preview panel. Compensate the
        // geometry-unit stroke so the outline stays a crisp screen-space line.
        var scaleX = PreviewViewbox.ActualWidth / ViewModel.PreviewWidthMm;
        var scaleY = PreviewViewbox.ActualHeight / ViewModel.PreviewHeightMm;
        var scale = Math.Min(scaleX, scaleY);
        if (!double.IsFinite(scale) || scale <= 0) return;

        _basePreviewScale = scale;
        ApplyStrokeThickness();
    }

    private void ApplyStrokeThickness()
    {
        if (_basePreviewScale <= 0) return;
        var screenScale = _basePreviewScale * _previewZoom;
        TracePreviewPath.StrokeThickness = TraceLineScreenPx / screenScale;
        PointsPreviewPath.StrokeThickness = PointDotScreenPx / screenScale;
        ViewModel.PreviewStrokeThicknessMm = TracePreviewPath.StrokeThickness;

        // Zoomed in far enough to see single source pixels, draw them as hard squares so a pixel edge is
        // exactly where the traced line is compared against; smaller than that, resample as usual.
        var screenPxPerSourcePx = screenScale * ViewModel.PreviewWidthMm / Math.Max(1, ViewModel.SourceImage.PixelWidth);
        RenderOptions.SetBitmapScalingMode(SourcePreviewImage,
            screenPxPerSourcePx >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(PreviewViewport), e.Delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitPreview();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle) return;
        _isPanning = true;
        _lastPanPoint = e.GetPosition(PreviewViewport);
        PreviewViewport.Cursor = Cursors.SizeAll;
        PreviewViewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var point = e.GetPosition(PreviewViewport);
        _previewOffsetX += point.X - _lastPanPoint.X;
        _previewOffsetY += point.Y - _lastPanPoint.Y;
        _lastPanPoint = point;
        ApplyPreviewTransform();
        e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isPanning) return;
        _isPanning = false;
        PreviewViewport.ReleaseMouseCapture();
        PreviewViewport.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void FitPreview()
    {
        _previewZoom = 1;
        _previewOffsetX = 0;
        _previewOffsetY = 0;
        ApplyPreviewTransform();
    }

    private void ZoomAt(Point anchor, double factor)
    {
        var newZoom = Math.Clamp(_previewZoom * factor, MinPreviewZoom, MaxPreviewZoom);
        if (Math.Abs(newZoom - _previewZoom) < 0.0001) return;

        // The Viewbox is centered in the viewport, so its untransformed layout origin is not (0,0).
        // Account for that origin to keep the exact point under the cursor stationary while zooming.
        var transformedOrigin = PreviewViewbox.TranslatePoint(new Point(0, 0), PreviewViewport);
        var layoutOriginX = transformedOrigin.X - _previewOffsetX;
        var layoutOriginY = transformedOrigin.Y - _previewOffsetY;
        var imageX = (anchor.X - transformedOrigin.X) / _previewZoom;
        var imageY = (anchor.Y - transformedOrigin.Y) / _previewZoom;
        _previewZoom = newZoom;
        _previewOffsetX = anchor.X - layoutOriginX - imageX * _previewZoom;
        _previewOffsetY = anchor.Y - layoutOriginY - imageY * _previewZoom;
        ApplyPreviewTransform();
    }

    private void ApplyPreviewTransform()
    {
        if (PreviewViewbox is null) return;
        PreviewViewbox.RenderTransform = new MatrixTransform(new Matrix(
            _previewZoom, 0, 0, _previewZoom, _previewOffsetX, _previewOffsetY));
        ApplyStrokeThickness();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasResult) return;
        DialogResult = true;
        Close();
    }
}
