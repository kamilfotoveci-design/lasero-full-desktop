using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Components;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Renders the real themed controls off-screen and checks the result by pixels, not by reading the
/// XAML: the neutral-first palette promises that nothing in the ordinary chrome is tinted, and a
/// template that quietly paints a pink wash would pass every source-level test. Set
/// LASERO_RENDER_OUT to a folder to keep the PNG specimens.
/// </summary>
[Collection("WpfUi")]
public sealed class PaletteRenderTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    private static Color C(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static T Place<T>(Panel parent, T element, double margin = 6) where T : FrameworkElement
    {
        element.Margin = new Thickness(margin);
        parent.Children.Add(element);
        return element;
    }

    private static (BitmapSource Bitmap, Dictionary<FrameworkElement, Rect> Bounds) Render(
        Panel content, Brush background, double width, string shot)
    {
        content.Width = width;
        var host = new Border { Background = background, Child = content };
        TextOptions.SetTextRenderingMode(host, TextRenderingMode.Grayscale);
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Content = host,
        };

        try
        {
            window.Show();
            Flush();
            host.UpdateLayout();

            var bounds = new Dictionary<FrameworkElement, Rect>();
            foreach (var child in content.Children.OfType<FrameworkElement>())
                bounds[child] = child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            Save(bitmap, shot);
            return (bitmap, bounds);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    private static Color Pixel(BitmapSource bitmap, double x, double y)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(
            (int)Math.Clamp(Math.Round(x), 0, bitmap.PixelWidth - 1),
            (int)Math.Clamp(Math.Round(y), 0, bitmap.PixelHeight - 1), 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
    }

    private static int Spread(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    private static bool Near(Color a, Color b, int tolerance = 2) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The first pixel whose channels differ by more than 8 steps, or null if the whole bitmap
    /// is neutral (white, gray or graphite).</summary>
    private static (int X, int Y, Color Colour)? FirstTintedPixel(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var data = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(data, stride, 0);
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                var c = Color.FromRgb(data[i + 2], data[i + 1], data[i]);
                if (Spread(c) > 8) return (x, y, c);
            }
        return null;
    }

    private static Panel NeutralSpecimen(out Dictionary<string, FrameworkElement> parts)
    {
        var panel = new StackPanel();
        parts = new Dictionary<string, FrameworkElement>();

        parts["primary"] = Place(panel, new Button { Content = "Nový projekt", Style = S("Button.Primary"), Height = 36 });
        parts["secondary"] = Place(panel, new Button { Content = "Importovat", Style = S("Button.Secondary"), Height = 36 });
        parts["default"] = Place(panel, new Button { Content = "Zrušit", Height = 36 });
        parts["ghost"] = Place(panel, new Button { Content = "Vše", Style = S("Button.Link"), Height = 32 });
        parts["field"] = Place(panel, new TextBox { Text = "Překližka 3 mm", Height = 36 });
        parts["checkOn"] = Place(panel, new CheckBox { Content = "Rámování před spuštěním", IsChecked = true });
        parts["checkOff"] = Place(panel, new CheckBox { Content = "Potvrdit reset", IsChecked = false });

        var track = new Border { Style = S("SegmentedTrack"), Height = 36 };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(new RadioButton { Content = "Čára", IsChecked = true, Style = S("Segment"), GroupName = "g" });
        strip.Children.Add(new RadioButton { Content = "Výplň", Style = S("Segment"), GroupName = "g" });
        track.Child = strip;
        parts["segment"] = Place(panel, track);

        var toggle = new ToggleButton
        {
            IsChecked = true,
            Style = S("Toggle.Icon"),
            Content = new IconGlyph { IconData = (Geometry)Application.Current.FindResource("Glyph.Link"), Width = 18, Height = 18 },
        };
        parts["toggleOn"] = Place(panel, toggle);

        parts["progress"] = Place(panel, new ProgressBar { Value = 60, Maximum = 100 });

        var list = new ListBox { Margin = new Thickness(6) };
        list.Items.Add(new ListBoxItem { Content = "Vektor", IsSelected = true });
        list.Items.Add(new ListBoxItem { Content = "Gravírování" });
        panel.Children.Add(list);
        parts["list"] = list;

        parts["badge"] = Place(panel, new StatusBadge { StatusText = "Nepřipojeno", Kind = StatePillKind.Neutral }, 6);
        parts["banner"] = Place(panel, new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.InfoMuted"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.InfoBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.FindResource("Radius.Md"),
            Padding = new Thickness(10),
            Child = new TextBlock { Text = "Bezpečný testovací režim. Laser se nemůže zapnout." },
        });
        return panel;
    }

    // The tint colours as they reach the screen: fills, the deeper text variant, hover and pressed.
    private static readonly string[] TintKeys = { "Brush.Tint", "Brush.Tint.Hover", "Brush.Tint.Pressed", "Brush.TintText", "Brush.TintText.Hover" };

    private static bool IsSolidTint(Color c) => TintKeys.Any(k => Near(c, C(k), 6));

    private static byte[] Pixels(BitmapSource bitmap, out int stride)
    {
        stride = bitmap.PixelWidth * 4;
        var data = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(data, stride, 0);
        return data;
    }

    private static Color At(byte[] data, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return Color.FromRgb(data[i + 2], data[i + 1], data[i]);
    }

    /// <summary>A pastel or tinted wash is a FLAT area of a coloured, non-tint pixel: a pixel with chroma
    /// whose four neighbours are the very same colour. Anti-aliased edges and text fringes never form
    /// one, and solid tint fills are excluded by <see cref="IsSolidTint"/>.</summary>
    internal static (int X, int Y, Color Colour)? FirstFlatWash(BitmapSource bitmap)
    {
        var data = Pixels(bitmap, out var stride);
        for (var y = 1; y < bitmap.PixelHeight - 1; y++)
            for (var x = 1; x < bitmap.PixelWidth - 1; x++)
            {
                var c = At(data, stride, x, y);
                if (Spread(c) <= 8 || IsSolidTint(c)) continue;
                if (Near(c, At(data, stride, x - 1, y), 1) && Near(c, At(data, stride, x + 1, y), 1)
                    && Near(c, At(data, stride, x, y - 1), 1) && Near(c, At(data, stride, x, y + 1), 1))
                    return (x, y, c);
            }
        return null;
    }

    /// <summary>Largest bounding-box area (px) of any connected run of solid tint pixels.</summary>
    internal static int LargestTintBlob(BitmapSource bitmap)
    {
        var data = Pixels(bitmap, out var stride);
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        var seen = new bool[w * h];
        var largest = 0;
        var queue = new Queue<(int X, int Y)>();
        for (var sy = 0; sy < h; sy++)
            for (var sx = 0; sx < w; sx++)
            {
                if (seen[sy * w + sx] || !IsSolidTint(At(data, stride, sx, sy))) continue;
                int minX = sx, maxX = sx, minY = sy, maxY = sy;
                seen[sy * w + sx] = true;
                queue.Enqueue((sx, sy));
                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[ny * w + nx]) continue;
                        if (!IsSolidTint(At(data, stride, nx, ny))) continue;
                        seen[ny * w + nx] = true;
                        queue.Enqueue((nx, ny));
                    }
                }
                largest = Math.Max(largest, (maxX - minX + 1) * (maxY - minY + 1));
            }
        return largest;
    }

    [Fact]
    public void OrdinaryChromeHasSolidTintWhereActiveButNoPastelWash()
    {
        Ui.Invoke(() =>
        {
            var (bitmap, _) = Render(NeutralSpecimen(out _), (Brush)Application.Current.FindResource("Brush.Canvas"), 360, "neutral-specimen");
            var wash = FirstFlatWash(bitmap);
            Assert.True(wash is null,
                wash is null ? "" : $"flat tinted area {Hex(wash.Value.Colour)} at {wash.Value.X},{wash.Value.Y}: a template paints a pastel wash in ordinary chrome");

            // Solid tint is allowed on small active controls only: a toggle pill, a checkbox, a progress fill.
            var blob = LargestTintBlob(bitmap);
            Assert.InRange(blob, 1, 2600);
        });
    }

    [Fact]
    public void PrimaryActionStaysGraphiteAndActiveControlsAreSolidTint()
    {
        Ui.Invoke(() =>
        {
            var (bitmap, bounds) = Render(NeutralSpecimen(out var parts), (Brush)Application.Current.FindResource("Brush.Canvas"), 360, "neutral-fills");
            var graphite = C("Brush.PrimaryAction");
            var tint = C("Brush.Tint");

            var primary = bounds[parts["primary"]];
            Assert.True(Near(Pixel(bitmap, primary.Left + 6, primary.Top + 6), graphite, 4), "primary button fill stays graphite");

            var toggle = bounds[parts["toggleOn"]];
            Assert.True(Near(Pixel(bitmap, toggle.Left + 3, toggle.Top + toggle.Height / 2), tint, 4),
                "an active toggle is a solid tint pill");

            var check = bounds[parts["checkOn"]];
            Assert.True(Near(Pixel(bitmap, check.Left + 3, check.Top + check.Height / 2 - 6), tint, 12),
                "a checked checkbox is solid tint");

            var progress = bounds[parts["progress"]];
            Assert.True(Near(Pixel(bitmap, progress.Left + 8, progress.Top + progress.Height / 2), tint, 4),
                "a progress fill is solid tint");
        });
    }

    [Fact]
    public void StatusChipsAreNeutralGrayWithAColouredDotOnly()
    {
        Ui.Invoke(() =>
        {
            var panel = new StackPanel();
            var kinds = new (StatePillKind Kind, string Dot)[]
            {
                (StatePillKind.Ready, "Brush.Success"),
                (StatePillKind.Busy, "Brush.Signal"),
                (StatePillKind.Warning, "Brush.Warning"),
                (StatePillKind.Error, "Brush.Danger"),
            };
            var badges = kinds.Select(k => (k, Badge: Place(panel, new StatusBadge { StatusText = "Stav", Kind = k.Kind }, 8))).ToList();

            var (bitmap, bounds) = Render(panel, (Brush)Application.Current.FindResource("Brush.Surface"), 160, "status-chips");
            var chip = C("Brush.Field");

            foreach (var (k, badge) in badges)
            {
                var r = bounds[badge];
                // Left padding of the pill: pure surface, so a tinted background would show here.
                Assert.True(Near(Pixel(bitmap, r.Left + 2, r.Top + r.Height / 2), chip, 2),
                    $"{k.Kind} chip is not the neutral field gray");
                // The 6px dot sits 8px in from the left edge.
                var dot = Pixel(bitmap, r.Left + 8 + 3, r.Top + r.Height / 2);
                Assert.True(Near(dot, C(k.Dot), 24), $"{k.Kind} dot is {Hex(dot)}, expected about {Hex(C(k.Dot))}");
            }
        });
    }

    [Fact]
    public void DestructiveButtonIsWhiteWithRedTextNotAPinkSurface()
    {
        Ui.Invoke(() =>
        {
            var panel = new StackPanel();
            var danger = Place(panel, new Button { Content = "Smazat", Style = S("Button.Danger"), Height = 36 });
            var (bitmap, bounds) = Render(panel, (Brush)Application.Current.FindResource("Brush.Surface"), 160, "danger-button");
            var r = bounds[danger];
            Assert.True(Near(Pixel(bitmap, r.Left + 5, r.Top + r.Height / 2), C("Brush.Panel"), 2), "destructive button surface must be white");
        });
    }
}
