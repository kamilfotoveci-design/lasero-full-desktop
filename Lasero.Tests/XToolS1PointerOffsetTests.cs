using System.Globalization;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public class XToolS1PointerOffsetTests
{
    private static XToolS1CalibrationScope Scope() => new("operator-selected-S1", "20W", "operator-recorded-firmware", 42);

    [Fact]
    public void PublishedExampleIsParsedWithExplicitScope()
    {
        var scope = Scope();
        Assert.True(XToolS1PointerOffset.TryParse("M1111 X0.00 Y3.00", scope, out var result));
        Assert.Equal(0, result!.Xmm);
        Assert.Equal(3, result.Ymm);
        Assert.Same(scope, result.Scope);
    }

    [Theory]
    [InlineData("cs-CZ")]
    [InlineData("sk-SK")]
    public void DecimalPointsAreInvariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.True(XToolS1PointerOffset.TryParse("M1111 X-0.25 Y+3.50", Scope(), out var result));
            Assert.Equal(-0.25, result!.Xmm);
            Assert.Equal(3.5, result.Ymm);
            Assert.False(XToolS1PointerOffset.TryParse("M1111 X0,25 Y3,50", Scope(), out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("M1111 X0.00")]
    [InlineData("M1111 X0.00 Y")]
    [InlineData("M1111 XNaN Y3")]
    [InlineData("M1111 XInfinity Y3")]
    [InlineData("M1111 X1e309 Y3")]
    [InlineData("M1111 X1 Y2 Z3")]
    [InlineData("M1111 X1 Y2\nok")]
    [InlineData("M1111 X1 Y2\r\n")]
    [InlineData("ok M1111 X1 Y2")]
    [InlineData("M1111 X1 Y2 X3")]
    [InlineData("M1111 Y2 X1")]
    [InlineData("M1111 X1Y2")]
    public void RejectsMalformedOrCombinedResponses(string? response)
    {
        Assert.False(XToolS1PointerOffset.TryParse(response, Scope(), out var result));
        Assert.Null(result);
    }

    [Fact]
    public void RejectsUnboundedResponse()
    {
        Assert.False(XToolS1PointerOffset.TryParse("M1111 X" + new string('9', 310) + " Y3", Scope(), out _));
    }

    [Fact]
    public void CalibrationCannotFollowChangedMachineModuleFirmwareOrHeight()
    {
        Assert.True(XToolS1PointerOffset.TryParse("M1111 X0 Y3", Scope(), out var result));
        Assert.True(result!.IsApplicableTo(Scope()));
        Assert.False(result.IsApplicableTo(new("another-S1", "20W", "operator-recorded-firmware", 42)));
        Assert.False(result.IsApplicableTo(new("operator-selected-S1", "40W", "operator-recorded-firmware", 42)));
        Assert.False(result.IsApplicableTo(new("operator-selected-S1", "20W", "different-firmware", 42)));
        Assert.False(result.IsApplicableTo(new("operator-selected-S1", "20W", "operator-recorded-firmware", 42.001)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidHeightIsRejected(double height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new XToolS1CalibrationScope("S1", "20W", "firmware", height));

    [Fact]
    public void MissingContextIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new XToolS1CalibrationScope("", "20W", "firmware", 42));
        Assert.Throws<ArgumentException>(() => new XToolS1CalibrationScope("S1", " ", "firmware", 42));
        Assert.Throws<ArgumentException>(() => new XToolS1CalibrationScope("S1", "20W", "", 42));
        Assert.Throws<ArgumentNullException>(() => XToolS1PointerOffset.TryParse("M1111 X0 Y3", null!, out _));
    }
}
