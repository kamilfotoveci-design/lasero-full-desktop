using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Renders the real themed ComboBox and reads back what the CLOSED selection box shows. The
/// bug this pins: DisplayMemberPath worked in the popup but the closed box printed the record dump.
/// Set LASERO_COMBO_SHOTS to a folder to also get PNGs of the closed windows.</summary>
public sealed class ComboBoxClosedBoxRenderTests
{
    private sealed record NoToString(int Id, string Label); // compiler ToString is the record dump

    // One WPF Application per test process: reuse the STA thread InlineTextEditorRenderTests already
    // started (theme, SharedUiStyles and converters loaded) instead of creating a second Application.
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static string ClosedText(ComboBox box) =>
        string.Concat(Descendants<TextBlock>(box).Where(t => t.IsVisible).Select(t => t.Text));

    [Fact]
    public void ClosedBoxShowsDisplayMemberPathForRecordsWithoutToString()
    {
        var text = Ui.Invoke(() =>
        {
            var box = new ComboBox
            {
                ItemsSource = new[] { new NoToString(1, "Zaoblený"), new NoToString(2, "Ostrý") },
                DisplayMemberPath = "Label",
                SelectedIndex = 0,
                Width = 200,
            };
            var window = new Window { Content = box, Width = 300, Height = 120, ShowActivated = false, Left = 100, Top = 100 };
            try
            {
                window.Show(); Flush();
                return ClosedText(box);
            }
            finally { window.Close(); }
        });
        Assert.Contains("Zaoblený", text);
        Assert.DoesNotContain("NoToString", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void OffsetPathWindowClosedJoinTypeBoxShowsTheLabel()
    {
        var texts = Ui.Invoke(() =>
        {
            var source = new SceneObject
            {
                LocalShapes = [], LocalPivot = Position.Zero, LocalBounds = BoundingBox2D.Empty, Name = "t",
            };
            var window = new Lasero.App.OffsetPathWindow(new OffsetPathViewModel([source]))
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100,
            };
            try
            {
                window.Show(); Flush(); window.UpdateLayout();
                var boxes = Descendants<ComboBox>(window).ToList();
                var list = boxes.Select(ClosedText).ToList();
                var folder = Environment.GetEnvironmentVariable("LASERO_COMBO_SHOTS");
                byte[]? bytes = null;
                if (!string.IsNullOrEmpty(folder))
                {
                    var rtb = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(window);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                    Directory.CreateDirectory(folder);
                    using var ms = new MemoryStream(); enc.Save(ms); bytes = ms.ToArray();
                    File.WriteAllBytes(Path.Combine(folder, "offset-closed.png"), bytes);
                }
                return list;
            }
            finally { window.Close(); }
        });
        Assert.NotEmpty(texts);
        Assert.Contains(texts, t => t.Contains("Zaoblený"));
        Assert.All(texts, t => Assert.DoesNotContain("{", t));
    }

    [Fact]
    public void EveryRecordBackedDropdownShowsItsLabelClosedAndOpen()
    {
        var closed = Ui.Invoke(() =>
        {
            var boxes = new List<ComboBox>
            {
                new() { ItemsSource = RasterImportViewModel.DitheringChoices, DisplayMemberPath = "Label", SelectedIndex = 1 },
                new() { ItemsSource = Lasero.Core.Machines.MachineCompatibilityCatalog.All, DisplayMemberPath = "DisplayName", SelectedIndex = 1 },
                new() { ItemsSource = new[] { "COM3", "COM7" }, SelectedIndex = 0 },
                new() { ItemsSource = new[] { 10.0, 1.0, 0.1 }, SelectedIndex = 0 },
            };
            var panel = new StackPanel { Margin = new Thickness(16) };
            foreach (var box in boxes) { box.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(box); }
            var window = new Window
            {
                Content = panel, Width = 420, Height = 260, ShowActivated = false, Left = 100, Top = 100,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Background"],
            };
            try
            {
                window.Show(); Flush(); window.UpdateLayout();
                var texts = boxes.Select(ClosedText).ToList();
                var folder = Environment.GetEnvironmentVariable("LASERO_COMBO_SHOTS");
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                    Save(window, Path.Combine(folder, "gallery-closed.png"));
                    for (var i = 0; i < 2; i++)
                    {
                        boxes[i].IsDropDownOpen = true; Flush();
                        var popup = (System.Windows.Controls.Primitives.Popup)boxes[i].Template.FindName("Popup", boxes[i]);
                        popup.Child.UpdateLayout();
                        Save((FrameworkElement)popup.Child, Path.Combine(folder, $"gallery-open-{i}.png"));
                        boxes[i].IsDropDownOpen = false; Flush();
                    }
                }
                return texts;
            }
            finally { window.Close(); }
        });

        Assert.Contains("Floyd-Steinberg", closed[0]);
        Assert.Contains("AlgoLaser MK2", closed[1]);
        Assert.Equal("COM3", closed[2]);
        Assert.Equal("10", closed[3]);
        Assert.All(closed, t => { Assert.DoesNotContain("{", t); Assert.DoesNotContain("Label =", t); });
    }

    private static void Save(FrameworkElement element, string path)
    {
        var w = (int)Math.Ceiling(element.ActualWidth);
        var h = (int)Math.Ceiling(element.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path); enc.Save(fs);
    }
}
