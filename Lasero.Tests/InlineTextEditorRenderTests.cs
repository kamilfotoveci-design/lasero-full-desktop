using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// The inline text editor has to land exactly on the letters it edits. These render the real
/// SceneCanvas twice - once with the flattened text object, once with the live TextBox standing in
/// for it - and compare where the ink actually is, so a regression in offset, size, rotation, scale or
/// zoom shows up as pixels rather than as an argument about which corner an enum means.
///
/// The canvas lives in an off-screen window on its own STA thread, inside a real App so the theme, the
/// TextBox style and the window's Display text formatting are all in play exactly as they are in the
/// running application. Set LASERO_RENDER_OUT to a folder to keep the comparison images.
/// </summary>
public sealed class InlineTextEditorRenderTests
{
    private const int CanvasWidth = 900;
    private const int CanvasHeight = 600;
    private const double DefaultScale = 4; // px per mm at "100 %", see SceneCanvas.DefaultScale


    private static readonly Dispatcher Ui = StartUi();

    private static Dispatcher StartUi()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // App.xaml itself is not loaded: its Window style points at "pack://application:,,,/Assets/...",
            // which resolves against the host executable under a test runner and throws. The App constructor
            // (window text options) and the same resource dictionaries are what the canvas depends on.
            var app = new Lasero.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, args) => args.Handled = true;
            foreach (var file in new[] { "LaseroTheme", "SharedUiStyles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Lasero.App;component/Theme/{file}.xaml"),
                });
            app.Resources["InverseBoolConverter"] = new Lasero.App.Converters.InverseBooleanConverter();
            app.Resources["BoolToVisibilityShared"] = new BooleanToVisibilityConverter();
            app.Resources["InverseBoolToVisibilityShared"] = new Lasero.App.Converters.InverseBooleanToVisibilityConverter();
            app.Resources["NotNullToVisibilityShared"] = new Lasero.App.Converters.NotNullToVisibilityConverter();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public sealed record Scenario(
        string Name, string Text, int ZoomPercent,
        double RotationDeg = 0, double ScaleX = 1, double ScaleY = 1, bool Bold = false, bool Italic = false);

    private static object[] Row(Scenario s) =>
        [s.Name, s.Text, s.ZoomPercent, s.RotationDeg, s.ScaleX, s.ScaleY, s.Bold, s.Italic];

    private static Scenario FromRow(string name, string text, int zoom, double rotation, double sx, double sy, bool bold, bool italic) =>
        new(name, text, zoom, rotation, sx, sy, bold, italic);

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var zoom in new[] { 25, 47, 100, 300 })
            yield return Row(new Scenario("plain", "TEXT", zoom));
        foreach (var zoom in new[] { 47, 100, 300 })
            yield return Row(new Scenario("rotated-30", "TEXT", zoom, RotationDeg: 30));
        yield return Row(new Scenario("rotated-90", "TEXT", 100, RotationDeg: 90));
        yield return Row(new Scenario("stretched-tall", "TEXT", 47, ScaleY: 3.5));
        yield return Row(new Scenario("stretched-tall", "TEXT", 100, ScaleY: 3.5));
        yield return Row(new Scenario("scaled-x3", "TEXT", 47, ScaleX: 3, ScaleY: 3));
        yield return Row(new Scenario("scaled-x3", "TEXT", 100, ScaleX: 3, ScaleY: 3));
        yield return Row(new Scenario("bold-italic", "Ahoj svete", 100, Bold: true, Italic: true));
        yield return Row(new Scenario("bold-italic", "Ahoj svete", 300, Bold: true, Italic: true));
        yield return Row(new Scenario("mirrored", "TEXT", 100, ScaleX: -1));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EditorInkLandsOnTheObjectInk(string name, string text, int zoom, double rotation, double sx, double sy, bool bold, bool italic)
    {
        var scenario = FromRow(name, text, zoom, rotation, sx, sy, bold, italic);
        var result = Ui.Invoke(() => Measure(scenario));
        WriteReport(scenario, result);

        var tolerance = 2.0 + 0.006 * Math.Max(result.Object.Width, result.Object.Height);
        Assert.True(result.Object.HasInk, "the reference render must contain the text");
        Assert.True(result.Editor.HasInk, "the editor must draw letters");
        Assert.True(Math.Abs(result.Editor.Left - result.Object.Left) <= tolerance, $"left edge off by {result.Editor.Left - result.Object.Left:0.0} px");
        Assert.True(Math.Abs(result.Editor.Right - result.Object.Right) <= tolerance, $"right edge off by {result.Editor.Right - result.Object.Right:0.0} px");
        Assert.True(Math.Abs(result.Editor.Top - result.Object.Top) <= tolerance, $"top edge off by {result.Editor.Top - result.Object.Top:0.0} px");
        Assert.True(Math.Abs(result.Editor.Bottom - result.Object.Bottom) <= tolerance, $"bottom edge off by {result.Editor.Bottom - result.Object.Bottom:0.0} px");
        Assert.True(result.Coverage >= 0.9, $"only {result.Coverage:P0} of the object's ink is under the editor's letters");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void SelectionHighlightCoversTheLetters(string name, string text, int zoom, double rotation, double sx, double sy, bool bold, bool italic)
    {
        var scenario = FromRow(name, text, zoom, rotation, sx, sy, bold, italic);
        var result = Ui.Invoke(() => Measure(scenario));

        Assert.True(result.Highlight.HasInk, "select-all must paint a highlight");
        // Italic and bold faces overhang the line box a little (an italic capital leans past its own
        // advance), and the highlight can only cover the advance box, so the slack scales with the text.
        var slack = 2.0 + 0.006 * Math.Max(result.Object.Width, result.Object.Height);
        Assert.True(result.Highlight.Left <= result.Object.Left + slack, "highlight starts right of the first letter");
        Assert.True(result.Highlight.Right >= result.Object.Right - slack, "highlight ends left of the last letter");
        Assert.True(result.Highlight.Top <= result.Object.Top + slack, "highlight starts below the top of the letters");
        Assert.True(result.Highlight.Bottom >= result.Object.Bottom - slack, "highlight ends above the bottom of the letters");
    }

    [Theory]
    [InlineData(47, 100)]
    [InlineData(100, 300)]
    [InlineData(300, 47)]
    public void EditorFollowsTheViewWhenTheCanvasIsZoomedWhileTyping(int startZoom, int endZoom)
    {
        var result = Ui.Invoke(() => MeasureZoomWhileEditing(startZoom, endZoom));
        WriteReport(new Scenario($"zoom-while-editing-from-{startZoom}", "TEXT", endZoom), result);

        var tolerance = 2.0 + 0.006 * Math.Max(result.Object.Width, result.Object.Height);
        Assert.True(result.Editor.HasInk);
        Assert.True(Math.Abs(result.Editor.Left - result.Object.Left) <= tolerance, $"left edge off by {result.Editor.Left - result.Object.Left:0.0} px");
        Assert.True(Math.Abs(result.Editor.Right - result.Object.Right) <= tolerance, $"right edge off by {result.Editor.Right - result.Object.Right:0.0} px");
        Assert.True(Math.Abs(result.Editor.Top - result.Object.Top) <= tolerance, $"top edge off by {result.Editor.Top - result.Object.Top:0.0} px");
        Assert.True(Math.Abs(result.Editor.Bottom - result.Object.Bottom) <= tolerance, $"bottom edge off by {result.Editor.Bottom - result.Object.Bottom:0.0} px");
        // The flattened text must not reappear underneath while the editor is open.
        Assert.False(result.ObjectVisibleUnderEditor, "the object's own outlines are showing through the editor");
    }

    // ------------------------------------------------------------------ measurement

    private sealed record Ink(int Left, int Top, int Right, int Bottom, int Count)
    {
        public bool HasInk => Count > 0;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private sealed record Result(
        Ink Object, Ink Editor, Ink Highlight, double Coverage, bool ObjectVisibleUnderEditor,
        BitmapSource ObjectImage, BitmapSource EditorImage, BitmapSource HighlightImage);

    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required SceneCanvas Canvas { get; init; }
        public required SceneViewModel ViewModel { get; init; }
        public required SceneObject Object { get; init; }
        public Rect Region { get; set; }
    }

    private static Rig Build(Scenario scenario)
    {
        var viewModel = new SceneViewModel();
        var canvas = new SceneCanvas { Width = CanvasWidth, Height = CanvasHeight, ViewModel = viewModel };
        var window = new Window
        {
            Width = CanvasWidth, Height = CanvasHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, SizeToContent = SizeToContent.WidthAndHeight,
            Content = canvas,
        };
        window.Show();
        Flush();

        viewModel.AddText(
            new TextSource { Text = scenario.Text, HeightMm = 12, Bold = scenario.Bold, Italic = scenario.Italic },
            new Position(100, 100, 0));
        var obj = viewModel.Objects.Single();
        obj.Transform = obj.Transform with
        {
            RotationDeg = scenario.RotationDeg, ScaleX = scenario.ScaleX, ScaleY = scenario.ScaleY,
        };
        viewModel.SelectedObjects.Clear();
        Flush();

        return new Rig { Window = window, Canvas = canvas, ViewModel = viewModel, Object = obj };
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, Private)!.GetValue(target)!;

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Private)!.SetValue(target, value);

    private static void SetZoom(Rig rig, int zoomPercent)
    {
        var scale = DefaultScale * zoomPercent / 100.0;
        const double margin = 20;
        Set(rig.Canvas, "_autoFit", false);
        Set(rig.Canvas, "_scale", scale);
        // Put the object's centre in the middle of the canvas at every zoom.
        Set(rig.Canvas, "_offsetXMm", rig.Object.Transform.X - (CanvasWidth / 2.0 - margin) / scale);
        Set(rig.Canvas, "_offsetYMm", rig.Object.Transform.Y - (CanvasHeight / 2.0 - margin) / scale);
        rig.Canvas.GetType().GetMethod("RepositionAll", Private)!.Invoke(rig.Canvas, null);
        rig.Canvas.UpdateLayout();
        Flush();

        var bounds = rig.Object.WorldBounds();
        var halfWidth = Math.Min(CanvasWidth / 2.0 - 34, bounds.Width * scale * 0.75 + 50);
        var halfHeight = Math.Min(CanvasHeight / 2.0 - 34, bounds.Height * scale * 1.3 + 50);
        // Clear of the ruler strips along the top and left edge, which carry dark labels of their own.
        rig.Region = new Rect(CanvasWidth / 2.0 - halfWidth, CanvasHeight / 2.0 - halfHeight, halfWidth * 2, halfHeight * 2);
    }

    private static void BeginEditing(Rig rig)
    {
        rig.ViewModel.SelectedObjects.Add(rig.Object);
        rig.Canvas.GetType().GetMethod("BeginInlineTextEdit", Private)!.Invoke(rig.Canvas, [rig.Object]);
        rig.Canvas.UpdateLayout();
        Flush();
    }

    private static TextBox Editor(Rig rig) => Get<TextBox>(rig.Canvas, "_inlineTextEditor");

    private static Result Measure(Scenario scenario)
    {
        var rig = Build(scenario);
        try
        {
            SetZoom(rig, scenario.ZoomPercent);
            var objectImage = Render(rig.Canvas);

            BeginEditing(rig);
            var editor = Editor(rig);
            editor.CaretBrush = Brushes.Transparent; // the caret is a full-height bar in the text colour: not a letter
            editor.Select(0, 0);
            Flush();
            var editorImage = Render(rig.Canvas);

            editor.SelectAll();
            Flush();
            var highlightImage = Render(rig.Canvas);

            return Compare(rig, objectImage, editorImage, highlightImage, objectVisibleUnderEditor: false);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    private static Result MeasureZoomWhileEditing(int startZoom, int endZoom)
    {
        var rig = Build(new Scenario("zoom", "TEXT", startZoom));
        try
        {
            SetZoom(rig, endZoom);
            var reference = Render(rig.Canvas); // what the object looks like at the zoom we will end on

            SetZoom(rig, startZoom);
            BeginEditing(rig);
            Editor(rig).CaretBrush = Brushes.Transparent;
            Editor(rig).Select(0, 0);

            SetZoom(rig, endZoom); // the operator scrolls the wheel with the editor open
            Flush();
            var editorImage = Render(rig.Canvas);

            // With the editor open the object's paths must stay hidden, whatever the view does.
            var paths = Get<Dictionary<SceneObject, List<System.Windows.Shapes.Path>>>(rig.Canvas, "_objectVisuals");
            var visible = paths.TryGetValue(rig.Object, out var list) && list.Any(p => p.Visibility == Visibility.Visible);

            return Compare(rig, reference, editorImage, editorImage, visible);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    private static BitmapSource Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(CanvasWidth, CanvasHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>Near-neutral dark pixels: the text colour and its antialiasing, not the red selection
    /// chrome, not the pale grid.</summary>
    private static bool IsLetterInk(byte b, byte g, byte r)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max < 150 && max - min < 30;
    }

    /// <summary>The pink select-all wash: light and reddish, unlike the saturated dashed frame.</summary>
    private static bool IsHighlight(byte b, byte g, byte r) => r > 200 && g > 130 && g < 215 && r - g > 25 && r - b > 15;

    private static (Ink Ink, bool[] Mask) Scan(byte[] pixels, Rect region, Func<byte, byte, byte, bool> predicate)
    {
        var mask = new bool[CanvasWidth * CanvasHeight];
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue, count = 0;
        for (var y = Math.Max(0, (int)region.Top); y < Math.Min(CanvasHeight, (int)region.Bottom); y++)
        for (var x = Math.Max(0, (int)region.Left); x < Math.Min(CanvasWidth, (int)region.Right); x++)
        {
            var i = (y * CanvasWidth + x) * 4;
            if (!predicate(pixels[i], pixels[i + 1], pixels[i + 2])) continue;
            mask[y * CanvasWidth + x] = true;
            count++;
            left = Math.Min(left, x); right = Math.Max(right, x + 1);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        return count == 0 ? (new Ink(0, 0, 0, 0, 0), mask) : (new Ink(left, top, right, bottom, count), mask);
    }

    private static Result Compare(Rig rig, BitmapSource objectImage, BitmapSource editorImage, BitmapSource highlightImage, bool objectVisibleUnderEditor)
    {
        var (objectInk, objectMask) = Scan(Pixels(objectImage), rig.Region, IsLetterInk);
        var (editorInk, editorMask) = Scan(Pixels(editorImage), rig.Region, IsLetterInk);
        var (highlightInk, _) = Scan(Pixels(highlightImage), rig.Region, IsHighlight);

        // How much of the object's ink has editor ink within a pixel of it.
        var covered = 0;
        for (var y = 1; y < CanvasHeight - 1; y++)
        for (var x = 1; x < CanvasWidth - 1; x++)
        {
            if (!objectMask[y * CanvasWidth + x]) continue;
            var near = false;
            for (var dy = -1; dy <= 1 && !near; dy++)
            for (var dx = -1; dx <= 1 && !near; dx++)
                near = editorMask[(y + dy) * CanvasWidth + x + dx];
            if (near) covered++;
        }

        var coverage = objectInk.Count == 0 ? 0 : covered / (double)objectInk.Count;
        return new Result(objectInk, editorInk, highlightInk, coverage, objectVisibleUnderEditor, objectImage, editorImage, highlightImage);
    }

    // ------------------------------------------------------------------ report

    private static void WriteReport(Scenario scenario, Result result)
    {
        var folder = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);

        var stem = $"{scenario.Name}_{scenario.ZoomPercent}";
        var side = Compose(result);
        using (var stream = File.Create(Path.Combine(folder, stem + ".png")))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(side));
            encoder.Save(stream);
        }

        File.AppendAllText(Path.Combine(folder, "metrics.txt"),
            $"{stem,-34} object=[{result.Object.Left},{result.Object.Top},{result.Object.Right},{result.Object.Bottom}] " +
            $"editor=[{result.Editor.Left},{result.Editor.Top},{result.Editor.Right},{result.Editor.Bottom}] " +
            $"highlight=[{result.Highlight.Left},{result.Highlight.Top},{result.Highlight.Right},{result.Highlight.Bottom}] " +
            $"coverage={result.Coverage:P0} objectShowing={result.ObjectVisibleUnderEditor}{Environment.NewLine}");
    }

    /// <summary>Three panels cropped to the text: the object, the editor, and the two overlaid (object in
    /// blue, editor in red, both in black) so an offset is visible at a glance.</summary>
    private static BitmapSource Compose(Result result)
    {
        var union = new[] { result.Object, result.Editor, result.Highlight }.Where(i => i.HasInk).ToList();
        if (union.Count == 0) return result.EditorImage;
        var pad = 24;
        var left = Math.Max(0, union.Min(i => i.Left) - pad);
        var top = Math.Max(0, union.Min(i => i.Top) - pad);
        var right = Math.Min(CanvasWidth, union.Max(i => i.Right) + pad);
        var bottom = Math.Min(CanvasHeight, union.Max(i => i.Bottom) + pad);
        var w = right - left;
        var h = bottom - top;

        var objectPixels = Pixels(result.ObjectImage);
        var editorPixels = Pixels(result.EditorImage);
        var highlightPixels = Pixels(result.HighlightImage);
        var output = new byte[(w * 4) * (h * 4)];
        var stride = w * 4 * 4;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = ((top + y) * CanvasWidth + left + x) * 4;
            void Put(int panel, byte b, byte g, byte r)
            {
                var o = y * stride + (panel * w + x) * 4;
                output[o] = b; output[o + 1] = g; output[o + 2] = r; output[o + 3] = 255;
            }
            Put(0, objectPixels[i], objectPixels[i + 1], objectPixels[i + 2]);
            Put(1, highlightPixels[i], highlightPixels[i + 1], highlightPixels[i + 2]);

            var o1 = IsLetterInk(objectPixels[i], objectPixels[i + 1], objectPixels[i + 2]);
            var e1 = IsLetterInk(editorPixels[i], editorPixels[i + 1], editorPixels[i + 2]);
            if (o1 && e1) Put(2, 0, 0, 0);
            else if (o1) Put(2, 230, 60, 30);   // object only: blue
            else if (e1) Put(2, 40, 40, 230);   // editor only: red
            else Put(2, 255, 255, 255);
        }
        // pad the unused fourth quarter of the buffer as white
        for (var y = 0; y < h; y++)
        for (var x = 3 * w; x < 4 * w; x++)
        {
            var o = y * stride + x * 4;
            output[o] = output[o + 1] = output[o + 2] = output[o + 3] = 255;
        }
        var bitmap = BitmapSource.Create(w * 4, h, 96, 96, PixelFormats.Pbgra32, null, output, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
