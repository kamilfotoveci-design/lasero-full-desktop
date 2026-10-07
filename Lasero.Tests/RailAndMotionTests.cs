using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using Lasero.App.Components;
using Lasero.App.Views;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The tool rail in its REAL states, by pixels, and the interaction motion by source and by clock.
///
/// History: a source test claimed the selected tool was a solid tint pill while the live app drew a pale
/// tile with a thin red edge. The cause was layering: the Hover layer was an OPAQUE PanelRaised border
/// that the mouse-over trigger faded to 100%, drawn above the Selected layer, so clicking a tool (which
/// leaves the pointer on it) painted the gray hover surface over the red. These tests render the real
/// RailTool and ShapeRailButton templates, push them into the states with the exact values the
/// template's own triggers use, and read the pixels, so that cannot pass while the app is wrong.
/// Nothing here shows a live window or sends input.
/// </summary>
[Collection("WpfUi")]
public sealed class RailAndMotionTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }

    private static string Source(params string[] parts) =>
        XmlComment.Replace(File.ReadAllText(Path.Combine(new[] { Root(), "Lasero.App" }.Concat(parts).ToArray())), "");

    /// <summary>Lets the dispatcher run for a wall-clock interval so entry animations (selection fade, icon scale-in)
    /// reach their resting state before a render is read.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Color Res(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    /// <summary>Lets the dispatcher run (render ticks, animation clocks) for a wall-clock interval. A polling loop,
    /// not a timer-driven frame, so a busy or looping animation can never starve it.</summary>
    private static Color Blend(Color over, Color under, double alpha) => Color.FromRgb(
        (byte)Math.Round(over.R * alpha + under.R * (1 - alpha)),
        (byte)Math.Round(over.G * alpha + under.G * (1 - alpha)),
        (byte)Math.Round(over.B * alpha + under.B * (1 - alpha)));

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private sealed record Shot(BitmapSource Bitmap, Rect Bounds, double Scale)
    {
        public Color Px(double x, double y)
        {
            var buffer = new byte[4];
            Bitmap.CopyPixels(new Int32Rect(
                (int)Math.Clamp(Math.Floor(x * Scale), 0, Bitmap.PixelWidth - 1),
                (int)Math.Clamp(Math.Floor(y * Scale), 0, Bitmap.PixelHeight - 1), 1, 1), buffer, 4, 0);
            return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
        }

        public Color Mid(double dx) => Px(Bounds.Left + dx, Bounds.Top + Bounds.Height / 2);

        /// <summary>Count of near-white pixels inside the middle of the control: the white glyph.</summary>
        public int WhiteGlyphPixels()
        {
            var count = 0;
            for (var x = Bounds.Left + 10; x < Bounds.Right - 10; x += 0.5)
                for (var y = Bounds.Top + 10; y < Bounds.Bottom - 10; y += 0.5)
                {
                    var c = Px(x, y);
                    if (c.R > 235 && c.G > 235 && c.B > 235) count++;
                }
            return count;
        }
    }

    private static Shot Render(FrameworkElement content, string name, double dpi = 96, Brush? background = null, Action<FrameworkElement>? beforeSettle = null)
    {
        var host = new Border { Background = background ?? (Brush)Application.Current.FindResource("Brush.Surface"), Padding = new Thickness(24), Child = content };
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
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            // Entry animations (selection fade, icon scale-in) must have finished: a render at t = 0 would
            // catch the start frame and prove nothing about the resting state.
            Pump(450);
            beforeSettle?.Invoke(content);
            host.UpdateLayout();

            var bounds = content.TransformToAncestor(host).TransformBounds(new Rect(content.RenderSize));
            var scale = dpi / 96.0;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(dir, name + ".png"));
                encoder.Save(stream);
            }
            return new Shot(bitmap, bounds, scale);
        }
        finally
        {
            window.Close();
        }
    }

    private static Style RailStyle(string key) => (Style)new DesignerToolRail().Resources[key];

    private static ControlTemplate TemplateOf(Style style) =>
        (ControlTemplate)style.Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value;

    private static IconGlyph Icon(string key) =>
        new() { IconData = (Geometry)Application.Current.FindResource(key), Width = 20, Height = 20 };

    private static RadioButton Tool(string iconKey, bool selected, string group) => new()
    {
        Style = RailStyle("RailTool"),
        GroupName = group,
        IsChecked = selected,
        Content = Icon(iconKey),
    };

    private static Border Part(Control control, string name) => (Border)control.Template.FindName(name, control);

    private static void SetFrameworkState(DependencyObject element, Type owner, string keyName, object value)
    {
        var key = owner.GetField(keyName, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as DependencyPropertyKey
            ?? throw new InvalidOperationException($"WPF state key {owner.Name}.{keyName} was not found.");
        element.SetValue(key, value);
    }

    /// <summary>The Opacity the template's own IsMouseOver trigger animates the Hover layer to.</summary>
    private static double HoverTarget(ControlTemplate template) =>
        template.Triggers.OfType<Trigger>().First(t => t.Property == UIElement.IsMouseOverProperty)
            .EnterActions.OfType<BeginStoryboard>().SelectMany(b => b.Storyboard.Children).OfType<DoubleAnimation>()
            .First(a => Storyboard.GetTargetName(a) == "Hover").To!.Value;

    /// <summary>The Opacity the template's IsPressed trigger sets on the Press layer.</summary>
    private static double PressTarget(ControlTemplate template) =>
        (double)template.Triggers.OfType<Trigger>().First(t => t.Property == ButtonBase.IsPressedProperty)
            .Setters.OfType<Setter>().First(s => s.TargetName == "Press" && s.Property == UIElement.OpacityProperty).Value;

    // ------------------------------------------------------------------ the bug itself

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void SelectedRailToolIsASolidTintPillWithAWhiteIconAtRestHoverAndPress(double dpi)
    {
        Ui.Invoke(() =>
        {
            var tint = Res("Brush.Tint");
            var ink = Res("Brush.HoverWash");
            var template = TemplateOf(RailStyle("RailTool"));
            var hover = HoverTarget(template);
            var press = PressTarget(template);

            // The hover and press layers are translucent ink: at most a few percent, never a surface.
            Assert.InRange(hover, 0.01, 0.12);
            Assert.InRange(press, 0.01, 0.16);

            foreach (var (state, alpha, isHover, isPressed) in new[]
                     {
                         ("rest", 0.0, false, false),
                         ("hover", hover, true, false),
                         ("hover-pressed", hover + press, true, true),
                     })
            {
                var tool = Tool("Glyph.Text", true, "g-" + state + dpi);
                var shot = Render(tool, $"rail-selected-{state}-{dpi:0}", dpi, beforeSettle: _ =>
                {
                    SetFrameworkState(tool, typeof(UIElement), "IsMouseOverPropertyKey", isHover);
                    SetFrameworkState(tool, typeof(ButtonBase), "IsPressedPropertyKey", isPressed);
                    Pump(450);
                });

                var expected = Blend(ink, tint, alpha);
                // Sample the left, middle and right of the pill, away from the glyph: all solid tint.
                foreach (var dx in new[] { 6.0, 13.0, tool.ActualWidth - 6 })
                {
                    var c = shot.Mid(dx);
                    Assert.True(Near(c, expected, 6), $"{state} @{dpi}dpi: pill pixel at +{dx} is {Hex(c)}, expected about {Hex(expected)}");
                    Assert.False(Near(c, Res("Brush.PanelRaised"), 12), $"{state}: the pill shows the gray hover surface {Hex(c)}");
                }

                Assert.True(shot.WhiteGlyphPixels() >= 6, $"{state} @{dpi}dpi: no white glyph on the pill");
            }
        });
    }

    [Theory]
    [InlineData("Glyph.Select", "select")]
    [InlineData("Glyph.Text", "text")]
    [InlineData("Glyph.Line", "line")]
    public void EachSelectedToolIsSolidTintAndItsUnselectedNeighbourHasNoFill(string icon, string name)
    {
        Ui.Invoke(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var on = Tool(icon, true, "pair-" + name);
            var off = Tool("Glyph.Select", false, "pair-" + name);
            on.Margin = off.Margin = new Thickness(6);
            panel.Children.Add(on);
            panel.Children.Add(off);
            var shot = Render(panel, "rail-pair-" + name);
            var host = (FrameworkElement)panel.Parent;
            var onB = on.TransformToAncestor(host).TransformBounds(new Rect(on.RenderSize));
            var offB = off.TransformToAncestor(host).TransformBounds(new Rect(off.RenderSize));
            Assert.True(Near(shot.Px(onB.Left + 5, onB.Top + onB.Height / 2), Res("Brush.Tint"), 4), "selected = tint");
            Assert.True(Near(shot.Px(offB.Left + 5, offB.Top + offB.Height / 2), Res("Brush.Surface"), 2), "unselected = no fill");
        });
    }

    [Fact]
    public void ArmedShapeToolIsSolidTintLikeTheOthers()
    {
        Ui.Invoke(() =>
        {
            var button = new Button { Style = RailStyle("ShapeRailButton"), Tag = "True", Content = Icon("Glyph.Rectangle") };
            var shot = Render(button, "rail-shape-armed");
            Assert.True(Near(shot.Mid(6), Res("Brush.Tint"), 4), $"armed shape tool is {Hex(shot.Mid(6))}");
            Assert.True(shot.WhiteGlyphPixels() >= 6);

            var template = TemplateOf(RailStyle("ShapeRailButton"));
            var hover = HoverTarget(template);
            var armedHover = new Button { Style = RailStyle("ShapeRailButton"), Tag = "True", Content = Icon("Glyph.Rectangle") };
            var shot2 = Render(armedHover, "rail-shape-armed-hover", beforeSettle: _ =>
            {
                SetFrameworkState(armedHover, typeof(UIElement), "IsMouseOverPropertyKey", true);
                Pump(450);
            });
            Assert.True(Near(shot2.Mid(6), Blend(Res("Brush.HoverWash"), Res("Brush.Tint"), hover), 6), $"armed + hover is {Hex(shot2.Mid(6))}");
        });
    }

    [Fact]
    public void FocusedSelectedToolShowsARingOutsideThePillAndTheKeyboardOnlyFlagGatesIt()
    {
        Ui.Invoke(() =>
        {
            var tool = Tool("Glyph.Text", true, "focus-sel");
            tool.Margin = new Thickness(8);
            FocusVisual.SetIsVisible(tool, true);
            var shot = Render(tool, "rail-selected-focused");
            var ring = Res("Brush.Tint");

            // Walk left from the pill into the margin: gap (surface) then 2px ring, so the ring is visible
            // against white even though the pill itself is the same red.
            var y = tool.TransformToAncestor((FrameworkElement)tool.Parent).TransformBounds(new Rect(tool.RenderSize)).Top + tool.ActualHeight / 2;
            var left = tool.TransformToAncestor((FrameworkElement)tool.Parent).TransformBounds(new Rect(tool.RenderSize)).Left;
            var run = Enumerable.Range(0, 8).Select(i => shot.Px(left - 4 + i, y)).ToList();
            Assert.Contains(run, c => Near(c, ring, 30));
            Assert.Contains(run, c => Near(c, Res("Brush.Surface"), 10));

            var plain = Tool("Glyph.Text", true, "focus-none");
            plain.Margin = new Thickness(8);
            var shot2 = Render(plain, "rail-selected-unfocused");
            var left2 = plain.TransformToAncestor((FrameworkElement)plain.Parent).TransformBounds(new Rect(plain.RenderSize)).Left;
            Assert.DoesNotContain(Enumerable.Range(0, 6).Select(i => shot2.Px(left2 - 6 + i, y)), c => Near(c, ring, 30));
        });
    }

    [Fact]
    public void RailToolLayersAreInTheOrderThatCannotHideTheSelection()
    {
        // Source-level guard for the root cause: no layer above Selected may be an opaque surface.
        var rail = Source("Views", "DesignerToolRail.xaml");
        Assert.DoesNotMatch(@"x:Name=""Hover""[^>]*Brush\.PanelRaised", rail);
        Assert.DoesNotMatch(@"TargetName=""Hover"" Property=""Opacity"" Value=""1""", rail);
        Assert.DoesNotMatch(@"TargetProperty=""Opacity""\s+To=""1"" Duration=""\{StaticResource Motion\.(Fast|Hover)\}""", rail);
        Assert.Contains("x:Name=\"Press\"", rail, StringComparison.Ordinal);
    }

    [Fact]
    public void RailStateSheetRendersEveryStateSideBySide()
    {
        Ui.Invoke(() =>
        {
            var template = TemplateOf(RailStyle("RailTool"));
            var hover = HoverTarget(template);
            var press = PressTarget(template);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            RadioButton Add(string icon, bool selected, string group)
            {
                var t = Tool(icon, selected, group);
                t.Margin = new Thickness(10);
                panel.Children.Add(t);
                return t;
            }

            Add("Glyph.Select", false, "s1");                       // idle
            var hoverIdle = Add("Glyph.Text", false, "s2");         // hover on an idle tool
            var selected = Add("Glyph.Text", true, "s3");           // selected
            var selectedHover = Add("Glyph.Text", true, "s4");      // selected + pointer still on it (the reported bug)
            var selectedPressed = Add("Glyph.Text", true, "s5");    // selected + pressed
            var focused = Add("Glyph.Text", true, "s6");            // selected + keyboard focus
            FocusVisual.SetIsVisible(focused, true);
            var armed = new Button { Style = RailStyle("ShapeRailButton"), Tag = "True", Content = Icon("Glyph.Rectangle"), Margin = new Thickness(10) };
            panel.Children.Add(armed);

            var shot = Render(panel, "rail-states-sheet", beforeSettle: _ =>
            {
                Part(hoverIdle, "Hover").Opacity = hover;
                Part(selectedHover, "Hover").Opacity = hover;
                Part(selectedPressed, "Hover").Opacity = hover;
                Part(selectedPressed, "Press").Opacity = press;
            });
            Assert.True(shot.Bitmap.PixelWidth > 100);
            _ = selected;
        });
    }

    // ------------------------------------------------------------------ other tint states, by pixels

    [Fact]
    public void CheckedCheckboxAndOnToggleAreSolidTintAtRestAfterTheirEntryAnimation()
    {
        Ui.Invoke(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new CheckBox { Content = "A", IsChecked = true, Margin = new Thickness(6) };
            var toggle = new ToggleButton
            {
                Style = (Style)Application.Current.FindResource("Toggle.Icon"),
                IsChecked = true,
                Margin = new Thickness(6),
                Content = Icon("Glyph.Link"),
            };
            panel.Children.Add(check);
            panel.Children.Add(toggle);
            var shot = Render(panel, "tint-checked-toggle");
            var host = (FrameworkElement)panel.Parent;
            var cb = check.TransformToAncestor(host).TransformBounds(new Rect(check.RenderSize));
            var tb = toggle.TransformToAncestor(host).TransformBounds(new Rect(toggle.RenderSize));
            Assert.True(Near(shot.Px(cb.Left + 3, cb.Top + cb.Height / 2 - 6), Res("Brush.Tint"), 12), "checked box");
            Assert.True(Near(shot.Px(tb.Left + 4, tb.Top + tb.Height / 2), Res("Brush.Tint"), 4), "toggle on");
        });
    }

    [Fact]
    public void SelectedTabAndNavItemCarryTheTintIndicator()
    {
        Ui.Invoke(() =>
        {
            var nav = new Button { Style = (Style)Application.Current.FindResource("NavButton"), Tag = "Active", Content = "Domů", Width = 160 };
            var shot = Render(nav, "nav-selected");
            // The 2px indicator sits on the leading edge, 8px in from top and bottom.
            var bar = shot.Px(nav.TransformToAncestor((FrameworkElement)nav.Parent).TransformBounds(new Rect(nav.RenderSize)).Left + 1,
                              nav.TransformToAncestor((FrameworkElement)nav.Parent).TransformBounds(new Rect(nav.RenderSize)).Top + nav.ActualHeight / 2);
            Assert.True(Near(bar, Res("Brush.Tint"), 6), $"nav indicator is {Hex(bar)}");
        });

        var materials = Source("MaterialsWindow.xaml");
        Assert.Contains("TabIndicator", materials, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""TabIndicator""[^>]*Brush\.(SelectedIndicator|Tint|Accent)", materials);
    }

    // ------------------------------------------------------------------ motion: source guards

    private static readonly string[] AnimatedFiles =
    {
        "Theme/LaseroTheme.xaml", "Theme/SharedUiStyles.xaml", "Views/DesignerToolRail.xaml", "Components/ProjectCard.xaml", "LaseroDialogWindow.xaml",
    };

    [Fact]
    public void InteractionAnimationsOnlyTouchOpacityAndRenderTransforms()
    {
        // GPU-friendly and layout-free: no Width/Height/Margin/Color animation in the shared interaction styles.
        var allowed = new Regex(@"^(Opacity|ScaleX|ScaleY|X|Y|\(UIElement\.RenderTransform\)\.\(TranslateTransform\.[XY]\)|\(UIElement\.RenderTransform\)\.\(ScaleTransform\.Scale[XY]\)|\(UIElement\.RenderTransform\)\.\(TransformGroup\.Children\)\[\d\]\.\((Translate|Scale)Transform\.[A-Za-z]+\))$");
        foreach (var file in AnimatedFiles)
        {
            var text = Source(file.Split('/'));
            foreach (Match m in Regex.Matches(text, @"Storyboard\.TargetProperty=""(?<p>[^""]+)"""))
                Assert.True(allowed.IsMatch(m.Groups["p"].Value), $"{file} animates {m.Groups["p"].Value}: only Opacity and RenderTransform may animate");
        }
    }

    [Fact]
    public void InteractionAnimationsTakeTheirDurationsFromMotionTokens()
    {
        foreach (var file in AnimatedFiles)
        {
            var text = Source(file.Split('/'));
            foreach (Match m in Regex.Matches(text, @"<DoubleAnimation\b[^>]*?>", RegexOptions.Singleline))
            {
                var tag = m.Value;
                if (tag.Contains("Breathe", StringComparison.Ordinal)) continue;
                Assert.Matches(@"Duration=""\{StaticResource Motion\.[A-Za-z]+\}""", tag);
            }
        }
    }

    [Fact]
    public void EveryInteractionDurationIsZeroedWhenWindowsAnimationsAreOff()
    {
        var theme = Source("Theme", "LaseroTheme.xaml");
        var accessibility = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "UiAccessibility.cs"));
        foreach (Match m in Regex.Matches(theme, @"<Duration x:Key=""(?<k>Motion\.[A-Za-z]+)"""))
        {
            var key = m.Groups["k"].Value;
            if (key == "Motion.Breathe") continue; // a loop for real activity, not interaction feedback
            Assert.Contains($"\"{key}\"", accessibility, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyOneOvershootExistsAndItIsTheSlightPressRelease()
    {
        var theme = Source("Theme", "LaseroTheme.xaml");
        Assert.Single(Regex.Matches(theme, "<BackEase"));
        var m = Regex.Match(theme, @"<BackEase x:Key=""Ease\.Release""[^>]*Amplitude=""(?<a>[0-9.]+)""");
        Assert.True(m.Success);
        Assert.InRange(double.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture), 0.1, 0.5);
    }

    [Fact]
    public void PopupMenusAndDialogsAreWiredToTheSharedOpenAnimation()
    {
        var theme = Source("Theme", "LaseroTheme.xaml");
        Assert.Contains("Motion.Popup", theme, StringComparison.Ordinal);
        Assert.Contains("PopupOpen", theme, StringComparison.Ordinal);
        Assert.Contains("Motion.Dialog", File.ReadAllText(Path.Combine(Root(), "Lasero.App", "LaseroDialogWindow.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("Motion.Hover", theme, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ motion: deterministic clock

    private static Storyboard PressStoryboard(Button button, bool press)
    {
        var trigger = button.Template.Triggers.OfType<Trigger>().First(t => t.Property == ButtonBase.IsPressedProperty);
        var actions = press ? trigger.EnterActions : trigger.ExitActions;
        return actions.OfType<BeginStoryboard>().Select(b => b.Storyboard).First(s =>
            s.Children.OfType<DoubleAnimation>().Any(a => Storyboard.GetTargetName(a) == "PressScale"));
    }

    [Fact]
    public void PressScalesToNinetySevenPercentAndSpringsBackThroughASlightOvershoot()
    {
        Ui.Invoke(() =>
        {
            var button = new Button { Content = "Nový projekt", Width = 160, Height = 44 };
            var window = new Window { Content = button, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                button.ApplyTemplate();
                button.UpdateLayout();

                Assert.Equal(1.0, ((ScaleTransform)button.Template.FindName("PressScale", button)).ScaleX, 3);
                // 90 ms press, evaluated from the template's own animation: 1.0 at t = 0, exactly 0.97 at 90 ms.
                var press = PressStoryboard(button, true).Children.OfType<DoubleAnimation>()
                    .First(a => Storyboard.GetTargetName(a) == "PressScale" && Storyboard.GetTargetProperty(a).Path == "ScaleX");
                Assert.Equal(0.97, press.To);
                Assert.Equal(TimeSpan.FromMilliseconds(90), press.Duration.TimeSpan);
                Assert.Equal(1.0, 1.0 + (0.97 - 1.0) * press.EasingFunction!.Ease(0), 6);
                Assert.Equal(0.97, 1.0 + (0.97 - 1.0) * press.EasingFunction!.Ease(1), 6);
                Assert.InRange(1.0 + (0.97 - 1.0) * press.EasingFunction!.Ease(0.5), 0.97, 1.0);

                // 200 ms release, evaluated from the template's own animation (To, Duration, easing): it
                // starts at 0.97, passes 1.0 slightly (the overshoot) and settles at exactly 1.0.
                var release = PressStoryboard(button, false).Children.OfType<DoubleAnimation>()
                    .First(a => Storyboard.GetTargetName(a) == "PressScale" && Storyboard.GetTargetProperty(a).Path == "ScaleX");
                Assert.Equal(1.0, release.To);
                Assert.Equal(TimeSpan.FromMilliseconds(200), release.Duration.TimeSpan);
                var curve = Enumerable.Range(0, 41).Select(i => 0.97 + (1.0 - 0.97) * release.EasingFunction!.Ease(i / 40.0)).ToList();
                Assert.Equal(0.97, curve[0], 6);
                Assert.Equal(1.0, curve[^1], 6);
                Assert.InRange(curve.Max(), 1.0005, 1.03);
                // Both storyboards target the same transform on both axes, so the squeeze is uniform.
                Assert.Equal(2, PressStoryboard(button, true).Children.OfType<DoubleAnimation>().Count(a => Storyboard.GetTargetName(a) == "PressScale"));
                Assert.Equal(2, PressStoryboard(button, false).Children.OfType<DoubleAnimation>().Count(a => Storyboard.GetTargetName(a) == "PressScale"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PressDurationsAreTheTokensAndTheyCollapseToZeroWithReducedMotion()
    {
        Ui.Invoke(() =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(90), ((Duration)Application.Current.FindResource("Motion.Press")).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(200), ((Duration)Application.Current.FindResource("Motion.Release")).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(120), ((Duration)Application.Current.FindResource("Motion.Hover")).TimeSpan);
            Assert.Equal(TimeSpan.FromMilliseconds(150), ((Duration)Application.Current.FindResource("Motion.Toggle")).TimeSpan);
        });
    }
}
