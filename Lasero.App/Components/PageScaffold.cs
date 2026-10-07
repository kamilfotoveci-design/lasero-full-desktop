using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lasero.App.Components;

/// <summary>
/// The frame every full screen sits in: a header band that never scrolls, then the scrolling content in one column
/// capped at Layout.ContentMaxWidth and anchored left. The page padding is 32, or 24 when the screen is narrower
/// than <see cref="CompactBelow"/>, so the same margin applies on Home, Zařízení and Chat. Only the content scrolls
/// (BodyScrolls false hands the height to the body, Chat), and its scrollbar sits on the screen edge.
///
/// Built in code rather than XAML on purpose: a UserControl whose own XAML registers element names cannot also host
/// named children from the screen that uses it (MC3093), and every screen names something.
/// </summary>
public sealed class PageScaffold : UserControl
{
    /// <summary>Narrower than this the page padding drops from 32 to 24.</summary>
    internal const double CompactBelow = 720;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(PageScaffold), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(PageScaffold), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyScrollsProperty = DependencyProperty.Register(
        nameof(BodyScrolls), typeof(bool), typeof(PageScaffold),
        new PropertyMetadata(true, (d, e) => ((PageScaffold)d)._scroller.VerticalScrollBarVisibility =
            (bool)e.NewValue ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled));

    private readonly Grid _headerBand = new();
    private readonly Grid _contentBand = new();
    private readonly ScrollViewer _scroller;

    public PageScaffold()
    {
        Focusable = false;
        const double maxWidth = 1120; // Layout.ContentMaxWidth (pinned by LayoutSystemTests)

        _headerBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        _contentBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        _headerBand.Children.Add(Presenter(HeaderProperty));
        _contentBand.Children.Add(Presenter(BodyProperty));

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _contentBand,
        };
        Grid.SetRow(_scroller, 1);

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_headerBand);
        root.Children.Add(_scroller);
        Content = root;

        ApplyPadding(32);
        SizeChanged += (_, e) => ApplyPadding(e.NewSize.Width < CompactBelow ? 24 : 32);
    }

    /// <summary>Normally a <see cref="PageHeader"/>.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The scrolling content.</summary>
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>False when the body manages its own scrolling and should fill the height (Chat).</summary>
    public bool BodyScrolls { get => (bool)GetValue(BodyScrollsProperty); set => SetValue(BodyScrollsProperty, value); }

    /// <summary>The page padding in effect (32, or 24 on a narrow screen).</summary>
    public double PagePadding { get; private set; }

    private ContentPresenter Presenter(DependencyProperty source)
    {
        var presenter = new ContentPresenter();
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding { Source = this, Path = new PropertyPath(source) });
        return presenter;
    }

    private void ApplyPadding(double padding)
    {
        if (PagePadding == padding) return;
        PagePadding = padding;
        _headerBand.Margin = new Thickness(padding, padding, padding, 24);
        _contentBand.Margin = new Thickness(padding, 0, padding, padding);
    }
}
