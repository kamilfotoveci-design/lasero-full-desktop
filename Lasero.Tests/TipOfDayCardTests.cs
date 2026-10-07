using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lasero.App.Components;
using Lasero.App.Controls.Motion;
using Lasero.App.Tour;

namespace Lasero.Tests;

/// <summary>The Home "Tip dne" card: source rules (one surface, tokens only, transform-only motion) and
/// off-screen layout checks. Renders of the whole Home live in ScreenSpecimenTests.</summary>
[Collection("WpfUi")]
public sealed class TipOfDayCardTests
{
    public sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly string Root = FindRepositoryRoot();
    private static string App(params string[] parts) => File.ReadAllText(Path.Combine([Root, "Lasero.App", .. parts]));

    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>No clipping, the action is visible and a real 40+ px target, the sentence is 15+ px and at most two lines.</summary>
    public static void AssertCardLaidOutCleanly(TipOfDayCard card, FrameworkElement view, double viewWidth)
    {
        card.UpdateLayout();
        var bounds = card.TransformToAncestor(view).TransformBounds(new Rect(card.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Right <= viewWidth + 0.5, $"card {bounds} leaves the column");
        Assert.True(bounds.Height > 0 && bounds.Top >= 0);

        var button = (Button)card.FindName("NextButton");
        var text = (TextBlock)card.FindName("TipText");
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.True(button.ActualHeight >= 40, $"action is {button.ActualHeight}px tall");
        Assert.True(button.ActualWidth >= 40);
        var inCard = button.TransformToAncestor(card).TransformBounds(new Rect(button.RenderSize));
        Assert.True(inCard.Right <= card.ActualWidth + 0.5 && inCard.Left >= 0, "action is clipped");
        Assert.True(inCard.Top >= 0 && inCard.Bottom <= card.ActualHeight + 0.5, "action is clipped vertically");

        Assert.True(text.FontSize >= 15, $"sentence is {text.FontSize}px");
        Assert.True(text.ActualHeight <= text.LineHeight * 2 + 1, $"sentence is {text.ActualHeight}px tall, more than two lines");
        var textBox = text.TransformToAncestor(card).TransformBounds(new Rect(text.RenderSize));
        Assert.True(textBox.Right <= inCard.Left + 0.5, "sentence runs under the action");
        Assert.True(textBox.Bottom <= card.ActualHeight, "sentence is clipped");
    }

    private static TipOfDayCard Host(string tip, double width, out Window window)
    {
        var card = new TipOfDayCard { Tip = tip, Position = 3, Count = 12, NextCommand = new NoopCommand() };
        window = new Window
        {
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            SizeToContent = SizeToContent.Height, Width = width,
            Content = new Border { Padding = new Thickness(24), Child = card },
        };
        window.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return card;
    }

    [Theory]
    [InlineData(896)]
    [InlineData(1182)]
    public void EveryTipOfTheDayFitsInTwoLines(double width) => Ui.Invoke(() =>
    {
        foreach (var tip in TipOfDay.All)
        {
            var card = Host(tip, width, out var window);
            try
            {
                var text = (TextBlock)card.FindName("TipText");
                Assert.True(text.ActualHeight <= text.LineHeight * 2 + 1, $"'{tip}' needs more than two lines at {width}");
                var progress = (TextBlock)card.FindName("Progress");
                Assert.Equal("3 z 12", progress.Text);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void AVeryLongTipIsTrimmedToTwoLinesInsteadOfGrowingTheCard() => Ui.Invoke(() =>
    {
        var card = Host(string.Join(" ", Enumerable.Repeat("Velmi dlouhý tip, který se nesmí rozlézt přes celou kartu.", 6)), 700, out var window);
        try
        {
            var text = (TextBlock)card.FindName("TipText");
            Assert.True(text.ActualHeight <= text.LineHeight * 2 + 1, $"{text.ActualHeight}");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ChangingTheTipCrossFadesOnlyWhenAnimationsAreOn() => Ui.Invoke(() =>
    {
        var old = (LaseroMotion.ForceAnimations, LaseroMotion.ForceReducedMotion);
        try
        {
            LaseroMotion.ForceReducedMotion = false;
            LaseroMotion.ForceAnimations = true;
            var card = Host("První tip.", 900, out var window);
            try
            {
                var text = (TextBlock)card.FindName("TipText");
                var outgoing = (TextBlock)card.FindName("TipOut");
                card.Tip = "Druhý tip.";
                Assert.Equal("Druhý tip.", text.Text);
                Assert.Equal("První tip.", outgoing.Text);
                Assert.Equal(Visibility.Visible, outgoing.Visibility);
                Assert.True(text.HasAnimatedProperties);

                LaseroMotion.ForceAnimations = false;
                LaseroMotion.ForceReducedMotion = true;
                card.Tip = "Třetí tip.";
                Assert.Equal("Třetí tip.", text.Text);
                Assert.Equal(Visibility.Collapsed, outgoing.Visibility);
                Assert.False(text.HasAnimatedProperties);
            }
            finally { window.Close(); }
        }
        finally { (LaseroMotion.ForceAnimations, LaseroMotion.ForceReducedMotion) = old; }
    });

    [Fact]
    public void CardIsOneFlatSurfaceOnTokensOnly()
    {
        var xaml = App("Components", "TipOfDayCard.xaml");
        var code = App("Components", "TipOfDayCard.xaml.cs");
        Assert.Single(Regex.Matches(xaml, "<Border[ >]"));
        Assert.Contains("Background=\"{DynamicResource Brush.Canvas}\"", xaml);
        Assert.Contains("Radius.Xl", xaml);
        Assert.DoesNotContain("BorderBrush", xaml);
        Assert.DoesNotContain("Effect=", xaml);
        Assert.DoesNotContain("Radius.Pill", xaml);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}\\b", xaml);
        Assert.DoesNotContain("FocusVisualStyle", xaml);
        Assert.Contains("Style=\"{StaticResource Button.Quiet}\"", xaml);
        Assert.Contains("Brush.TextPrimary", xaml);
        Assert.Contains("Size.Text.Assistant.Body", xaml);
        Assert.Contains("Tone=\"Amber\"", xaml);
        Assert.Contains("Glyph.Lightbulb", xaml);
        Assert.Contains("Glyph.ChevronRight", xaml);
        Assert.Contains("ToolTip=\"Ukázat další tip\"", xaml);

        Assert.Contains("LaseroMotion.AnimationsEnabled", code);
        Assert.Contains("\"Motion.Base\"", code);
        Assert.Contains("TranslateTransform.YProperty", code);
        Assert.DoesNotMatch("(Width|Height|Margin|Padding)Property", code);
        Assert.DoesNotContain("TimeSpan", code);
    }

    [Fact]
    public void HomePlacesTheCardAfterTheActionsAndBeforeContinueWorking()
    {
        var home = App("Views", "HomeView.xaml");
        var actions = home.IndexOf("HomeActions", StringComparison.Ordinal);
        var tip = home.IndexOf("HomeTipOfDay", StringComparison.Ordinal);
        var next = home.IndexOf("Title=\"Pokračovat v práci\"", StringComparison.Ordinal);
        Assert.True(actions > 0 && actions < tip && tip < next);
        Assert.Contains("Tip=\"{Binding Home.TipOfDay}\"", home);
        Assert.Contains("NextCommand=\"{Binding Home.NextTipCommand}\"", home);
        Assert.Contains("AutomationProperties.Name=\"Tip dne\"", home);
    }

    [Fact]
    public void CardCopyFollowsTheBrandRules()
    {
        var xaml = Regex.Replace(App("Components", "TipOfDayCard.xaml"), "<!--.*?-->", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(xaml, "(?:Text|ToolTip|Name)=\"([^\"{]+)\""))
        {
            Assert.DoesNotContain('?', m.Groups[1].Value);
            Assert.DoesNotContain('!', m.Groups[1].Value);
        }
        Assert.Equal(1, TipOfDay.PositionOf(TipOfDay.All[0]));
        Assert.Equal(TipOfDay.All.Count, TipOfDay.PositionOf(TipOfDay.All[^1]));
    }

    [Fact]
    public void TipsAreInPlainLanguage()
    {
        var jargon = new Regex(@"preflight|kerf|bezier|béz|vektor|bitmap|rastr|offset|hatch|G-code", RegexOptions.IgnoreCase);
        foreach (var tip in TipOfDay.All)
            Assert.False(jargon.IsMatch(tip), $"'{tip}' uses expert wording");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LaseroDesktop.sln")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
