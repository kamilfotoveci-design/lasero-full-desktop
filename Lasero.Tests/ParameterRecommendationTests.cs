using Lasero.App.ViewModels;

namespace Lasero.Tests;

public sealed class ParameterRecommendationTests
{
    [Fact]
    public void ParsesSpeedAndPowerFromATypicalCzechAnswer()
    {
        var recommendation = ParameterRecommendation.TryParse(
            "Doporučuji rychlost 4000 mm/min a výkon 40 %, průchody 2.");

        Assert.NotNull(recommendation);
        Assert.Equal(4000, recommendation!.SpeedMmPerMinute);
        Assert.Equal(40, recommendation.PowerPercent);
        Assert.Equal(2, recommendation.Passes);
    }

    [Fact]
    public void PassesDefaultsToOneWhenTheAnswerNamesNone()
    {
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %");

        Assert.NotNull(recommendation);
        Assert.Equal(1, recommendation!.Passes);
    }

    [Fact]
    public void ParsesDpiIntoAFillLineInterval()
    {
        var recommendation = ParameterRecommendation.TryParse(
            "rychlost 3000 mm/min, výkon 30 %, rozlišení 300 dpi");

        Assert.NotNull(recommendation);
        Assert.Equal(300, recommendation!.Dpi);
        Assert.True(recommendation.HasDpi);
        Assert.Equal(25.4 / 300, recommendation.FillLineIntervalMm, 6);
    }

    [Fact]
    public void FillLineIntervalFallsBackToTheDefaultWhenNoDpiIsNamed()
    {
        var recommendation = ParameterRecommendation.TryParse("rychlost 3000 mm/min, výkon 30 %");

        Assert.NotNull(recommendation);
        Assert.False(recommendation!.HasDpi);
        Assert.Equal(25.4 / 254, recommendation.FillLineIntervalMm, 6);
    }

    [Theory]
    [InlineData("speed 4000mm/min, power 40%")]
    [InlineData("posuv: 4000 mm/min, výkon: 40 %")]
    [InlineData("rychlost 4 000 mm/min, výkon 40,0 %")]
    public void AcceptsUnitAndLocaleVariations(string text)
    {
        var recommendation = ParameterRecommendation.TryParse(text);

        Assert.NotNull(recommendation);
        Assert.Equal(4000, recommendation!.SpeedMmPerMinute);
        Assert.Equal(40, recommendation.PowerPercent);
    }

    [Fact]
    public void ReturnsNullWithoutAnyRecognizableNumbers()
    {
        Assert.Null(ParameterRecommendation.TryParse("Zkuste jiný materiál, tento se nehodí."));
    }

    [Fact]
    public void ReturnsNullOnEmptyOrWhitespaceText()
    {
        Assert.Null(ParameterRecommendation.TryParse(null));
        Assert.Null(ParameterRecommendation.TryParse(string.Empty));
        Assert.Null(ParameterRecommendation.TryParse("   "));
    }

    [Fact]
    public void RequiresPowerEvenWhenSpeedIsPresent()
    {
        // Speed and power are the two required fields — a card offering half a recipe is worse than
        // no card, so a reply naming only one must not produce a recommendation.
        Assert.Null(ParameterRecommendation.TryParse("rychlost 4000 mm/min"));
    }

    [Fact]
    public void RequiresSpeedEvenWhenPowerIsPresent()
    {
        Assert.Null(ParameterRecommendation.TryParse("výkon 40 %"));
    }

    [Fact]
    public void RequiresTheUnitNextToSpeedSoAnUnrelatedNumberIsNotMistakenForAFeedRate()
    {
        // "40 mm tloušťky materiálu" must never be read as a feed rate just because a nearby number
        // happens to be followed by "mm".
        Assert.Null(ParameterRecommendation.TryParse("Materiál má 40 mm tloušťky, výkon 40 %."));
    }

    [Theory]
    [InlineData("rychlost 0 mm/min, výkon 40 %")]
    [InlineData("rychlost 70000 mm/min, výkon 40 %")]
    public void RejectsSpeedOutsideWhatADiodeEngraverCanBeSetTo(string text)
    {
        Assert.Null(ParameterRecommendation.TryParse(text));
    }

    [Theory]
    [InlineData("rychlost 4000 mm/min, výkon 150 %")]
    public void RejectsPowerOutsideZeroToOneHundredPercent(string text)
    {
        Assert.Null(ParameterRecommendation.TryParse(text));
    }

    [Fact]
    public void ClampsPassesOutsideOneToFiftyBackToOneRatherThanRejectingTheWholeRecommendation()
    {
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %, průchodů 99");

        Assert.NotNull(recommendation);
        Assert.Equal(1, recommendation!.Passes);
    }

    [Fact]
    public void IgnoresDpiOutsideFiftyToTwoThousandRatherThanRejectingTheWholeRecommendation()
    {
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %, rozlišení 5000 dpi");

        Assert.NotNull(recommendation);
        Assert.False(recommendation!.HasDpi);
    }

    [Fact]
    public void OriginLayerIdIsNullUntilExplicitlySet()
    {
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %");

        Assert.NotNull(recommendation);
        Assert.Null(recommendation!.OriginLayerId);
    }
}
