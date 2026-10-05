using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Tour;

namespace Lasero.Tests;

/// <summary>
/// Renders the real overlay over a stand-in workspace (coloured blocks named like the real targets, at
/// the real positions) in an off-screen window, and checks the result by geometry and by pixels: the
/// spotlight is the target's bounds, the card is fully on screen and clear of it, and the cut-out really
/// shows the target undimmed. Run at the three window sizes the app is verified at and at 100, 125 and
/// 150 percent scaling. Set LASERO_RENDER_OUT to a folder to keep the PNGs.
/// </summary>
public sealed class TourOverlayRenderTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static readonly TheoryData<double, double, double> Cases = new()
    {
        { 1080, 640, 96 }, { 1080, 640, 120 }, { 1080, 640, 144 },
        { 1366, 768, 96 }, { 1366, 768, 120 }, { 1366, 768, 144 },
        { 1920, 1080, 96 }, { 1920, 1080, 120 }, { 1920, 1080, 144 },
    };

    private static readonly Color Surface = Color.FromRgb(0xF7, 0xF6, 0xF3);

    private sealed class Harness
    {
        public Window Window = null!;
        public Grid Root = null!;
        public TourOverlay Overlay = null!;
        public Dictionary<string, Border> Targets = new();

        public void Arrange(double w, double h)
        {
            Root.Width = w;
            Root.Height = h;
            Place("HomeActions", HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(188, 136, 0, 0), 580, 44, Colors.SeaGreen);
            Place("DesignerRail", HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(0, 60, 0, 0), 56, h - 108, Colors.SteelBlue);
            Place("DesignerInspector", HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 60, 0, 0), 320, h - 108, Colors.Goldenrod);
            Place("RailMaterialsButton", HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(4, 360, 0, 0), 48, 48, Colors.Orchid);
            Place("StripMachineZone", HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(16, 0, 0, 6), 190, 36, Colors.Teal);
            Place("StripJobActions", HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 20, 6), 400, 36, Colors.Chocolate);
            Place("PersistentAvatarLayer", HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 340, 118), 48, 48, Colors.SlateBlue);
        }

        private void Place(string name, HorizontalAlignment ha, VerticalAlignment va, Thickness margin, double w, double h, Color color)
        {
            if (!Targets.TryGetValue(name, out var border))
            {
                border = new Border { Name = name, Background = new SolidColorBrush(color) };
                Targets[name] = border;
                Root.Children.Insert(Root.Children.Count - 1, border); // overlay stays last
            }

            border.HorizontalAlignment = ha;
            border.VerticalAlignment = va;
            border.Margin = margin;
            border.Width = w;
            border.Height = Math.Max(1, h);
        }
    }

    private static Harness Build(double width, double height)
    {
        var harness = new Harness();
        harness.Root = new Grid { Background = new SolidColorBrush(Surface) };
        harness.Overlay = new TourOverlay { ForceStatic = true };
        harness.Root.Children.Add(harness.Overlay);
        harness.Window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Content = harness.Root,
        };
        harness.Arrange(width, height);
        harness.Window.Show();
        Flush();
        return harness;
    }

    private static BitmapSource Render(Harness h, double dpi)
    {
        var w = h.Root.ActualWidth;
        var ht = h.Root.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * dpi / 96), (int)Math.Ceiling(ht * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(h.Root);
        return bitmap;
    }

    private static Color Pixel(BitmapSource bitmap, double x, double y, double dpi)
    {
        var px = (int)Math.Clamp(Math.Round(x * dpi / 96), 0, bitmap.PixelWidth - 1);
        var py = (int)Math.Clamp(Math.Round(y * dpi / 96), 0, bitmap.PixelHeight - 1);
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(px, py, 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
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

    private static bool Near(Color a, Color b, int tolerance = 3) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryStepPlacesTheCardOnScreenAndCutsTheTargetOutAtEverySizeAndScale(double width, double height, double dpi)
    {
        Ui.Invoke(() =>
        {
            var h = Build(width, height);
            try
            {
                Keyboard.ClearFocus();
                h.Overlay.StartTour();
                Flush();

                var view = new Rect(0, 0, width, height);
                for (var step = 0; step < TourSteps.All.Count; step++)
                {
                    var layout = h.Overlay.LastLayout;
                    Assert.NotNull(layout);
                    var current = TourSteps.All[step];
                    var card = layout!.Value.Card;
                    Assert.True(view.Contains(card), $"{current.Id}: card {card} leaves the {width}x{height} window");

                    var target = h.Targets[current.TargetIds[0]];
                    var bounds = target.TransformToVisual(h.Root).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
                    var spot = layout.Value.Spotlight;
                    Assert.NotNull(spot);
                    var expected = Rect.Intersect(Rect.Inflate(bounds, TourLayout.SpotlightPadding, TourLayout.SpotlightPadding), view);
                    Assert.Equal(expected.Left, spot!.Value.Left, 1);
                    Assert.Equal(expected.Top, spot.Value.Top, 1);
                    Assert.Equal(expected.Width, spot.Value.Width, 1);
                    Assert.Equal(expected.Height, spot.Value.Height, 1);
                    Assert.False(card.IntersectsWith(Rect.Inflate(spot.Value, -1, -1)), $"{current.Id}: card covers the spotlight");

                    var bitmap = Render(h, dpi);
                    Assert.Equal((int)Math.Ceiling(width * dpi / 96), bitmap.PixelWidth);
                    Save(bitmap, $"tour-step{step + 1}-{width}x{height}-{dpi}dpi");

                    var targetColor = ((SolidColorBrush)target.Background).Color;
                    // Near the top edge, away from the stand-in blocks that sit on top of the rail.
                    var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + Math.Min(8, bounds.Height / 2));
                    Assert.True(Near(Pixel(bitmap, center.X, center.Y, dpi), targetColor),
                        $"{current.Id}: the cut-out must show the target undimmed, got {Pixel(bitmap, center.X, center.Y, dpi)} expected {targetColor}");

                    // Somewhere in the dimmed area that is neither the card nor another block: darker than the surface.
                    var dimmed = FindDimmedSample(h, card, spot.Value, width, height);
                    var dimColor = Pixel(bitmap, dimmed.X, dimmed.Y, dpi);
                    Assert.True(Luma(dimColor) < Luma(Surface) - 40, $"{current.Id}: area outside the spotlight is not dimmed ({dimColor})");

                    // The card is opaque white inside its border.
                    var cardPixel = Pixel(bitmap, card.Left + 8, card.Top + card.Height / 2, dpi);
                    Assert.True(Near(cardPixel, Colors.White, 4), $"{current.Id}: card surface is {cardPixel}");

                    h.Overlay.Session!.Next();
                    Flush();
                }

                // Closing card: centred, no spotlight.
                Assert.True(h.Overlay.Session!.IsFinishCard);
                var finish = h.Overlay.LastLayout!.Value;
                Assert.Null(finish.Spotlight);
                Assert.True(view.Contains(finish.Card));
                Save(Render(h, dpi), $"tour-finish-{width}x{height}-{dpi}dpi");
            }
            finally { h.Window.Close(); }
        });
    }

    private static Point FindDimmedSample(Harness h, Rect card, Rect spot, double width, double height)
    {
        var blocks = h.Targets.Values
            .Select(t => t.TransformToVisual(h.Root).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight)))
            .ToList();
        for (var y = 8.0; y < height; y += 24)
        for (var x = 8.0; x < width; x += 24)
        {
            var p = new Point(x, y);
            if (card.Contains(p) || Rect.Inflate(card, 8, 8).Contains(p)) continue;
            if (Rect.Inflate(spot, 12, 12).Contains(p)) continue;
            if (blocks.Any(b => Rect.Inflate(b, 4, 4).Contains(p))) continue;
            return p;
        }

        throw new InvalidOperationException("no free area to sample");
    }

    [Fact]
    public void ResizingTheWindowRepositionsTheSpotlightAndKeepsTheCardOnScreen()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                h.Overlay.StartTour();
                Flush();
                h.Overlay.Session!.Next(); // designer rail
                h.Overlay.Session.Next();  // inspector
                Flush();
                var before = h.Overlay.LastLayout!.Value;

                h.Arrange(1080, 640);
                Flush();
                var after = h.Overlay.LastLayout!.Value;
                var view = new Rect(0, 0, 1080, 640);

                Assert.NotEqual(before.Spotlight, after.Spotlight);
                Assert.True(view.Contains(after.Card));
                var inspector = h.Targets["DesignerInspector"];
                var bounds = inspector.TransformToVisual(h.Root).TransformBounds(new Rect(0, 0, inspector.ActualWidth, inspector.ActualHeight));
                Assert.Equal(bounds.Left - TourLayout.SpotlightPadding, after.Spotlight!.Value.Left, 1);
                Assert.Equal(bounds.Height + 2 * TourLayout.SpotlightPadding, after.Spotlight.Value.Height, 1);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void MissingTargetStillShowsTheStepAsACentredCardInsteadOfSkippingIt()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                h.Targets["HomeActions"].Visibility = Visibility.Collapsed;
                Flush();
                h.Overlay.StartTour();
                Flush();

                var layout = h.Overlay.LastLayout!.Value;
                Assert.Null(layout.Spotlight);
                Assert.Equal(TourPlacement.Center, layout.Placement);
                Assert.Equal("1 z 7", h.Overlay.CounterText.Text);
                Assert.Equal(Visibility.Collapsed, h.Overlay.SpotRing.Visibility);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void CardTextShowsCounterTitleBodyAndTheStepSpecificExtras()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                h.Overlay.StartTour();
                Flush();
                Assert.Equal("1 z 7", h.Overlay.CounterText.Text);
                Assert.Equal(TourSteps.All[0].Title, string.Concat(h.Overlay.TitleText.Inlines.Select(i => ((System.Windows.Documents.Run)i).Text)));
                Assert.Equal(TourSteps.All[0].Body, h.Overlay.BodyText.Text);
                Assert.Equal(Visibility.Collapsed, h.Overlay.BackButton.Visibility);
                Assert.Equal(Visibility.Collapsed, h.Overlay.SafetyBox.Visibility);

                h.Overlay.Session!.Next(); h.Overlay.Session.Next();
                Flush();
                Assert.Equal("3 z 7", h.Overlay.CounterText.Text);
                Assert.Equal(Visibility.Visible, h.Overlay.PointsHost.Visibility);
                Assert.Equal(3, h.Overlay.PointsHost.Children.Count);
                Assert.Equal(Visibility.Visible, h.Overlay.BackButton.Visibility);

                h.Overlay.Session.Next(); h.Overlay.Session.Next(); h.Overlay.Session.Next();
                Flush();
                Assert.Equal("6 z 7", h.Overlay.CounterText.Text);
                Assert.Equal(Visibility.Visible, h.Overlay.SafetyBox.Visibility);
                Assert.Equal(TourSteps.All[5].SafetyNote, h.Overlay.SafetyText.Text);

                h.Overlay.Session.Next(); h.Overlay.Session.Next();
                Flush();
                Assert.Equal(Visibility.Visible, h.Overlay.ReplayButton.Visibility);
                Assert.Equal("Dokončit", h.Overlay.NextLabel.Text);
                Assert.Equal(Visibility.Collapsed, h.Overlay.SkipButton.Visibility);
            }
            finally { h.Window.Close(); }
        });
    }

    private static KeyEventArgs Press(UIElement target, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    [Fact]
    public void ArrowKeysEscAndSwallowedKeysAreRoutedByTheOverlay()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                Keyboard.ClearFocus();
                var ended = new List<TourOutcome>();
                var fallbacks = 0;
                h.Overlay.TourEnded += ended.Add;
                h.Overlay.FocusFallback = () => fallbacks++;
                h.Overlay.StartTour();
                Flush();

                var right = Press(h.Overlay, Key.Right);
                h.Overlay.RaiseEvent(right);
                Assert.True(right.Handled);
                Assert.Equal(1, h.Overlay.Session!.Index);

                var left = Press(h.Overlay, Key.Left);
                h.Overlay.RaiseEvent(left);
                Assert.Equal(0, h.Overlay.Session.Index);

                // A canvas shortcut must not get through while the tour is open.
                foreach (var key in new[] { Key.Delete, Key.V, Key.Z, Key.F })
                {
                    var swallowed = Press(h.Overlay, key);
                    h.Overlay.RaiseEvent(swallowed);
                    Assert.True(swallowed.Handled, $"{key} must not reach the window");
                }

                var esc = Press(h.Overlay, Key.Escape);
                h.Overlay.RaiseEvent(esc);
                Assert.True(esc.Handled);
                Assert.Equal([TourOutcome.Skipped], ended);
                Assert.False(h.Overlay.IsOpen);
                Assert.Equal(1, fallbacks);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void WelcomeShowsStaticEndStateAndEscSkipsItWithTheRememberedChoice()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                var answers = new List<WelcomeChoice>();
                h.Overlay.WelcomeAnswered += answers.Add;
                h.Overlay.ShowWelcome();
                Flush();

                Assert.True(h.Overlay.IsWelcomeVisible);
                Assert.Equal(220, h.Overlay.EtchLine.Width);
                Assert.Null(h.Overlay.WordmarkImage.Clip);
                Assert.Equal(0, h.Overlay.Beam.Opacity);
                Assert.Equal(1, h.Overlay.WelcomeCard.Opacity);
                var view = new Rect(0, 0, 1366, 768);
                var card = h.Overlay.WelcomeCard.TransformToVisual(h.Root).TransformBounds(new Rect(0, 0, h.Overlay.WelcomeCard.ActualWidth, h.Overlay.WelcomeCard.ActualHeight));
                Assert.True(view.Contains(card));
                Save(Render(h, 96), "tour-welcome-1366x768");

                var esc = Press(h.Overlay, Key.Escape);
                h.Overlay.RaiseEvent(esc);
                Assert.Equal([WelcomeChoice.Skipped], answers);
                Assert.False(h.Overlay.IsOpen);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void WelcomeFitsTheSmallestSupportedWindow()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1080, 640);
            try
            {
                h.Overlay.ShowWelcome();
                Flush();
                var card = h.Overlay.WelcomeCard.TransformToVisual(h.Root).TransformBounds(new Rect(0, 0, h.Overlay.WelcomeCard.ActualWidth, h.Overlay.WelcomeCard.ActualHeight));
                Assert.True(new Rect(0, 0, 1080, 640).Contains(card), card.ToString());
                Save(Render(h, 96), "tour-welcome-1080x640");
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void StartingTheTourFromTheWelcomeReportsTheChoiceAndSwitchesLayers()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                var answers = new List<WelcomeChoice>();
                var screens = new List<Lasero.App.ViewModels.AppScreen>();
                h.Overlay.WelcomeAnswered += answers.Add;
                h.Overlay.Navigate = screens.Add;
                h.Overlay.CurrentScreen = () => Lasero.App.ViewModels.AppScreen.Home;
                h.Overlay.ShowWelcome();
                Flush();

                h.Overlay.StartButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Flush();

                Assert.Equal([WelcomeChoice.StartedTour], answers);
                Assert.True(h.Overlay.IsCoachVisible);
                Assert.False(h.Overlay.IsWelcomeVisible);
                Assert.Equal(Lasero.App.ViewModels.AppScreen.Home, screens[0]); // step 1 asks for Home

                // Finishing returns to the screen the tour started on.
                var ended = new List<TourOutcome>();
                h.Overlay.TourEnded += ended.Add;
                for (var i = 0; i < 8; i++) h.Overlay.Session!.Next();
                Assert.Equal([TourOutcome.Finished], ended);
                Assert.Equal(Lasero.App.ViewModels.AppScreen.Home, screens[^1]);
                Assert.False(h.Overlay.IsOpen);
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void TourNavigatesToTheScreenEachStepNeeds()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                var screens = new List<Lasero.App.ViewModels.AppScreen>();
                h.Overlay.Navigate = screens.Add;
                h.Overlay.StartTour();
                Flush();
                for (var i = 0; i < 7; i++) { h.Overlay.Session!.Next(); Flush(); }

                var expected = TourSteps.All.Select(s => s.Screen).ToList();
                Assert.Equal(expected, screens.Take(expected.Count).ToList());
            }
            finally { h.Window.Close(); }
        });
    }

    [Fact]
    public void CancelClosesTheOverlayAsSkippedSoARunningJobIsNeverHiddenBehindIt()
    {
        Ui.Invoke(() =>
        {
            var h = Build(1366, 768);
            try
            {
                var ended = new List<TourOutcome>();
                h.Overlay.TourEnded += ended.Add;
                h.Overlay.StartTour();
                Flush();
                h.Overlay.Cancel();
                Assert.Equal([TourOutcome.Skipped], ended);
                Assert.False(h.Overlay.IsOpen);
            }
            finally { h.Window.Close(); }
        });
    }
}

public sealed class TipChipRenderTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    [Fact]
    public void ChipRendersOneBorderedCardWithTheTipText()
    {
        Ui.Invoke(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "lasero-chip-" + Guid.NewGuid().ToString("N"), "settings.json");
            var store = new Lasero.App.AppSettingsStore(path);
            store.Load();
            var service = new GuidanceService(store);
            service.SwitchAccount("u", false);
            service.RecordWelcome(WelcomeChoice.Skipped);
            var chip = new TipChip { ForceStatic = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
            chip.Attach(service);
            var root = new Grid { Width = 700, Height = 120, Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF6, 0xF3)) };
            root.Children.Add(chip);
            var window = new Window { WindowStyle = WindowStyle.None, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Content = root };
            try
            {
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(service.TryOfferTip(TipCatalog.Import));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Visible, chip.Visibility);
                Assert.Equal(TipCatalog.Import, service.CurrentTip!.Id);
                Assert.Equal("Velikost se mění úchyty, poloha přetažením.", chip.TipText.Text);

                var rtb = new RenderTargetBitmap(700, 120, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var s = File.Create(Path.Combine(dir, "tip-chip.png"));
                    enc.Save(s);
                }

                service.DismissTip();
                Assert.Equal(Visibility.Collapsed, chip.Visibility);
            }
            finally { window.Close(); }
        });
    }
}
