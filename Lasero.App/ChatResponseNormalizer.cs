using System.Text;
using System.Text.RegularExpressions;

namespace Lasero.App;

/// <summary>Turns model markdown into compact, readable WPF text while leaving fenced code intact.</summary>
public static partial class ChatResponseNormalizer
{
    public static string Normalize(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;

        var lines = response.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder(response.Length);
        var inCode = false;
        foreach (var source in lines)
        {
            var line = source.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                output.AppendLine(line);
                continue;
            }

            line = HeadingRegex().Replace(line, string.Empty);
            line = BulletRegex().Replace(line, "• ");
            line = NumberRegex().Replace(line, "$1 ");
            line = InlineCodeRegex().Replace(line, "$1");
            line = StrongRegex().Replace(line, "$1");
            line = EmphasisRegex().Replace(line, "$1");
            line = LinkRegex().Replace(line, "$1 ($2)");
            line = line.Trim();
            if (line.Length == 0)
            {
                if (output.Length > 0 && !output.ToString().EndsWith("\n\n", StringComparison.Ordinal))
                    output.AppendLine();
                continue;
            }

            output.AppendLine(line);
        }

        return output.ToString().Trim();
    }

    [GeneratedRegex("^\\s{0,3}#{1,6}\\s*")] private static partial Regex HeadingRegex();
    [GeneratedRegex("^\\s*[-*+]\\s+")] private static partial Regex BulletRegex();
    [GeneratedRegex("^\\s*(\\d+[.)])\\s+")] private static partial Regex NumberRegex();
    [GeneratedRegex("`([^`]+)`")] private static partial Regex InlineCodeRegex();
    [GeneratedRegex("\\*\\*([^*]+)\\*\\*")] private static partial Regex StrongRegex();
    [GeneratedRegex("(?<!\\*)\\*([^*]+)\\*(?!\\*)|_([^_]+)_")] private static partial Regex EmphasisRegex();
    [GeneratedRegex("\\[([^]]+)\\]\\(([^)]+)\\)")] private static partial Regex LinkRegex();
}
