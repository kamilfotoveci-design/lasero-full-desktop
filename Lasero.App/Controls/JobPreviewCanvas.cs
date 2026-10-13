using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;

namespace Lasero.App.Controls;

/// <summary>
/// The toolpath canvas of the job preview window. The path is painted by <see cref="PreviewRaster"/> into one
/// bitmap (never one element or geometry per segment, so jobs of hundreds of thousands of segments stay light)
/// and the element draws that bitmap plus the segment in progress and the laser head marker. Moving the timeline
/// forward only paints the newly reached segments; moving it back, zooming or panning repaints the bitmap.
/// Wheel zooms around the pointer, the middle (or left) button pans, a double click fits the job.
///
/// Everything here is view state. The canvas reads a <see cref="JobPreviewModel"/> and never talks to a machine.
/// </summary>
public sealed class JobPreviewCanvas : FrameworkElement
{
    private const double FitMarginDip = 32;

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(JobPreviewModel), typeof(JobPreviewCanvas),
        new PropertyMetadata(null, (d, _) => ((JobPreviewCanvas)d).OnModelChanged()));

    public static readonly DependencyProperty ShowRapidsProperty = DependencyProperty.Register(
        nameof(ShowRapids), typeof(bool), typeof(JobPreviewCanvas),
        new PropertyMetadata(true, (d, _) => ((JobPreviewCanvas)d).OnAppearanceChanged()));

    public static readonly DependencyProperty ShadeByPowerProperty = DependencyProperty.Register(
        nameof(ShadeByPower), typeof(bool), typeof(JobPreviewCanvas),
        new PropertyMetadata(false, (d, _) => ((JobPreviewCanvas)d).OnAppearanceChanged()));

    public static readonly DependencyProperty InvertProperty = DependencyProperty.Register(
        nameof(Invert), typeof(bool), typeof(JobPreviewCanvas),
        new PropertyMetadata(false, (d, _) => ((JobPreviewCanvas)d).OnAppearanceChanged()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(JobPreviewCanvas),
        new PropertyMetadata(1.0, (d, _) => ((JobPreviewCanvas)d).InvalidateVisual()));

    private PreviewRaster? _ghost;
    private PreviewRaster? _live;
    private WriteableBitmap? _bitmap;
    private PreviewViewport _view = new(1, 0, 0);
    private RgbColor _background = PreviewPalette.White;
    private RgbColor[,] _cutColors = new RgbColor[1, PowerShading.Levels + 1];
    private RgbColor _ghostCut;
    private RgbColor _ghostTravel;
    private bool _paletteDirty = true;
    private bool _ghostDirty = true;
    private bool _fitPending = true;
    private bool _viewChanged;
    private int _drawn = -1;
    private bool _isPanning;
    private Point _lastPan;

    public JobPreviewCanvas()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        Cursor = Cursors.Arrow;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public JobPreviewModel? Model { get => (JobPreviewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool ShowRapids { get => (bool)GetValue(ShowRapidsProperty); set => SetValue(ShowRapidsProperty, value); }
    public bool ShadeByPower { get => (bool)GetValue(ShadeByPowerProperty); set => SetValue(ShadeByPowerProperty, value); }
    public bool Invert { get => (bool)GetValue(InvertProperty); set => SetValue(InvertProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>Shows the whole job again, as it opens.</summary>
    public void FitToView()
    {
        _fitPending = true;
        _viewChanged = false;
        InvalidateVisual();
    }

    /// <summary>The path as drawn now, at device resolution, for "Uložit obrázek". Null while there is nothing to draw.</summary>
    public BitmapSource? CaptureImage()
    {
        if (!Prepare(out var width, out var height, out var dpiX, out var dpiY) || _live is null) return null;
        var image = BitmapSource.Create(width, height, 96 * dpiX, 96 * dpiY, PixelFormats.Bgr32, null, _live.Pixels, width * 4);
        image.Freeze();
        return image;
    }

    private void OnModelChanged()
    {
        _paletteDirty = true;
        _ghostDirty = true;
        _drawn = -1;
        _fitPending = true;
        _viewChanged = false;
        InvalidateVisual();
    }

    private void OnAppearanceChanged()
    {
        _paletteDirty = true;
        _ghostDirty = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ painting

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Prepare(out var width, out var height, out var dpiX, out var dpiY) || _bitmap is null)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        dc.DrawImage(_bitmap, new Rect(0, 0, width / dpiX, height / dpiY));
        DrawProgressOverlay(dc, width, height, dpiX, dpiY);
    }

    /// <summary>Brings the pixel buffers up to date with the model, the switches, the view and the timeline.</summary>
    private bool Prepare(out int width, out int height, out double dpiX, out double dpiY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        dpiX = dpi.DpiScaleX;
        dpiY = dpi.DpiScaleY;
        width = (int)Math.Round(ActualWidth * dpiX);
        height = (int)Math.Round(ActualHeight * dpiY);
        if (Model is not { } model || width < 2 || height < 2) return false;

        if (_live is null || _ghost is null || _bitmap is null || _live.Width != width || _live.Height != height)
        {
            _live = new PreviewRaster(width, height);
            _ghost = new PreviewRaster(width, height);
            _bitmap = new WriteableBitmap(width, height, 96 * dpiX, 96 * dpiY, PixelFormats.Bgr32, null);
            _ghostDirty = true;
            if (!_viewChanged) _fitPending = true;
        }

        if (_fitPending)
        {
            _view = PreviewViewport.Fit(model.FitBounds, width, height, FitMarginDip * dpiX);
            _fitPending = false;
            _ghostDirty = true;
        }

        if (_paletteDirty)
        {
            BuildPalette(model);
            _ghostDirty = true;
        }

        var count = model.Document.Segments.Count;
        if (_ghostDirty)
        {
            _ghost.Fill(_background);
            DrawSegments(_ghost, 0, count, ghost: true);
            _ghostDirty = false;
            _drawn = -1;
        }

        var progress = double.IsNaN(Progress) ? 0 : Math.Clamp(Progress, 0, 1);
        var target = count == 0
            ? 0
            : progress >= 1 ? count : JobPreviewTimeline.Locate(model.Document, model.Estimate, progress).CompletedSegments;

        var changed = false;
        if (_drawn < 0 || target < _drawn)
        {
            _live.CopyFrom(_ghost);
            _drawn = 0;
            changed = true;
        }

        if (target > _drawn)
        {
            DrawSegments(_live, _drawn, target, ghost: false);
            _drawn = target;
            changed = true;
        }

        if (changed)
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _live.Pixels, width * 4, 0);
        return true;
    }

    private void BuildPalette(JobPreviewModel model)
    {
        var light = TryFindResource("Brush.Canvas.WorkArea") is SolidColorBrush brush
            ? new RgbColor(brush.Color.R, brush.Color.G, brush.Color.B)
            : PreviewPalette.White;
        _background = Invert ? PreviewPalette.Graphite : light;
        var ink = Invert ? PreviewPalette.White : PreviewPalette.Graphite;

        _cutColors = new RgbColor[model.Operations.Count + 1, PowerShading.Levels + 1];
        for (var slot = 0; slot <= model.Operations.Count; slot++)
        {
            var color = slot == 0 ? ink : PreviewPalette.Visible(model.Operations[slot - 1].Color, Invert);
            for (var level = 1; level <= PowerShading.Levels; level++)
                _cutColors[slot, level] = ShadeByPower ? PowerShading.Shade(color, _background, level) : color;
        }

        _ghostCut = PreviewPalette.Blend(_background, ink, 0.16);
        _ghostTravel = PreviewPalette.Blend(_background, ink, 0.08);
        _paletteDirty = false;
    }

    /// <summary>
    /// Paints segments [from, to). Moves with the laser off go first so the cutting lines stay on top of them.
    /// <paramref name="ghost"/> paints the whole job in a pale neutral, the part the timeline has not reached yet.
    /// </summary>
    private void DrawSegments(PreviewRaster raster, int from, int to, bool ghost)
    {
        var model = Model;
        if (model is null) return;

        var points = model.Path.Points;
        var starts = model.Path.PieceStart;
        var scale = _view.Scale;
        var originX = raster.Width / 2.0 - _view.CenterX * scale;
        var originY = raster.Height / 2.0 + _view.CenterY * scale;

        for (var pass = 0; pass < 2; pass++)
        {
            var travelPass = pass == 0;
            if (travelPass && !ShowRapids) continue;
            for (var i = from; i < to; i++)
            {
                if (model.SegmentIsTravel[i] != travelPass) continue;

                RgbColor color;
                var alpha = 1.0;
                if (ghost) color = travelPass ? _ghostTravel : _ghostCut;
                else if (travelPass) { color = PreviewPalette.Travel; alpha = 0.7; }
                else color = _cutColors[model.SegmentOperation[i] + 1, model.SegmentPowerLevel[i]];

                for (var piece = starts[i]; piece < starts[i + 1]; piece++)
                {
                    var p = piece * 4;
                    raster.DrawLine(
                        originX + points[p] * scale, originY - points[p + 1] * scale,
                        originX + points[p + 2] * scale, originY - points[p + 3] * scale,
                        color, alpha);
                }
            }
        }
    }

    /// <summary>The segment being worked right now, drawn up to the head, and the head itself.</summary>
    private void DrawProgressOverlay(DrawingContext dc, int width, int height, double dpiX, double dpiY)
    {
        var model = Model;
        if (model is null || model.Document.Segments.Count == 0) return;
        var progress = double.IsNaN(Progress) ? 0 : Math.Clamp(Progress, 0, 1);
        if (progress >= 1) return;

        var point = JobPreviewTimeline.Locate(model.Document, model.Estimate, progress);
        var index = point.CompletedSegments;
        if (index >= model.Document.Segments.Count) return;

        Point ToDip(double x, double y) => new(_view.ToScreenX(x, width) / dpiX, _view.ToScreenY(y, height) / dpiY);

        var travel = model.SegmentIsTravel[index];
        if (point.PartialFraction > 0 && !(travel && !ShowRapids))
        {
            var color = travel
                ? PreviewPalette.Travel
                : _cutColors[model.SegmentOperation[index] + 1, model.SegmentPowerLevel[index]];
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), 1.0 / dpiX)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();

            var points = model.Path.Points;
            var first = model.Path.PieceStart[index];
            var pieces = model.Path.PieceStart[index + 1] - first;
            var reach = point.PartialFraction * pieces;
            var whole = (int)Math.Floor(reach);
            var rest = reach - whole;
            for (var k = 0; k < pieces && k <= whole; k++)
            {
                var p = (first + k) * 4;
                double x0 = points[p], y0 = points[p + 1], x1 = points[p + 2], y1 = points[p + 3];
                if (k == whole)
                {
                    if (rest <= 0) break;
                    x1 = x0 + (x1 - x0) * rest;
                    y1 = y0 + (y1 - y0) * rest;
                }

                dc.DrawLine(pen, ToDip(x0, y0), ToDip(x1, y1));
            }
        }

        // The head: a graphite (white on the inverted canvas) ring with a hairline of canvas colour around it,
        // so it reads on top of any operation colour.
        var ink = Invert ? PreviewPalette.White : PreviewPalette.Graphite;
        var halo = new Pen(new SolidColorBrush(Color.FromRgb(_background.R, _background.G, _background.B)), 3.5);
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(ink.R, ink.G, ink.B)), 1.5);
        halo.Freeze();
        ring.Freeze();
        var center = ToDip(point.Head.X, point.Head.Y);
        dc.DrawEllipse(null, halo, center, 5, 5);
        dc.DrawEllipse(null, ring, center, 5, 5);
    }

    // ------------------------------------------------------------------ pointer

    private Point ToPixels(Point dip)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(dip.X * dpi.DpiScaleX, dip.Y * dpi.DpiScaleY);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Model is null || e.Delta == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var at = ToPixels(e.GetPosition(this));
        var width = ActualWidth * dpi.DpiScaleX;
        var height = ActualHeight * dpi.DpiScaleY;
        _view = _view.ZoomAt(at.X, at.Y, Math.Pow(1.0015, e.Delta), width, height);
        _viewChanged = true;
        _ghostDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Left)) return;

        if (e.ClickCount >= 2)
        {
            FitToView();
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _lastPan = ToPixels(e.GetPosition(this));
        Cursor = Cursors.SizeAll;
        Focus();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isPanning) return;

        var now = ToPixels(e.GetPosition(this));
        _view = _view.PanBy(now.X - _lastPan.X, now.Y - _lastPan.Y);
        _lastPan = now;
        _viewChanged = true;
        _ghostDirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isPanning || e.ChangedButton is not (MouseButton.Middle or MouseButton.Left)) return;
        EndPan();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPan();
    }

    private void EndPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
