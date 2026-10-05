using Lasero.App.Controls;

namespace Lasero.Tests;

public sealed class RulerLabelLayoutTests
{
    [Theory]
    [InlineData(0, 12, 100, false)]
    [InlineData(6, 12, 100, true)]
    [InlineData(94, 12, 100, true)]
    [InlineData(100, 12, 100, false)]
    [InlineData(double.NaN, 12, 100, false)]
    public void VerticalLabelIsDrawnOnlyWhenFullyInsideRuler(double center, double height, double rulerHeight, bool expected)
    {
        Assert.Equal(expected, RulerLabelLayout.FitsVertically(center, height, rulerHeight));
    }
}
