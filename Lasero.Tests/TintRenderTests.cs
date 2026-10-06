using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
/// The tint, the depth and the identification tiles (DESIGN.md "Color", "Elevation", "Tiles").
/// Source-level tests read the theme back; render tests draw the real themed controls off-screen
/// (never a live window, never synthetic input) and check pixels. Set LASERO_RENDER_OUT to keep the
/// PNG specimens.
/// </summary>
[Collection("WpfUi")]
public sealed class TintRenderTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }

    private static string Theme() => XmlComment.Replace(File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml")), "");

    private static string AppFile(params string[] parts) =>
        XmlComment.Replace(File.ReadAllText(Path.Combine(new[] { Root(), "Lasero.App" }.Concat(parts).ToArray())), "");

    // ------------------------------------------------------------------ colour maths

    private static double Linear(int c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Lum(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Lum(a), Lum(b));
        if (x < y) (x, y) = (y, x);
        return (x + 0.05) / (y + 0.05);
    }

    private static Color Hex(string hex) =>
        Color.FromRgb(byte.Parse(hex[..2], NumberStyles.HexNumber), byte.Parse(hex[2..4], NumberStyles.HexNumber), byte.Parse(hex[4..6], NumberStyles.HexNumber));

    private static Dictionary<string, Color> Brushes()
    {
        var map = new Dictionary<string, Color>();
        foreach (Match m in Regex.Matches(Theme(), @"<SolidColorBrush\s+x:Key=""(?<k>[^""]+)""\s+Color=""#(?<h>[0-9A-Fa-f]{6,8})"""))
        {
            var h = m.Groups["h"].Value;
            map[m.Groups["k"].Value] = Hex(h[^6..]);
        }
        return map;
    }

    private static Color Tok(string key) => Brushes()[key];

    private static bool Near(Color a, Color b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    // ------------------------------------------------------------------ tint tokens and contrast

    [Fact]
    public void TintTokensExistWithTheSpecifiedValues()
    {
        Assert.Equal(Hex("E5302B"), Tok("Brush.Tint"));
        Assert.Equal(Hex("CF2A26"), Tok("Brush.Tint.Hover"));
        Assert.Equal(Hex("B9231F"), Tok("Brush.Tint.Pressed"));
        Assert.Equal(Hex("FFFFFF"), Tok("Brush.OnTint"));
    }

    [Fact]
    public void FocusRingIsTwoPixelTintWithAOnePixelWhiteGapAndWorksOnWhiteAndGray()
    {
        var ring = Tok("Brush.FocusRing");
        Assert.Equal(Tok("Brush.Tint"), ring);
        foreach (var ground in new[] { "Brush.Surface", "Brush.Canvas", "Brush.FocusGap" })
            Assert.True(Contrast(ring, Tok(ground)) >= 3.0, $"focus ring on {ground} is {Contrast(ring, Tok(ground)):0.00}:1");
        // On a graphite fill the ring is separated from it by the white gap, so the 3:1 pair is ring vs gap.
        Assert.Equal(Hex("FFFFFF"), Tok("Brush.FocusGap"));

        var theme = Theme();
        Assert.Contains("x:Name=\"FocusRing\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FocusGap\"", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedTextStaysReadableOnTheSelectionHighlight()
    {
        // 30% tint over the field gray, then graphite text on it.
        var field = Tok("Brush.Field");
        var tint = Tok("Brush.Tint");
        var blended = Color.FromRgb(
            (byte)Math.Round(tint.R * 0.3 + field.R * 0.7),
            (byte)Math.Round(tint.G * 0.3 + field.G * 0.7),
            (byte)Math.Round(tint.B * 0.3 + field.B * 0.7));
        Assert.True(Contrast(Tok("Brush.TextPrimary"), blended) >= 7.0, $"text on selection is {Contrast(Tok("Brush.TextPrimary"), blended):0.0}:1");
    }

    [Fact]
    public void CaretAndSelectionBrushesOfTextInputsAreTheTint()
    {
        Ui.Invoke(() =>
        {
            var textBox = new TextBox { Style = (Style)Application.Current.FindResource(typeof(TextBox)) };
            var passwordBox = new PasswordBox { Style = (Style)Application.Current.FindResource(typeof(PasswordBox)) };
            foreach (var (caret, selection) in new[]
            {
                (textBox.CaretBrush, textBox.SelectionBrush),
                (passwordBox.CaretBrush, passwordBox.SelectionBrush),
            })
            {
                Assert.Equal(Color.FromRgb(0xE5, 0x30, 0x2B), ((SolidColorBrush)caret).Color);
                var colour = ((SolidColorBrush)selection).Color;
                Assert.Equal(0xE5, colour.R);
                Assert.InRange(colour.A, 60, 95);
            }
        });
    }

    // ------------------------------------------------------------------ depth tokens

    [Fact]
    public void ElevationTokensMatchTheCardShadowSpec()
    {
        var theme = Theme();
        double D(string key)
        {
            var m = Regex.Match(theme, $@"<system:Double x:Key=""{Regex.Escape(key)}"">(?<v>[0-9.]+)</system:Double>");
            Assert.True(m.Success, $"token {key} missing");
            return double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
        }

        // 0 1px 2px at 6% plus 0 8px 24px at 6%.
        Assert.Equal(1, D("Elevation.Card.Contact.OffsetY"));
        Assert.Equal(2, D("Elevation.Card.Contact.Blur"));
        Assert.Equal(0.06, D("Elevation.Card.Contact.Opacity"));
        Assert.Equal(8, D("Elevation.Card.Ambient.OffsetY"));
        Assert.Equal(24, D("Elevation.Card.Ambient.Blur"));
        Assert.Equal(0.06, D("Elevation.Card.Ambient.Opacity"));

        // The code defaults are the same numbers, so a missing resource cannot change the look.
        Assert.Equal(new ShadowLayer(1, 2, 0.06), ElevationTokens.CardContactDefault);
        Assert.Equal(new ShadowLayer(8, 24, 0.06), ElevationTokens.CardAmbientDefault);
    }

    [Fact]
    public void PopoversAndDialogsSitOneLevelAboveCards()
    {
        var theme = Theme();
        var dropdown = Regex.Match(theme, @"x:Key=""Shadow\.Dropdown""[^>]*Opacity=""(?<o>[0-9.]+)"" BlurRadius=""(?<b>[0-9.]+)"" ShadowDepth=""(?<d>[0-9.]+)""");
        Assert.True(dropdown.Success);
        Assert.Equal(0.12, double.Parse(dropdown.Groups["o"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(32, double.Parse(dropdown.Groups["b"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(12, double.Parse(dropdown.Groups["d"].Value, CultureInfo.InvariantCulture));

        var modal = Regex.Match(theme, @"x:Key=""Shadow\.Modal""[^>]*Opacity=""(?<o>[0-9.]+)"" BlurRadius=""(?<b>[0-9.]+)""");
        Assert.True(double.Parse(modal.Groups["b"].Value, CultureInfo.InvariantCulture) > 32, "a dialog is above a popover");
    }

    [Fact]
    public void CardsHaveTheTwelvePixelRadiusAndTheDesignCanvasNeverCarriesAnEffect()
    {
        Assert.Contains("<CornerRadius x:Key=\"Radius.Lg\">12</CornerRadius>", File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml")), StringComparison.Ordinal);
        var shared = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "SharedUiStyles.xaml"));
        Assert.Matches(@"x:Key=""Card""[\s\S]*?Radius\.Lg", shared);
        Assert.Matches(@"x:Key=""InspectorSection""[\s\S]*?Radius\.Lg", shared);

        // The ElevatedBorder never uses an Effect, and nothing in it touches the scene canvas.
        var source = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Components", "ElevatedBorder.cs"));
        Assert.DoesNotContain("DropShadowEffect", source.Replace("DropShadowEffect: an Effect", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Effect =", source, StringComparison.Ordinal);
        foreach (var canvas in new[] { "SceneCanvas.xaml", "SceneCanvas.xaml.cs" })
        {
            var text = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Controls", canvas));
            Assert.DoesNotContain("ElevatedBorder", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CardStylesAreAppliedThroughElevatedBorderOnHomeSettingsAndMaterials()
    {
        foreach (var file in new[] { "Views/HomeView.xaml", "SettingsWindow.xaml", "MaterialsWindow.xaml", "Views/DeviceView.xaml", "DeviceSettingsWindow.xaml" })
        {
            var text = AppFile(file.Split('/'));
            Assert.DoesNotMatch(@"<Border\b[^>]*Style=""\{StaticResource (Card|InspectorSection|ThumbnailCard|MetricCard)\}""", text);
            Assert.Contains("components:ElevatedBorder", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ tiles

    private static readonly string[] CoreTones = { "Red", "Orange", "Amber", "Green", "Teal", "Blue", "Indigo", "Graphite" };

    [Fact]
    public void TilePaletteIsDefinedAndEveryTileKeepsThreeToOneAgainstItsWhiteIcon()
    {
        var all = Brushes();
        foreach (var tone in Enum.GetNames<TileTone>())
        {
            Assert.True(all.ContainsKey("Brush.Tile." + tone), $"Brush.Tile.{tone} is missing");
            Assert.True(Contrast(Hex("FFFFFF"), all["Brush.Tile." + tone]) >= 3.0,
                $"white on Brush.Tile.{tone} is {Contrast(Hex("FFFFFF"), all["Brush.Tile." + tone]):0.00}:1");
        }

        foreach (var tone in CoreTones) Assert.Contains(tone, Enum.GetNames<TileTone>());

        // Desaturated enough to stay premium: no tile is a neon (HSV saturation under 0.75, no pure primaries).
        foreach (var tone in Enum.GetNames<TileTone>())
        {
            var c = all["Brush.Tile." + tone];
            var max = Math.Max(c.R, Math.Max(c.G, c.B));
            var min = Math.Min(c.R, Math.Min(c.G, c.B));
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            Assert.True(saturation <= 0.85, $"Tile.{tone} saturation {saturation:0.00}");
        }
    }

    [Fact]
    public void TilesAreFlatSolidSquaresWithTheWhiteIconAndNoGradientOrGlow()
    {
        var xaml = AppFile("Components", "IconTile.xaml");
        Assert.Contains("Radius.Md", xaml, StringComparison.Ordinal);
        Assert.Contains("Brush.OnTint", xaml, StringComparison.Ordinal);
        foreach (var banned in new[] { "Gradient", "Effect", "DropShadow", "Opacity=" })
            Assert.DoesNotContain(banned, xaml, StringComparison.Ordinal);
        Assert.Contains("PropertyMetadata(28.0", File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Components", "IconTile.xaml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolbarAndRailIconsStayLineIconsAndNoScreenShowsMoreThanFourTileColours()
    {
        foreach (var file in new[] { "Views/DesignerToolRail.xaml", "Views/SelectionPropertiesBar.xaml", "Views/NodeEditToolbar.xaml", "Views/CanvasViewControls.xaml", "Components/WindowTitleBar.xaml" })
            Assert.DoesNotContain("IconTile", AppFile(file.Split('/')), StringComparison.Ordinal);

        foreach (var file in new[] { "Views/HomeView.xaml", "SettingsWindow.xaml", "DeviceSettingsWindow.xaml", "MaterialsWindow.xaml", "Views/DeviceView.xaml" })
        {
            var tones = Regex.Matches(AppFile(file.Split('/')), @"IconTile[^>]*\bTone=""(?<t>[A-Za-z]+)""").Select(m => m.Groups["t"].Value).Distinct().ToList();
            Assert.True(tones.Count <= 4, $"{file} shows {tones.Count} tile colours: {string.Join(", ", tones)}");
        }
    }

    [Fact]
    public void MaterialCategoriesMapToTheirTileColours()
    {
        Assert.Equal(TileTone.Orange, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("wood"));
        Assert.Equal(TileTone.Amber, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("plywood"));
        Assert.Equal(TileTone.Blue, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("acrylic"));
        Assert.Equal(TileTone.Brown, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("leather"));
        Assert.Equal(TileTone.Gray, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("paper"));
        Assert.Equal(TileTone.Steel, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("metal"));
        Assert.Equal(TileTone.Gray, Lasero.App.ViewModels.MaterialSwatchCardViewModel.ToneFor("something-new"));
    }

    // ------------------------------------------------------------------ off-screen rendering

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Color Res(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static BitmapSource Draw(FrameworkElement content, Brush background, string shot, out Rect bounds, double pad = 30)
    {
        var host = new Border { Background = background, Padding = new Thickness(pad), Child = content };
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
            bounds = content.TransformToAncestor(host).TransformBounds(new Rect(content.RenderSize));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            Save(bitmap, shot);
            return bitmap;
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

    private static Color Px(BitmapSource bitmap, double x, double y)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Clamp(Math.Floor(x), 0, bitmap.PixelWidth - 1), (int)Math.Clamp(Math.Floor(y), 0, bitmap.PixelHeight - 1), 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
    }

    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    [Fact]
    public void EveryIconTileRendersItsToneWithRoundedCornersAndAWhiteGlyph()
    {
        Ui.Invoke(() =>
        {
            var row = new WrapPanel { Width = 340 };
            foreach (var tone in Enum.GetValues<TileTone>())
                row.Children.Add(new IconTile { Tone = tone, IconData = (Geometry)Application.Current.FindResource("Glyph.Materials"), Margin = new Thickness(6) });

            var bitmap = Draw(row, (Brush)Application.Current.FindResource("Brush.Surface"), "tiles-palette", out _);
            var tiles = row.Children.OfType<IconTile>().ToList();
            var host = (FrameworkElement)row.Parent;
            foreach (var tile in tiles)
            {
                var b = tile.TransformToAncestor(host).TransformBounds(new Rect(tile.RenderSize));
                var left = b.Left;
                var top = b.Top;
                Assert.Equal(28, Math.Round(b.Width));
                Assert.Equal(28, Math.Round(b.Height));
                // The tone fill sits just inside the left edge, mid-height.
                var fill = Px(bitmap, left + 3, top + 14);
                Assert.True(Near(fill, Res("Brush.Tile." + tile.Tone), 3), $"{tile.Tone}: fill {fill} is not its token");
                // The corner pixel is the white surface: the tile is rounded, not a hard square.
                var corner = Px(bitmap, left + 0.5, top + 0.5);
                Assert.True(Near(corner, Res("Brush.Surface"), 24), $"{tile.Tone}: corner is {corner}, the tile is not rounded");
            }
        });
    }

    [Fact]
    public void ElevatedBorderCastsASoftNeutralShadowBelowAndFadesToTheCanvas()
    {
        Ui.Invoke(() =>
        {
            var card = new ElevatedBorder
            {
                Width = 200,
                Height = 100,
                Background = (Brush)Application.Current.FindResource("Brush.Panel"),
                BorderBrush = (Brush)Application.Current.FindResource("Brush.PanelBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)Application.Current.FindResource("Radius.Lg"),
                Child = new TextBlock { Text = "Karta", Margin = new Thickness(12) },
            };
            var canvas = (SolidColorBrush)Application.Current.FindResource("Brush.Canvas");
            var bitmap = Draw(card, canvas, "elevated-card", out var b, pad: 60);

            Assert.Null(card.Effect);
            var midX = b.Left + b.Width / 2;
            var canvasColour = canvas.Color;
            var below = Px(bitmap, midX, b.Bottom + 6);
            var farBelow = Px(bitmap, midX, b.Bottom + 40);
            var above = Px(bitmap, midX, b.Top - 4);

            // The ambient shadow darkens the canvas just below the card (offset 8, blur 24)...
            Assert.True(below.R < canvasColour.R - 4, $"shadow below is {below}, canvas {canvasColour}");
            // ...stays neutral (no coloured glow)...
            Assert.True(Math.Max(below.R, Math.Max(below.G, below.B)) - Math.Min(below.R, Math.Min(below.G, below.B)) <= 6, $"coloured shadow {below}");
            // ...is heavier below than above (it is offset downwards)...
            Assert.True(below.R < above.R, $"below {below} should be darker than above {above}");
            // ...and is gone well away from the card, and never exceeds a soft low-opacity level.
            Assert.True(Near(farBelow, canvasColour, 2), $"shadow still present 40px below: {farBelow}");
            Assert.True(below.R > canvasColour.R - 30, $"shadow too heavy: {below}");
            // Inside the card it is the white panel, untouched by the shadow.
            Assert.True(Near(Px(bitmap, b.Left + 6, b.Top + 50), Res("Brush.Panel"), 2));
        });
    }

    [Fact]
    public void ElevationNoneDrawsNoShadow()
    {
        Ui.Invoke(() =>
        {
            var card = new ElevatedBorder
            {
                Elevation = ElevationLevel.None,
                Width = 160,
                Height = 80,
                Background = (Brush)Application.Current.FindResource("Brush.Panel"),
            };
            var canvas = (SolidColorBrush)Application.Current.FindResource("Brush.Canvas");
            var bitmap = Draw(card, canvas, "elevated-card-none", out var b, pad: 50);
            Assert.True(Near(Px(bitmap, b.Left + b.Width / 2, b.Bottom + 6), canvas.Color, 1));
        });
    }

    [Fact]
    public void CheckboxFocusRingIsTwoPixelTintOutsideAOnePixelGapEvenWhenChecked()
    {
        Ui.Invoke(() =>
        {
            foreach (var isChecked in new[] { false, true })
            {
                var box = new CheckBox { Content = "Rámování", IsChecked = isChecked };
                FocusVisual.SetIsVisible(box, true);
                var bitmap = Draw(box, (Brush)Application.Current.FindResource("Brush.Surface"), "checkbox-focus-" + (isChecked ? "on" : "off"), out var b);
                var tint = Res("Brush.Tint");
                var y = b.Top + b.Height / 2;
                // Walk right from the control's left edge: ring (tint) then gap (surface) then the box outline/fill.
                var run = Enumerable.Range(0, 14).Select(i => Px(bitmap, b.Left - 6 + i, y)).ToList();
                var firstRing = run.FindIndex(c => Near(c, tint, 40));
                Assert.True(firstRing >= 0, $"no tint ring found (checked={isChecked})");
                Assert.True(run.Skip(firstRing).Count(c => Near(c, tint, 40)) >= 2, "the ring is two pixels wide");
                Assert.Contains(run.Skip(firstRing + 2), c => Near(c, Res("Brush.Surface"), 12));
            }
        });
    }

    [Fact]
    public void ButtonFocusRingKeepsAWhiteGapSoItShowsOnTheGraphitePrimaryButton()
    {
        Ui.Invoke(() =>
        {
            var button = new Button { Content = "Nový projekt", Style = S("Button.Primary"), Width = 160, Height = 40 };
            FocusVisual.SetIsVisible(button, true);
            var bitmap = Draw(button, (Brush)Application.Current.FindResource("Brush.Canvas"), "button-focus", out var b);
            var y = b.Top + b.Height / 2;
            var tint = Res("Brush.Tint");
            Assert.True(Near(Px(bitmap, b.Left + 0.5, y), tint, 40), "outer pixel is the ring");
            Assert.True(Near(Px(bitmap, b.Left + 1.5, y), tint, 40), "ring is 2px");
            Assert.True(Near(Px(bitmap, b.Left + 2.5, y), Res("Brush.FocusGap"), 12), "1px white gap");
            Assert.True(Near(Px(bitmap, b.Left + 5.5, y), Res("Brush.PrimaryAction"), 12), "graphite fill is still graphite");
        });
    }

    [Fact]
    public void SliderFillAndThumbRimAreSolidTintAndTheRestOfTheGrooveIsNeutral()
    {
        Ui.Invoke(() =>
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = 60, Width = 200 };
            var bitmap = Draw(slider, (Brush)Application.Current.FindResource("Brush.Surface"), "slider-states", out var b);
            var y = b.Top + b.Height / 2;
            Assert.True(Near(Px(bitmap, b.Left + 40, y), Res("Brush.Tint"), 4), "fill is solid tint");
            var right = Px(bitmap, b.Left + 175, y);
            Assert.True(Near(right, Res("Brush.ToggleTrackOff"), 4), $"unfilled groove is neutral, got {right}");
        });
    }

    [Fact]
    public void ToggleCheckboxRadioStatesRenderTheSpecimenSheet()
    {
        Ui.Invoke(() =>
        {
            var sheet = new StackPanel { Width = 380 };
            T(sheet, new ToggleButton { Style = S("Toggle.Icon"), IsChecked = true, Content = new IconGlyph { IconData = (Geometry)Application.Current.FindResource("Glyph.Link"), Width = 18, Height = 18 } });
            T(sheet, new ToggleButton { Style = S("Toggle.Icon"), IsChecked = false, Content = new IconGlyph { IconData = (Geometry)Application.Current.FindResource("Glyph.Link"), Width = 18, Height = 18 } });
            T(sheet, new CheckBox { Content = "Zapnuto", IsChecked = true });
            T(sheet, new CheckBox { Content = "Vypnuto", IsChecked = false });
            T(sheet, new Slider { Value = 30, Maximum = 100, Width = 200 });
            T(sheet, new ProgressBar { Value = 45, Maximum = 100, Width = 200 });
            T(sheet, new Button { Content = "Další tip", Style = S("Button.Quiet") });
            T(sheet, new Button { Content = "Vše", Style = S("Button.Link") });
            T(sheet, new TextBox { Text = "Překližka 3 mm" });
            var bitmap = Draw(sheet, (Brush)Application.Current.FindResource("Brush.Surface"), "tint-specimen-sheet", out _);
            // The sheet must contain solid tint (active parts) and still no flat pastel wash.
            Assert.True(PaletteRenderTests.FirstFlatWash(bitmap) is null, "pastel wash in the specimen");
            Assert.True(PaletteRenderTests.LargestTintBlob(bitmap) > 0);
        });

        static void T(Panel parent, FrameworkElement element)
        {
            element.Margin = new Thickness(0, 4, 0, 4);
            element.HorizontalAlignment = HorizontalAlignment.Left;
            parent.Children.Add(element);
        }
    }

    [Fact]
    public void QuietAndLinkButtonsAreTintTextWithADeeperHoverAndNoUnderline()
    {
        var shared = XmlComment.Replace(File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "SharedUiStyles.xaml")), "");
        Assert.Matches(@"x:Key=""Button\.Quiet""[\s\S]*?Brush\.TintText\}""", shared);
        Assert.Matches(@"x:Key=""Button\.Link""[\s\S]*?Brush\.TintText\}""", shared);
        Assert.Contains("Brush.TintText.Hover", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Underline", shared, StringComparison.Ordinal);

        Ui.Invoke(() =>
        {
            var link = new Button { Content = "Další tip", Style = S("Button.Quiet"), FontSize = 24 };
            var bitmap = Draw(link, (Brush)Application.Current.FindResource("Brush.Canvas"), "quiet-link", out var b);
            var best = int.MaxValue;
            for (var x = (int)b.Left; x < b.Right; x++)
                for (var y = (int)b.Top; y < b.Bottom; y++)
                {
                    var c = Px(bitmap, x, y);
                    best = Math.Min(best, Math.Abs(c.R - Res("Brush.TintText").R) + Math.Abs(c.G - Res("Brush.TintText").G) + Math.Abs(c.B - Res("Brush.TintText").B));
                }
            Assert.True(best < 30, $"no pixel of the link text is the tint text colour (closest delta {best})");
        });
    }
}
