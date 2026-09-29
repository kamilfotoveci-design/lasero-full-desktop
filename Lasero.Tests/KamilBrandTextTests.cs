using System.Reflection;
using Lasero.Core.LaseroApi;
using Xunit;

namespace Lasero.Tests;

public sealed class KamilBrandTextTests
{
    [Fact]
    public void AssistantInstructionForbidsQuestionAndExclamationMarks()
    {
        var field = typeof(LaseroChatClient).GetField("AssistantInstruction", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var instruction = Assert.IsType<string>(field!.GetRawConstantValue());

        Assert.Contains("Nepoužívej otazníky ani vykřičníky", instruction, StringComparison.Ordinal);

        // The instruction itself must follow the rule it hands to the model.
        Assert.DoesNotContain('?', instruction);
        Assert.DoesNotContain('!', instruction);
    }
}
