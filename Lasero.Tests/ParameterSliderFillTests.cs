using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lasero.App.Components;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The filled part of a <see cref="ParameterSlider"/> has to end at the thumb centre and sit on the
/// same horizontal line as the groove and the thumb. It used to be laid out inside a 32px page
/// button (theme-wide RepeatButton MinHeight) in an 18px slider, so it hung 7px below the track and
/// stopped at the thumb's left edge, which at low values on a wide range (speed 350 of 10..12000)
/// left an unattached stub.
/// </summary>
public class ParameterSliderFillTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var enabled in new[] { true, false })
        {
            foreach (var v in new[] { 0d, 1d, 50d, 95d, 100d }) yield return new object[] { 0d, 100d, 1d, v, enabled };
            foreach (var v in new[] { 10d, 350d, 6000d, 11990d, 12000d }) yield return new object[] { 10d, 12000d, 10d, v, enabled };
        }
    }


    [Fact]
    public void FillIsCentredOnTheTrackAndEndsUnderTheThumbCentre()
    {
        Sta(() =>
        {
            foreach (var c in Cases())
            {
            var (min, max, tick, value, enabled) = ((double)c[0], (double)c[1], (double)c[2], (double)c[3], (bool)c[4]);
            var host = new Border { Width = 396, Child = new ParameterSlider { Minimum = min, Maximum = max, TickFrequency = tick, Value = value, FieldWidth = 112, IsEnabled = enabled } };
            host.Measure(new Size(396, 400));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();

            var slider = Find<Slider>(host);
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var decrease = track.DecreaseRepeatButton;
            var fill = Find<Border>(decrease, b => b.Height == 4);

            // The page button is the track's height, not the theme's control height.
            Assert.Equal(slider.ActualHeight, decrease.ActualHeight, 0.01);

            var bounds = fill.TransformToAncestor(slider).TransformBounds(new Rect(0, 0, fill.ActualWidth, fill.ActualHeight));
            var thumb = track.Thumb.TransformToAncestor(slider).TransformBounds(new Rect(0, 0, track.Thumb.ActualWidth, track.Thumb.ActualHeight));
            var thumbCentre = thumb.Left + thumb.Width / 2;

            Assert.Equal(slider.ActualHeight / 2, bounds.Top + bounds.Height / 2, 0.51);
            Assert.Equal(thumb.Top + thumb.Height / 2, bounds.Top + bounds.Height / 2, 0.51);
            Assert.Equal(0, bounds.Left, 0.01);
            Assert.True(bounds.Right >= thumbCentre - 0.01, $"fill ends at {bounds.Right}, thumb centre {thumbCentre}");
            Assert.True(bounds.Right <= thumb.Right + 0.01, "fill must stay under the thumb, not poke out past it");
            }
        });
    }

    [Fact]
    public void TrackButtonsOverrideTheThemeWideRepeatButtonStyleExplicitly()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Components", "ParameterSlider.xaml"));
        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));

        // Precondition that makes the explicit style necessary: the implicit style forces a MinHeight.
        Assert.Contains("<Style TargetType=\"RepeatButton\">", theme, StringComparison.Ordinal);

        Assert.Contains("<Style x:Key=\"Slider.Parameter.Segment\" TargetType=\"RepeatButton\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"0\" />", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Style=\"{StaticResource Slider.Parameter.Segment}\"").Length - 1);
        Assert.Contains("Margin=\"0,0,-9,0\"", xaml, StringComparison.Ordinal);
    }

    private static T Find<T>(DependencyObject root, Func<T, bool>? where = null) where T : DependencyObject
    {
        var found = FindAll(root).OfType<T>().FirstOrDefault(x => where?.Invoke(x) ?? true);
        return found ?? throw new InvalidOperationException($"{typeof(T).Name} not found");
    }

    private static IEnumerable<DependencyObject> FindAll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in FindAll(child)) yield return d;
        }
    }

    // Reuse the process-wide themed WPF test dispatcher. WPF permits only one Application per
    // AppDomain, and separate STA helpers race when xUnit runs UI test classes in parallel.
    private static void Sta(Action a) => InlineTextEditorRenderTests.Ui.Invoke(a);
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}
