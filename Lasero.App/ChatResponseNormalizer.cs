using System.Text;
using System.Text.RegularExpressions;

namespace Lasero.App;

/// <summary>
/// Turns model markdown into compact, readable WPF text while leaving fenced code intact, and keeps a
/// runaway answer short.
///
/// KAMIL is written for complete beginners (see <c>LaseroChatClient.AssistantInstruction</c>: about 60
/// words, at most 3 steps). The model does not always obey, so <see cref="Condense"/> is the backstop:
/// a reply that is clearly over the limit is shown as its first sentences and steps, at a sentence
/// boundary, with the rest one "Zobrazit více" click away. A reply within the limit is never touched.
/// </summary>
public static partial class ChatResponseNormalizer
{
    /// <summary>Above this many words (code excluded) a reply is condensed. Deliberately above the 60
    /// the model is asked for, so a slightly long but sensible answer is shown whole.</summary>
    public const int SoftWordLimit = 90;

    /// <summary>What a condensed reply is trimmed to, at a sentence boundary.</summary>
    public const int ShortWordBudget = 70;

    /// <summary>Steps or bullets shown in a condensed reply.</summary>
    public const int MaxShortListItems = 3;

    public static string Normalize(string? response) => Join(NormalizeLines(response));

    /// <summary>The normalized reply, plus a short version when the reply runs long. Never cuts
    /// mid-sentence and never cuts a code block in half.</summary>
    public static CondensedResponse Condense(string? response)
    {
        var lines = NormalizeLines(response);
        var full = Join(lines);
        if (full.Length == 0) return new CondensedResponse(string.Empty, string.Empty, false);

        var words = lines.Where(line => !line.IsCode).Sum(line => CountWords(line.Text));
        var listItems = lines.Count(line => !line.IsCode && IsListItem(line.Text));
        if (words <= SoftWordLimit && listItems <= MaxShortListItems + 1)
            return new CondensedResponse(full, full, false);

        var kept = new List<Line>();
        var budget = 0;
        var items = 0;
        var stopped = false;
        var pendingBlank = false;

        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
            {
                pendingBlank = kept.Count > 0;
                continue;
            }

            if (line.IsCode)
            {
                if (pendingBlank) kept.Add(Line.Blank);
                pendingBlank = false;
                kept.Add(line);
                continue;
            }

            var isItem = IsListItem(line.Text);
            if (isItem && items >= MaxShortListItems)
            {
                stopped = true;
                break;
            }

            var lineWords = CountWords(line.Text);
            if (budget + lineWords <= ShortWordBudget)
            {
                if (pendingBlank) kept.Add(Line.Blank);
                pendingBlank = false;
                kept.Add(line);
                budget += lineWords;
                if (isItem) items++;
                continue;
            }

            // The line is what pushes the answer over. Keep whole sentences of it that still fit; if
            // not even one does and nothing has been kept yet, keep its first sentence so the answer
            // is never empty.
            var partial = new StringBuilder();
            foreach (var sentence in SentenceBoundaryRegex().Split(line.Text))
            {
                var sentenceWords = CountWords(sentence);
                if (budget + sentenceWords > ShortWordBudget && (partial.Length > 0 || kept.Count > 0)) break;
                if (partial.Length > 0) partial.Append(' ');
                partial.Append(sentence);
                budget += sentenceWords;
                if (budget > ShortWordBudget) break;
            }

            if (partial.Length > 0)
            {
                if (pendingBlank) kept.Add(Line.Blank);
                kept.Add(new Line(partial.ToString(), false));
            }

            stopped = true;
            break;
        }

        // A lead-in such as "Postup:" with its steps cut away, or a bare marker such as "1." whose text
        // was on the next line, reads as broken.
        while (kept.Count > 0 && (kept[^1].Text.Length == 0
            || (!kept[^1].IsCode && (kept[^1].Text.EndsWith(':') || BareMarkerRegex().IsMatch(kept[^1].Text)))))
            kept.RemoveAt(kept.Count - 1);

        var shortText = Join(kept);
        if (!stopped || shortText.Length == 0 || shortText == full)
            return new CondensedResponse(full, full, false);
        return new CondensedResponse(shortText, full, true);
    }

    private static List<Line> NormalizeLines(string? response)
    {
        var output = new List<Line>();
        if (string.IsNullOrWhiteSpace(response)) return output;

        var table = new List<string>();
        var inCode = false;
        foreach (var source in response.Replace("\r\n", "\n").Split('\n'))
        {
            var line = source.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushTable(table, output);
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                output.Add(new Line(line, true));
                continue;
            }

            if (TableRowRegex().IsMatch(line))
            {
                table.Add(line);
                continue;
            }

            FlushTable(table, output);
            AddCleaned(output, line);
        }

        FlushTable(table, output);
        while (output.Count > 0 && output[^1].Text.Length == 0 && !output[^1].IsCode)
            output.RemoveAt(output.Count - 1);
        return output;
    }

    private static void AddCleaned(List<Line> output, string source)
    {
        var line = Clean(source);
        if (line.Length == 0)
        {
            if (output.Count > 0 && output[^1].Text.Length > 0) output.Add(Line.Blank);
            return;
        }

        output.Add(new Line(line, false));
    }

    private static string Clean(string line)
    {
        line = HeadingRegex().Replace(line, string.Empty);
        line = BulletRegex().Replace(line, "• ");
        line = NumberRegex().Replace(line, "$1 ");
        line = InlineCodeRegex().Replace(line, "$1");
        line = StrongRegex().Replace(line, "$1");
        line = EmphasisRegex().Replace(line, "$1");
        line = LinkRegex().Replace(line, "$1 ($2)");
        return line.Trim();
    }

    /// <summary>A markdown table does not render as one in a TextBlock; it shows as rows of pipes. Each
    /// row becomes a plain line ("Výkon: 60 %", or cells joined with a hyphen for wider tables), the
    /// separator row disappears, and the header row of a two-column table (typically "Parametr |
    /// Hodnota") is dropped because it carries no information.</summary>
    private static void FlushTable(List<string> table, List<Line> output)
    {
        if (table.Count == 0) return;

        var hasSeparator = table.Any(row => TableSeparatorRegex().IsMatch(row));
        var rows = table
            .Where(row => !TableSeparatorRegex().IsMatch(row))
            .Select(row => row.Trim().Trim('|').Split('|')
                .Select(cell => Clean(cell))
                .Where(cell => cell.Length > 0)
                .ToArray())
            .Where(cells => cells.Length > 0)
            .ToList();
        table.Clear();

        if (hasSeparator && rows.Count > 1 && rows[0].Length <= 2) rows.RemoveAt(0);
        foreach (var cells in rows)
            output.Add(new Line(cells.Length == 2 ? $"{cells[0]}: {cells[1]}" : string.Join(" - ", cells), false));
    }

    private static string Join(IEnumerable<Line> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines) builder.Append(line.Text).Append('\n');
        return builder.ToString().Trim().Replace("\n", Environment.NewLine);
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsListItem(string text) =>
        text.StartsWith("• ", StringComparison.Ordinal) || NumberedItemRegex().IsMatch(text);

    private readonly record struct Line(string Text, bool IsCode)
    {
        public static Line Blank => new(string.Empty, false);
    }

    [GeneratedRegex("^\\s{0,3}#{1,6}\\s*")] private static partial Regex HeadingRegex();
    [GeneratedRegex("^\\s*[-*+]\\s+")] private static partial Regex BulletRegex();
    [GeneratedRegex("^\\s*(\\d+[.)])\\s+")] private static partial Regex NumberRegex();
    [GeneratedRegex("^\\d+[.)]\\s")] private static partial Regex NumberedItemRegex();
    [GeneratedRegex("^([0-9]+[.)]|•)$")] private static partial Regex BareMarkerRegex();
    [GeneratedRegex("`([^`]+)`")] private static partial Regex InlineCodeRegex();
    [GeneratedRegex("\\*\\*([^*]+)\\*\\*")] private static partial Regex StrongRegex();
    [GeneratedRegex("(?<!\\*)\\*([^*]+)\\*(?!\\*)|_([^_]+)_")] private static partial Regex EmphasisRegex();
    [GeneratedRegex("\\[([^]]+)\\]\\(([^)]+)\\)")] private static partial Regex LinkRegex();
    [GeneratedRegex("^\\s*\\|.*\\|\\s*$")] private static partial Regex TableRowRegex();
    [GeneratedRegex("^\\s*\\|?\\s*:?-{2,}:?\\s*(\\|\\s*:?-{2,}:?\\s*)*\\|?\\s*$")] private static partial Regex TableSeparatorRegex();
    [GeneratedRegex("(?<=[.!?…])\\s+")] private static partial Regex SentenceBoundaryRegex();
}

/// <summary>A reply as shown by default (<see cref="Short"/>), with the complete text
/// (<see cref="Full"/>) behind "Zobrazit více" when <see cref="IsTruncated"/>.</summary>
public sealed record CondensedResponse(string Short, string Full, bool IsTruncated);
