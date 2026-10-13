using Lasero.App;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

/// <summary>KAMIL answers a beginner briefly: the prompt asks for it, and the normalizer trims an
/// answer that ignores the prompt without cutting it mid-sentence.</summary>
public sealed class ChatBrevityTests
{
    [Fact]
    public void PolicyRequestsConversationalBrevityWithoutSuppressingSafety()
    {
        var instruction = KamilResponsePolicy.BuildInstruction("sk");

        Assert.Contains("25-60 words", instruction, StringComparison.Ordinal);
        Assert.Contains("1-3 short", instruction, StringComparison.Ordinal);
        Assert.Contains("one useful follow-up question", instruction, StringComparison.Ordinal);
        Assert.Contains("safety warning", instruction, StringComparison.Ordinal);
        Assert.Contains("unknown", instruction, StringComparison.Ordinal);
        Assert.EndsWith("Reply in Slovak.", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageFollowsExplicitChoiceOrTheLatestClearUserMessage()
    {
        Assert.Equal("sk", KamilResponsePolicy.ResolveLanguage("Ako môžem vytrasovať toto logo?"));
        Assert.Equal("cs", KamilResponsePolicy.ResolveLanguage("Jak můžu vytrasovat tohle logo?"));
        Assert.Equal("en", KamilResponsePolicy.ResolveLanguage("How can I trace this image?"));
        Assert.Equal("sk", KamilResponsePolicy.ResolveLanguage("Jak můžu vytrasovat tohle logo?", "sk"));
        Assert.Equal("cs", KamilResponsePolicy.ResolveLanguage("Logo"));
    }

    [Fact]
    public void NormalizerDoesNotRewriteTheWordingOfAnAnswer()
    {
        const string reply = "Rád ti pomohu, tvůj první pokus vyjde.";

        Assert.Equal(reply, ChatResponseNormalizer.Normalize(reply));
        Assert.Equal(reply, ChatResponseNormalizer.Condense(reply).Short);
    }

    [Fact]
    public void ShortAnswerIsLeftAlone()
    {
        var reply = "Výkon: 60 %\nRychlost: 3000 mm/min\nPrůchody: 2\nNejdřív vyzkoušej na odřezku.";

        var result = ChatResponseNormalizer.Condense(reply);

        Assert.False(result.IsTruncated);
        Assert.Equal(result.Full, result.Short);
        Assert.Contains("Rychlost: 3000 mm/min", result.Short);
    }

    [Fact]
    public void TableBecomesPlainLinesAndTheContentlessHeaderDisappears()
    {
        var reply = "| Parametr | Hodnota |\n|---|---|\n| Výkon | 60 % |\n| Rychlost | 3000 mm/min |";

        var result = ChatResponseNormalizer.Normalize(reply);

        Assert.DoesNotContain("|", result);
        Assert.DoesNotContain("Parametr", result);
        Assert.Equal("Výkon: 60 %" + Environment.NewLine + "Rychlost: 3000 mm/min", result);
    }

    [Fact]
    public void WideTableKeepsItsHeaderJoinedWithHyphens()
    {
        var result = ChatResponseNormalizer.Normalize("| Materiál | Výkon | Rychlost |\n|---|---|---|\n| Překližka | 60 % | 3000 |");

        Assert.Equal("Materiál - Výkon - Rychlost" + Environment.NewLine + "Překližka - 60 % - 3000", result);
    }

    [Fact]
    public void RunawayAnswerIsCutAtASentenceBoundaryUnderTheBudget()
    {
        var sentences = Enumerable.Range(1, 30).Select(i => $"Věta číslo {i} je stále stejně dlouhá jako ostatní věty.");
        var reply = string.Join(" ", sentences);

        var result = ChatResponseNormalizer.Condense(reply);

        Assert.True(result.IsTruncated);
        Assert.Equal(reply, result.Full);
        Assert.EndsWith(".", result.Short);
        Assert.True(result.Short.Split(' ').Length <= ChatResponseNormalizer.ShortWordBudget);
        Assert.True(reply.StartsWith(result.Short, StringComparison.Ordinal), "the short text is a prefix, nothing is reworded");
    }

    [Fact]
    public void LongListKeepsOnlyTheFirstThreeSteps()
    {
        var reply = "Postup:\n" + string.Join("\n", Enumerable.Range(1, 8).Select(i => $"{i}. Krok {i} popisuje jednu drobnou činnost při přípravě."));

        var result = ChatResponseNormalizer.Condense(reply);

        Assert.True(result.IsTruncated);
        Assert.Contains("3. Krok 3", result.Short);
        Assert.DoesNotContain("4. Krok 4", result.Short);
        Assert.Contains("8. Krok 8", result.Full);
    }

    [Fact]
    public void HeadingsAreStrippedAndCodeBlocksStayIntact()
    {
        var filler = string.Join(" ", Enumerable.Repeat("Toto je doplňující věta bez nového obsahu.", 20));
        var reply = "## Postup\nNejdřív vlož kód.\n```gcode\nG1 X10 Y20\n\nG1 X30\n```\n" + filler;

        var result = ChatResponseNormalizer.Condense(reply);

        Assert.DoesNotContain("#", result.Short);
        Assert.Contains("G1 X10 Y20", result.Short);
        Assert.Contains("G1 X30", result.Short);
        Assert.True(result.IsTruncated);
        Assert.True(result.Short.Length < result.Full.Length);
    }

    [Fact]
    public void SingleGiantParagraphStillYieldsWholeSentences()
    {
        var reply = string.Join(" ", Enumerable.Repeat("Toto je jedna docela obyčejná věta o laseru.", 40));

        var result = ChatResponseNormalizer.Condense(reply);

        Assert.True(result.IsTruncated);
        Assert.EndsWith("laseru.", result.Short);
    }

    [Fact]
    public void EmptyReplyCondensesToNothing()
    {
        var result = ChatResponseNormalizer.Condense("  ");

        Assert.Equal(string.Empty, result.Short);
        Assert.False(result.IsTruncated);
    }
}
