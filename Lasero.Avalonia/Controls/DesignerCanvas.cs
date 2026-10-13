using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.Avalonia.Controls;

public sealed class DesignerCanvas : Control
{
    public static readonly StyledProperty<SceneDocument?> DocumentProperty =
        AvaloniaProperty.Register<DesignerCanvas, SceneDocument?>(nameof(Document));

    public static readonly StyledProperty<SceneObject?> SelectedObjectProperty =
        AvaloniaProperty.Register<DesignerCanvas, SceneObject?>(nameof(SelectedObject), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<EditorTool> ActiveToolProperty =
        AvaloniaProperty.Register<DesignerCanvas, EditorTool>(nameof(ActiveTool));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DesignerCanvas, double>(nameof(Zoom), 1, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> PanXProperty =
        AvaloniaProperty.Register<DesignerCanvas, double>(nameof(PanX), 0, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> PanYProperty =
        AvaloniaProperty.Register<DesignerCanvas, double>(nameof(PanY), 0, defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> WorkAreaWidthMmProperty =
        AvaloniaProperty.Register<DesignerCanvas, double>(nameof(WorkAreaWidthMm), 500);

    public static readonly StyledProperty<double> WorkAreaHeightMmProperty =
        AvaloniaProperty.Register<DesignerCanvas, double>(nameof(WorkAreaHeightMm), 400);

    public static readonly StyledProperty<EditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<DesignerCanvas, EditorViewModel?>(nameof(Editor));

    private readonly IBrush workspaceBrush = new SolidColorBrush(Color.Parse("#F7F7F5"));
    private readonly IBrush sheetBrush = Brushes.White;
    private readonly IBrush rulerBrush = new SolidColorBrush(Color.Parse("#F3F4F2"));
    private readonly IBrush minorGridBrush = new SolidColorBrush(Color.Parse("#EEF0ED"));
    private readonly IBrush majorGridBrush = new SolidColorBrush(Color.Parse("#DDE1DC"));
    private readonly IBrush borderBrush = new SolidColorBrush(Color.Parse("#D2D4D0"));
    private readonly IBrush accentBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private readonly IBrush labelBrush = new SolidColorBrush(Color.Parse("#666B68"));
    private Point dragStart;
    private double dragPanX;
    private double dragPanY;
    private bool isPanning;
    private bool isDrawing;
    private Point drawingStart;
    private Point drawingCurrent;

    static DesignerCanvas()
    {
        AffectsRender<DesignerCanvas>(DocumentProperty, SelectedObjectProperty, ActiveToolProperty,
            ZoomProperty, PanXProperty, PanYProperty, WorkAreaWidthMmProperty, WorkAreaHeightMmProperty);
        FocusableProperty.OverrideDefaultValue<DesignerCanvas>(true);
    }

    public SceneDocument? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public SceneObject? SelectedObject { get => GetValue(SelectedObjectProperty); set => SetValue(SelectedObjectProperty, value); }
    public EditorTool ActiveTool { get => GetValue(ActiveToolProperty); set => SetValue(ActiveToolProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double PanX { get => GetValue(PanXProperty); set => SetValue(PanXProperty, value); }
    public double PanY { get => GetValue(PanYProperty); set => SetValue(PanYProperty, value); }
    public double WorkAreaWidthMm { get => GetValue(WorkAreaWidthMmProperty); set => SetValue(WorkAreaWidthMmProperty, value); }
    public double WorkAreaHeightMm { get => GetValue(WorkAreaHeightMmProperty); set => SetValue(WorkAreaHeightMmProperty, value); }
    public EditorViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DocumentProperty) return;

        if (change.OldValue is SceneDocument oldDocument)
            Detach(oldDocument);
        if (change.NewValue is SceneDocument newDocument)
            Attach(newDocument);
    }

    [SupportedOSPlatform("windows")]
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(workspaceBrush, Bounds);
        if (Bounds.Width < 80 || Bounds.Height < 80) return;

        var transform = GetCanvasTransform();
        var workRect = new Rect(transform.OriginX, transform.TopY,
            WorkAreaWidthMm * transform.Scale, WorkAreaHeightMm * transform.Scale);
        var sheetRect = new Rect(workRect.X - 28, workRect.Y - 20, workRect.Width + 28, workRect.Height + 20);

        context.DrawRectangle(sheetBrush, new Pen(borderBrush), sheetRect);
        context.FillRectangle(rulerBrush, new Rect(sheetRect.X, sheetRect.Y, sheetRect.Width, 20));
        context.FillRectangle(rulerBrush, new Rect(sheetRect.X, sheetRect.Y, 28, sheetRect.Height));
        DrawGrid(context, workRect, transform);
        DrawRulers(context, workRect, transform);
        DrawObjects(context, workRect, transform);
        DrawDraft(context, workRect, transform);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        SetCurrentValue(ZoomProperty, Math.Clamp(Zoom * factor, 0.25, 4));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            isPanning = true;
            dragStart = e.GetPosition(this);
            dragPanX = PanX;
            dragPanY = PanY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;
        if (ActiveTool == EditorTool.Select)
        {
            SelectedObject = HitTestObject(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (ActiveTool is EditorTool.Rectangle or EditorTool.Line or EditorTool.Shape)
        {
            isDrawing = true;
            drawingStart = ScreenToWorld(e.GetPosition(this));
            drawingCurrent = drawingStart;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (isDrawing)
        {
            drawingCurrent = ScreenToWorld(e.GetPosition(this));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!isPanning) return;
        var position = e.GetPosition(this);
        SetCurrentValue(PanXProperty, dragPanX + position.X - dragStart.X);
        SetCurrentValue(PanYProperty, dragPanY + position.Y - dragStart.Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (isDrawing)
        {
            isDrawing = false;
            drawingCurrent = ScreenToWorld(e.GetPosition(this));
            Editor?.AddPrimitive(ActiveTool, drawingStart.X, drawingStart.Y, drawingCurrent.X, drawingCurrent.Y);
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (isPanning)
        {
            isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private CanvasTransform GetCanvasTransform()
    {
        var availableWidth = Math.Max(1, Bounds.Width - 72);
        var availableHeight = Math.Max(1, Bounds.Height - 64);
        var fit = Math.Min(availableWidth / WorkAreaWidthMm, availableHeight / WorkAreaHeightMm);
        var scale = Math.Max(0.05, fit * Zoom);
        var width = WorkAreaWidthMm * scale;
        var height = WorkAreaHeightMm * scale;
        return new CanvasTransform((Bounds.Width - width) / 2 + PanX, (Bounds.Height - height) / 2 + PanY, scale);
    }

    private void DrawGrid(DrawingContext context, Rect workRect, CanvasTransform transform)
    {
        using var clip = context.PushClip(workRect);
        for (var x = 0d; x <= WorkAreaWidthMm; x += 10)
        {
            var screenX = transform.OriginX + x * transform.Scale;
            var brush = x % 50 == 0 ? majorGridBrush : minorGridBrush;
            context.DrawLine(new Pen(brush), new Point(screenX, workRect.Top), new Point(screenX, workRect.Bottom));
        }

        for (var y = 0d; y <= WorkAreaHeightMm; y += 10)
        {
            var screenY = transform.TopY + (WorkAreaHeightMm - y) * transform.Scale;
            var brush = y % 50 == 0 ? majorGridBrush : minorGridBrush;
            context.DrawLine(new Pen(brush), new Point(workRect.Left, screenY), new Point(workRect.Right, screenY));
        }

        context.DrawLine(new Pen(accentBrush, 1), new Point(workRect.Left, workRect.Bottom), new Point(workRect.Right, workRect.Bottom));
        context.DrawLine(new Pen(accentBrush, 1), new Point(workRect.Left, workRect.Top), new Point(workRect.Left, workRect.Bottom));
    }

    private void DrawRulers(DrawingContext context, Rect workRect, CanvasTransform transform)
    {
        for (var x = 0d; x <= WorkAreaWidthMm; x += 50)
        {
            var screenX = transform.OriginX + x * transform.Scale;
            context.DrawLine(new Pen(borderBrush), new Point(screenX, workRect.Top - 6), new Point(screenX, workRect.Top));
            DrawLabel(context, x.ToString("0", CultureInfo.InvariantCulture), new Point(screenX + 3, workRect.Top - 18));
        }

        for (var y = 0d; y <= WorkAreaHeightMm; y += 50)
        {
            var screenY = transform.TopY + (WorkAreaHeightMm - y) * transform.Scale;
            context.DrawLine(new Pen(borderBrush), new Point(workRect.Left - 6, screenY), new Point(workRect.Left, screenY));
            DrawLabel(context, y.ToString("0", CultureInfo.InvariantCulture), new Point(workRect.Left - 26, screenY - 7));
        }
    }

    // Raster placement judgement needs to see the actual photo, not just its rectangle outline — the
    // outline alone (from item.GetWorldShapes() below) is what SceneObjectFactory.FromRaster creates
    // for hit-testing/resize, but Lasero.App's SceneCanvas always overlays the real bitmap on top of
    // it. Cached by file path since Render is called every frame/pan/zoom and reloading from disk
    // that often would be wasteful; never evicted, matching this app's current scale (a handful of
    // imports per session, not hundreds).
    [SupportedOSPlatform("windows")]
    private readonly Dictionary<string, Bitmap?> rasterPreviewCache = new();

    [SupportedOSPlatform("windows")]
    private Bitmap? GetRasterPreview(string filePath)
    {
        if (rasterPreviewCache.TryGetValue(filePath, out var cached)) return cached;
        Bitmap? bitmap;
        try
        {
            bitmap = ProcessedImagePreviewRenderer.RenderFileForCanvas(filePath);
        }
        catch
        {
            bitmap = null;
        }
        rasterPreviewCache[filePath] = bitmap;
        return bitmap;
    }

    [SupportedOSPlatform("windows")]
    private void DrawObjects(DrawingContext context, Rect workRect, CanvasTransform transform)
    {
        if (Document is null) return;
        using var clip = context.PushClip(workRect);
        foreach (var item in Document.Objects.Where(item => item.IsVisible))
        {
            if (item.RasterFilePath is { } rasterFilePath && GetRasterPreview(rasterFilePath) is { } preview)
            {
                // Axis-aligned only: no object in this editor supports rotation yet (no resize/rotate
                // handles at all — see MIGRATION.md), so WorldBounds() is already the true on-screen
                // rectangle, not an approximation of a rotated one.
                var bounds = item.WorldBounds();
                var topLeft = ToScreen(bounds.MinX, bounds.MaxY, transform);
                var bottomRight = ToScreen(bounds.MaxX, bounds.MinY, transform);
                context.DrawImage(preview, new Rect(0, 0, preview.PixelSize.Width, preview.PixelSize.Height), new Rect(topLeft, bottomRight));
            }

            foreach (var shape in item.GetWorldShapes())
            {
                if (shape.Points.Count < 2) continue;
                var geometry = new StreamGeometry();
                using (var geometryContext = geometry.Open())
                {
                    geometryContext.BeginFigure(ToScreen(shape.Points[0].X, shape.Points[0].Y, transform), false);
                    foreach (var point in shape.Points.Skip(1))
                        geometryContext.LineTo(ToScreen(point.X, point.Y, transform));
                    geometryContext.EndFigure(shape.IsClosed);
                }

                var color = Color.FromRgb(shape.LayerColor.R, shape.LayerColor.G, shape.LayerColor.B);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.4), geometry);
            }

            if (item == SelectedObject)
            {
                var bounds = item.WorldBounds();
                var topLeft = ToScreen(bounds.MinX, bounds.MaxY, transform);
                var bottomRight = ToScreen(bounds.MaxX, bounds.MinY, transform);
                context.DrawRectangle(null, new Pen(accentBrush, 1.5), new Rect(topLeft, bottomRight));
            }
        }
    }

    private SceneObject? HitTestObject(Point screenPoint)
    {
        if (Document is null) return null;
        var transform = GetCanvasTransform();
        var worldX = (screenPoint.X - transform.OriginX) / transform.Scale;
        var worldY = WorkAreaHeightMm - (screenPoint.Y - transform.TopY) / transform.Scale;
        var tolerance = 5 / transform.Scale;
        return Document.Objects.Reverse().FirstOrDefault(item =>
        {
            var bounds = item.WorldBounds();
            return worldX >= bounds.MinX - tolerance && worldX <= bounds.MaxX + tolerance &&
                   worldY >= bounds.MinY - tolerance && worldY <= bounds.MaxY + tolerance;
        });
    }

    private Point ScreenToWorld(Point screenPoint)
    {
        var transform = GetCanvasTransform();
        return new Point(
            Math.Clamp((screenPoint.X - transform.OriginX) / transform.Scale, 0, WorkAreaWidthMm),
            Math.Clamp(WorkAreaHeightMm - (screenPoint.Y - transform.TopY) / transform.Scale, 0, WorkAreaHeightMm));
    }

    private void DrawDraft(DrawingContext context, Rect workRect, CanvasTransform transform)
    {
        if (!isDrawing) return;
        using var clip = context.PushClip(workRect);
        var start = ToScreen(drawingStart.X, drawingStart.Y, transform);
        var end = ToScreen(drawingCurrent.X, drawingCurrent.Y, transform);
        var pen = new Pen(accentBrush, 1.5, dashStyle: new DashStyle([4, 3], 0));
        if (ActiveTool == EditorTool.Line)
            context.DrawLine(pen, start, end);
        else
            context.DrawRectangle(null, pen, new Rect(start, end));
    }

    private Point ToScreen(double x, double y, CanvasTransform transform) =>
        new(transform.OriginX + x * transform.Scale, transform.TopY + (WorkAreaHeightMm - y) * transform.Scale);

    private void DrawLabel(DrawingContext context, string text, Point point)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), 10, labelBrush);
        context.DrawText(formatted, point);
    }

    private void Attach(SceneDocument document)
    {
        document.Objects.CollectionChanged += OnObjectsChanged;
        foreach (var item in document.Objects) item.PropertyChanged += OnObjectChanged;
    }

    private void Detach(SceneDocument document)
    {
        document.Objects.CollectionChanged -= OnObjectsChanged;
        foreach (var item in document.Objects) item.PropertyChanged -= OnObjectChanged;
    }

    private void OnObjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SceneObject item in e.OldItems) item.PropertyChanged -= OnObjectChanged;
        if (e.NewItems is not null)
            foreach (SceneObject item in e.NewItems) item.PropertyChanged += OnObjectChanged;
        InvalidateVisual();
    }

    private void OnObjectChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private readonly record struct CanvasTransform(double OriginX, double TopY, double Scale);
}
