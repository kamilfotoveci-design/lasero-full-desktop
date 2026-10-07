using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace Lasero.App.Components;

/// <summary>
/// One section of a docked panel (the Návrh inspector, any right panel): a hairline above (not on the first section),
/// 24 and 20 px padding (Layout.InspectorPadding), a Card-level <see cref="SectionHeader"/> with an optional trailing
/// control, then the content. Sections are separated by this hairline only; there is no card inside the panel.
/// Code only, for the same reason as <see cref="PageScaffold"/>: the content is named by the panel that uses it.
/// </summary>
[ContentProperty(nameof(Body))]
public sealed class InspectorSection : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(InspectorSection), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderActionProperty = DependencyProperty.Register(
        nameof(HeaderAction), typeof(object), typeof(InspectorSection), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(InspectorSection), new PropertyMetadata(null));

    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.Register(
        nameof(IsFirst), typeof(bool), typeof(InspectorSection),
        new PropertyMetadata(false, (d, e) => ((InspectorSection)d)._frame.BorderThickness = new Thickness(0, (bool)e.NewValue ? 0 : 1, 0, 0)));

    private readonly Border _frame = new() { BorderThickness = new Thickness(0, 1, 0, 0) };

    public InspectorSection()
    {
        Focusable = false;
        _frame.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        _frame.SetResourceReference(Border.PaddingProperty, "Layout.InspectorPadding");

        var header = new SectionHeader { Level = SectionLevel.Card };
        header.SetBinding(SectionHeader.TitleProperty, new Binding { Source = this, Path = new PropertyPath(HeaderProperty) });
        header.SetBinding(SectionHeader.ActionProperty, new Binding { Source = this, Path = new PropertyPath(HeaderActionProperty) });
        var body = new ContentPresenter();
        body.SetBinding(ContentPresenter.ContentProperty, new Binding { Source = this, Path = new PropertyPath(BodyProperty) });

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        _frame.Child = stack;
        Content = _frame;
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public object? HeaderAction { get => GetValue(HeaderActionProperty); set => SetValue(HeaderActionProperty, value); }

    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>The first section of a panel has no hairline above it.</summary>
    public bool IsFirst { get => (bool)GetValue(IsFirstProperty); set => SetValue(IsFirstProperty, value); }
}
