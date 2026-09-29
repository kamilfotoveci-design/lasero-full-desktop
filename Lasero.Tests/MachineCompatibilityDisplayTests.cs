using Lasero.Core.Machines;
using Xunit;

namespace Lasero.Tests;

public sealed class MachineCompatibilityDisplayTests
{
    [Fact]
    public void EveryCompatibilityOptionRendersItsReadableDisplayName()
    {
        Assert.NotEmpty(MachineCompatibilityCatalog.All);
        foreach (var option in MachineCompatibilityCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.DisplayName));
            Assert.Equal(option.DisplayName, option.ToString());
            Assert.DoesNotContain("MachineCompatibility", option.ToString());
            Assert.DoesNotContain("{", option.ToString());
        }
    }
}
