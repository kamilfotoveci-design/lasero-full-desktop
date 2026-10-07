using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

public enum SectionLevel
{
    /// <summary>A section of a screen: 21 SemiBold, 16 below.</summary>
    Page,
    /// <summary>A card or panel title: 17 SemiBold, 12 below.</summary>
    Card,
}

/// <summary>The shared section and card heading, see SectionHeader.xaml.</summary>
public partial class SectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(SectionHeader), new PropertyMetadata(null,
            (d, e) => ((SectionHeader)d).Tile.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(TileTone), typeof(SectionHeader), new PropertyMetadata(TileTone.Graphite));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(SectionHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(SectionLevel), typeof(SectionHeader),
        new PropertyMetadata(SectionLevel.Page, (d, _) => ((SectionHeader)d).ApplyLevel()));

    public SectionHeader()
    {
        InitializeComponent();
        ApplyLevel();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public Geometry? IconData { get => (Geometry?)GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }

    public TileTone Tone { get => (TileTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    /// <summary>A trailing link or button ("Vše"). One at most.</summary>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    public SectionLevel Level { get => (SectionLevel)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private void ApplyLevel()
    {
        var page = Level == SectionLevel.Page;
        TitleText.Style = (Style)FindResource(page ? "SectionHeading" : "PanelTitle");
        TitleText.Margin = new Thickness(0);
        Row.Margin = new Thickness(0, 0, 0, page ? 16 : 12);
    }
}
