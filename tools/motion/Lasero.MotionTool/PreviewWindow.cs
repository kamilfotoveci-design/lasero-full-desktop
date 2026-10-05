using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lasero.App.Controls.Motion;

namespace Lasero.MotionTool;

/// <summary>
/// Preview harness for the brand motion controls: the vector intro, the compact pulse (mark only and with
/// wordmark) and the video control with its fallback, on the warm welcome background. Not part of the app.
/// Command line: preview [--tab intro|pulse|video] [--reduced] [--video path]
/// </summary>
public sealed class PreviewWindow : Window
{
    private readonly LaseroIntroAnimation _intro = new() { AutoPlay = false };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5A, 0x54)), Margin = new Thickness(16, 8, 16, 8) };

    public PreviewWindow()
    {
        Title = "LASERO motion preview";
        Width = 1200; Height = 760;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF6, 0xF3));
        var args = Environment.GetCommandLineArgs().Skip(2).ToArray();
        var reduced = args.Contains("--reduced");
        LaseroMotion.ForceReducedMotion = reduced;
        LaseroMotion.ForceAnimations = args.Contains("--force"); // this machine may have Windows animations off
        var tab = args.SkipWhile(a => a != "--tab").Skip(1).FirstOrDefault() ?? "intro";
        var videoPath = args.SkipWhile(a => a != "--video").Skip(1).FirstOrDefault();

        var root = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_status);

        var host = new Grid();
        root.Children.Add(host);
        var tabs = new Dictionary<string, FrameworkElement>
        {
            ["intro"] = BuildIntro(),
            ["pulse"] = BuildPulse(),
            ["video"] = BuildVideo(videoPath),
            ["pulse1"] = new LaseroLogoPulse { Width = 96, Height = 120, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        foreach (var (name, content) in tabs)
        {
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            b.Click += (_, _) => Show(host, tabs, name);
            bar.Children.Add(b);
        }
        var replay = new Button { Content = "replay intro", Padding = new Thickness(12, 4, 12, 4) };
        replay.Click += (_, _) => { Show(host, tabs, "intro"); _intro.Play(); };
        bar.Children.Add(replay);
        Content = root;
        Show(host, tabs, tab);
        Loaded += (_, _) => { if (tab == "intro") _intro.Play(); };
        var info = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        info.Tick += (_, _) => _status.Text = $"animations enabled={LaseroMotion.AnimationsEnabled}  intro playing={_intro.IsPlaying} time={_intro.Time:F2}s  render tier={RenderCapability.Tier >> 16}";
        info.Start();
    }

    private static void Show(Grid host, Dictionary<string, FrameworkElement> tabs, string name)
    {
        host.Children.Clear();
        if (tabs.TryGetValue(name, out var c)) host.Children.Add(c);
    }

    private FrameworkElement BuildIntro()
    {
        _intro.Margin = new Thickness(0);
        return _intro;
    }

    private static FrameworkElement BuildPulse()
    {
        var g = new UniformGrid { Columns = 3, Margin = new Thickness(24) };
        g.Children.Add(new LaseroLogoPulse { Width = 140, Height = 190, HorizontalAlignment = HorizontalAlignment.Center });
        g.Children.Add(new LaseroLogoPulse { Width = 64, Height = 90, HorizontalAlignment = HorizontalAlignment.Center });
        g.Children.Add(new LaseroLogoPulse { ShowWordmark = true, Width = 360, Height = 240 });
        return g;
    }

    private static FrameworkElement BuildVideo(string? path)
    {
        var video = new LaseroIntroVideo { Source = path, Margin = new Thickness(24) };
        return video;
    }
}
