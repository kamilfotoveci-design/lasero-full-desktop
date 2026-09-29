using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lasero.App.ViewModels;
using Lasero.Core.Trace;

namespace Lasero.App;

public partial class BitmapTraceWindow : Window
{
    private const double MinPreviewZoom = 0.25;
    private const double MaxPreviewZoom = 12;
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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) =>
        {
            UpdatePreviewStrokeThickness();
            UpdatePreviewDisplayMode();
        };
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.Dispose();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BitmapTraceViewModel.Mode)) UpdatePreviewDisplayMode();
    }

    private void OnPreviewViewboxSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewStrokeThickness();

    private void OnPreviewDisplayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdatePreviewDisplayMode();

    private void UpdatePreviewDisplayMode()
    {
        if (PreviewDisplayMode is null || SourcePreviewImage is null || TracePreviewPath is null || ColoredTracePaths is null || ViewModel is null) return;
        var mode = PreviewDisplayMode.SelectedIndex;
        var isColor = ViewModel.Mode == TraceMode.Color;
        SourcePreviewImage.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        SourcePreviewImage.Opacity = mode == 0 ? 1 : 0.45;
        TracePreviewPath.Visibility = mode == 0 || isColor ? Visibility.Collapsed : Visibility.Visible;
        ColoredTracePaths.Visibility = mode == 0 || !isColor ? Visibility.Collapsed : Visibility.Visible;
        ColoredTracePaths.Opacity = mode == 1 ? 0.7 : 1;
    }

    private void UpdatePreviewStrokeThickness()
    {
        if (ViewModel is null || PreviewViewbox is null || TracePreviewPath is null) return;

        // The Viewbox scales the drawing in millimetres to fit the preview panel. Compensate the
        // geometry-unit stroke so the outline stays a crisp screen-space line like a CAD trace.
        var scaleX = PreviewViewbox.ActualWidth / ViewModel.PreviewWidthMm;
        var scaleY = PreviewViewbox.ActualHeight / ViewModel.PreviewHeightMm;
        var scale = Math.Min(scaleX, scaleY);
        if (double.IsFinite(scale) && scale > 0)
        {
            _basePreviewScale = scale;
            TracePreviewPath.StrokeThickness = 1.25 / (scale * _previewZoom);
            ViewModel.PreviewStrokeThicknessMm = TracePreviewPath.StrokeThickness;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var cursor = e.GetPosition(PreviewViewport);
        ZoomAt(cursor, e.Delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
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

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter(), 1.2);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter(), 1 / 1.2);

    private void OnFitPreviewClick(object sender, RoutedEventArgs e)
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

    private Point ViewportCenter() => new(PreviewViewport.ActualWidth / 2, PreviewViewport.ActualHeight / 2);

    private void ApplyPreviewTransform()
    {
        if (PreviewViewbox is null) return;
        PreviewViewbox.RenderTransform = new MatrixTransform(new Matrix(
            _previewZoom, 0, 0, _previewZoom, _previewOffsetX, _previewOffsetY));
        if (ZoomLevelText is not null)
            ZoomLevelText.Text = $"{_previewZoom:P0}";
        if (TracePreviewPath is not null && _basePreviewScale > 0)
        {
            TracePreviewPath.StrokeThickness = 1.25 / (_basePreviewScale * _previewZoom);
            ViewModel.PreviewStrokeThicknessMm = TracePreviewPath.StrokeThickness;
        }
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
