using Lasero.Core.LaseroApi;
using Xunit;

namespace Lasero.Tests;

public sealed class KamilBrandTextTests
{
    [Fact]
    public void KamilMayAskOneNaturalQuestionAndMustNotInventMachineFacts()
    {
        var instruction = KamilResponsePolicy.BuildInstruction("cs");

        Assert.Contains("one useful follow-up question", instruction, StringComparison.Ordinal);
        Assert.Contains("Never infer laser wattage", instruction, StringComparison.Ordinal);
        Assert.Contains("Never claim an action succeeded", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("never use question marks", instruction, StringComparison.OrdinalIgnoreCase);
    }
}
