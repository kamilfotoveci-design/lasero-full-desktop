using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// An answer laid out as a conversation reads: plain paragraphs with a gap between them, and numbered
/// or bulleted steps with a hanging marker. It is deliberately not markdown. The text is already
/// normalised (see <see cref="ChatResponseNormalizer"/>); this only gives its blank lines and its
/// steps the spacing a single TextBlock cannot.
/// </summary>
public sealed partial class KamilMessageBody : Decorator
{
    private const double ParagraphGap = 8;
    private const double StepGap = 4;
    private const double MarkerWidth = 24;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(KamilMessageBody),
        new PropertyMetadata(string.Empty, (d, _) => ((KamilMessageBody)d).Rebuild()));

    /// <summary>Overrides the text style, for a host that wants different text rendering options.
    /// Defaults to <c>Kamil.MessageText</c>.</summary>
    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(KamilMessageBody),
        new PropertyMetadata(null, (d, _) => ((KamilMessageBody)d).Rebuild()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    public KamilMessageBody() => Rebuild();

    private void Rebuild()
    {
        var panel = new StackPanel();
        var style = TextStyle ?? Application.Current?.TryFindResource("Kamil.MessageText") as Style;
        var paragraph = new List<string>();
        var blankBefore = false;
        var previousWasStep = false;
        var first = true;

        void AddBlock(UIElement block, bool isStep)
        {
            var gap = first ? 0 : blankBefore || (previousWasStep && !isStep) ? ParagraphGap : StepGap;
            if (block is FrameworkElement element) element.Margin = new Thickness(0, gap, 0, 0);
            panel.Children.Add(block);
            first = false;
            blankBefore = false;
            previousWasStep = isStep;
        }

        TextBlock NewText(string text)
        {
            var block = new TextBlock { Text = text };
            if (style is not null) block.Style = style;
            return block;
        }

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            AddBlock(NewText(string.Join("\n", paragraph)), isStep: false);
            paragraph.Clear();
        }

        foreach (var raw in (Text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
                blankBefore = !first;
                continue;
            }

            var step = StepRegex().Match(line);
            if (!step.Success)
            {
                paragraph.Add(line);
                continue;
            }

            FlushParagraph();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MarkerWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = NewText(step.Groups["marker"].Value);
            marker.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            var body = NewText(step.Groups["text"].Value);
            Grid.SetColumn(body, 1);
            row.Children.Add(marker);
            row.Children.Add(body);
            AddBlock(row, isStep: true);
        }

        FlushParagraph();
        Child = panel;
    }

    [GeneratedRegex(@"^(?<marker>\d+[.)]|•)\s+(?<text>.+)$")]
    private static partial Regex StepRegex();
}
