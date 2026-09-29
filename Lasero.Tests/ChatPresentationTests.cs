using Lasero.App;

namespace Lasero.Tests;

public sealed class ChatPresentationTests
{
    [Fact]
    public void NormalizerRemovesMarkdownChromeButKeepsMeaning()
    {
        var result = ChatResponseNormalizer.Normalize("## Parametry\n**Výkon:** 30 %\n- rychlost\n1. test na odřezku\n[Dokumentace](https://lasero.net)");

        Assert.DoesNotContain("##", result, StringComparison.Ordinal);
        Assert.DoesNotContain("**", result, StringComparison.Ordinal);
        Assert.Contains("Parametry", result, StringComparison.Ordinal);
        Assert.Contains("• rychlost", result, StringComparison.Ordinal);
        Assert.Contains("1. test na odřezku", result, StringComparison.Ordinal);
        Assert.Contains("Dokumentace (https://lasero.net)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizerPreservesFencedCodeContents()
    {
        var result = ChatResponseNormalizer.Normalize("Nastav:\n```gcode\nG1 X10 Y20\n```\nHotovo.");

        Assert.Contains("G1 X10 Y20", result, StringComparison.Ordinal);
        Assert.DoesNotContain("```", result, StringComparison.Ordinal);
        Assert.Contains("Hotovo.", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceDefaultsContainSafeAssistantSizeBounds()
    {
        var preferences = new WorkspacePreferences();

        Assert.Equal(420, preferences.ClampedAssistantWidth);
        Assert.Equal(560, preferences.ClampedAssistantHeight);
        Assert.InRange(preferences.ClampedAssistantWidth, WorkspacePreferences.MinAssistantWidth, WorkspacePreferences.MaxAssistantWidth);
        Assert.InRange(preferences.ClampedAssistantHeight, WorkspacePreferences.MinAssistantHeight, WorkspacePreferences.MaxAssistantHeight);
    }
}
