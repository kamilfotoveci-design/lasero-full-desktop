using Lasero.Core.Raster;

namespace Lasero.Tests;

public class ImageProcessorTests
{
    private static GrayscaleImage MakeImage(int width, int height, params byte[] luminance) =>
        new() { Width = width, Height = height, Luminance = luminance };

    [Fact]
    public void GrayscaleMapping_PowerIsInverseOfLuminance()
    {
        var image = MakeImage(1, 1, 64);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions());

        Assert.Equal(1.0 - 64.0 / 255.0, result.At(0, 0), precision: 6);
    }

    [Fact]
    public void ThresholdMode_BelowThresholdBurnsFullPowerAboveDoesNot()
    {
        var image = MakeImage(2, 1, 50, 200);
        var options = new ImageProcessingOptions { UseThreshold = true, ThresholdValue = 128 };

        var result = ImageProcessor.Process(image, options);

        Assert.Equal(1.0, result.At(0, 0));
        Assert.Equal(0.0, result.At(1, 0));
    }

    [Fact]
    public void Invert_FlipsWhichPixelsBurn()
    {
        var image = MakeImage(2, 1, 0, 255); // black, white

        var normal = ImageProcessor.Process(image, new ImageProcessingOptions());
        var inverted = ImageProcessor.Process(image, new ImageProcessingOptions { Invert = true });

        Assert.Equal(1.0, normal.At(0, 0), precision: 6);   // black burns fully
        Assert.Equal(0.0, normal.At(1, 0), precision: 6);   // white doesn't burn
        Assert.Equal(0.0, inverted.At(0, 0), precision: 6); // inverted: black -> no burn
        Assert.Equal(1.0, inverted.At(1, 0), precision: 6); // inverted: white -> full burn
    }

    [Fact]
    public void Brightness_ExtremePositiveClampsToNoBurn()
    {
        var image = MakeImage(1, 1, 0); // pure black — would normally burn fully

        var result = ImageProcessor.Process(image, new ImageProcessingOptions { Brightness = 1000 });

        Assert.Equal(0.0, result.At(0, 0), precision: 6);
    }

    [Fact]
    public void Brightness_ExtremeNegativeClampsToFullBurn()
    {
        var image = MakeImage(1, 1, 255); // pure white — would normally not burn

        var result = ImageProcessor.Process(image, new ImageProcessingOptions { Brightness = -1000 });

        Assert.Equal(1.0, result.At(0, 0), precision: 6);
    }

    [Fact]
    public void Contrast_ZeroLeavesValuesUnchanged()
    {
        var image = MakeImage(1, 1, 90);

        var noContrast = ImageProcessor.Process(image, new ImageProcessingOptions());
        var explicitZero = ImageProcessor.Process(image, new ImageProcessingOptions { Contrast = 0 });

        Assert.Equal(noContrast.At(0, 0), explicitZero.At(0, 0), precision: 9);
    }

    [Fact]
    public void Contrast_ExtremePushesMidtonesTowardExtremesAndClamps()
    {
        // A pixel darker than mid-gray (128) should get pushed toward pure black (full burn) under high contrast.
        var image = MakeImage(1, 1, 100);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions { Contrast = 250 });

        Assert.Equal(1.0, result.At(0, 0), precision: 6);
    }

    [Fact]
    public void Dithering_ConstantMidGrayRowProducesDeterministicAlternatingPattern()
    {
        // Hand-verified Floyd-Steinberg error diffusion for four constant luminance=128 pixels in a
        // single row (no vertical diffusion possible with height=1): darkness ~0.498039 per pixel
        // propagates as 0,1,0,1 — see ImageProcessor.Dither.
        var image = MakeImage(4, 1, 128, 128, 128, 128);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions { UseDithering = true });

        Assert.Equal(new[] { 0.0, 1.0, 0.0, 1.0 }, result.PowerFraction);
    }

    [Fact]
    public void Dithering_IsDeterministicAcrossRepeatedRuns()
    {
        var image = MakeImage(5, 3, 10, 240, 60, 180, 128, 5, 250, 90, 90, 200, 15, 15, 15, 15, 15);
        var options = new ImageProcessingOptions { UseDithering = true };

        var first = ImageProcessor.Process(image, options);
        var second = ImageProcessor.Process(image, options);

        Assert.Equal(first.PowerFraction, second.PowerFraction);
    }

    [Fact]
    public void Dithering_TakesPrecedenceOverThreshold()
    {
        var image = MakeImage(4, 1, 128, 128, 128, 128);
        var options = new ImageProcessingOptions { UseDithering = true, UseThreshold = true, ThresholdValue = 200 };

        var result = ImageProcessor.Process(image, options);

        // If threshold (not dither) had won, every pixel would burn (128 < 200) — dithering instead
        // produces the alternating pattern, proving the documented precedence.
        Assert.Equal(new[] { 0.0, 1.0, 0.0, 1.0 }, result.PowerFraction);
    }

    // ===================== Ported from the web app's photo tool =====================
    // The desktop and the browser have to burn the same file the same way. These pin the operations
    // that were missing, the order they run in, and the algorithms the web app offers.

    [Fact]
    public void EveryAdjustmentDefaultsToNoChange()
    {
        var image = MakeImage(3, 3, 10, 40, 70, 100, 128, 160, 190, 220, 250);

        var untouched = ImageProcessor.Process(image, new ImageProcessingOptions());
        var explicitDefaults = ImageProcessor.Process(image, new ImageProcessingOptions
        {
            Gamma = 1, Exposure = 0, Brightness = 0, Contrast = 0,
            Highlights = 0, Shadows = 0, BlackPoint = 0, WhitePoint = 255,
            NoiseReduction = 0, Sharpen = 0, EdgeEnhance = 0,
        });

        Assert.Equal(untouched.PowerFraction, explicitDefaults.PowerFraction);
        // Nothing set means the power is still the plain inverse of luminance.
        Assert.Equal(1.0 - 10 / 255.0, untouched.At(0, 0), 9);
    }

    [Theory]
    [InlineData(0.5, true)]   // below 1 darkens, so more power
    [InlineData(2.0, false)]  // above 1 lightens, so less power
    public void GammaBendsTheMidtonesInTheRightDirection(double gamma, bool expectsMorePower)
    {
        var image = MakeImage(1, 1, 128);

        var plain = ImageProcessor.Process(image, new ImageProcessingOptions()).At(0, 0);
        var bent = ImageProcessor.Process(image, new ImageProcessingOptions { Gamma = gamma }).At(0, 0);

        Assert.Equal(expectsMorePower, bent > plain);
    }

    [Fact]
    public void HighlightsAndShadowsEachTouchOnlyTheirHalfOfTheRange()
    {
        var image = MakeImage(2, 1, 40, 220);

        var lifted = ImageProcessor.Process(image, new ImageProcessingOptions { Shadows = 60 });
        var pulled = ImageProcessor.Process(image, new ImageProcessingOptions { Highlights = -60 });
        var plain = ImageProcessor.Process(image, new ImageProcessingOptions());

        // Shadows moved the dark pixel and left the bright one alone.
        Assert.NotEqual(plain.At(0, 0), lifted.At(0, 0), 6);
        Assert.Equal(plain.At(1, 0), lifted.At(1, 0), 9);

        // Highlights, the other way round.
        Assert.Equal(plain.At(0, 0), pulled.At(0, 0), 9);
        Assert.NotEqual(plain.At(1, 0), pulled.At(1, 0), 6);
    }

    [Fact]
    public void LevelsRemapStretchesTheChosenRangeToFullBlackAndWhite()
    {
        var image = MakeImage(3, 1, 50, 100, 150);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions { BlackPoint = 50, WhitePoint = 150 });

        Assert.Equal(1.0, result.At(0, 0), 6);  // 50 becomes black, so full power
        Assert.Equal(0.5, result.At(1, 0), 6);  // 100 lands in the middle
        Assert.Equal(0.0, result.At(2, 0), 6);  // 150 becomes white, so none
    }

    [Fact]
    public void SpatialFiltersLeaveTheOnePixelBorderAlone()
    {
        // A single dark pixel in the middle of white. A blur that wrapped or clamped at the edges would
        // put a visible frame around every engraved photo, so the border is copied through instead.
        var image = MakeImage(3, 3, 255, 255, 255, 255, 0, 255, 255, 255, 255);

        var blurred = ImageProcessor.Process(image, new ImageProcessingOptions { NoiseReduction = 100 });

        Assert.Equal(0.0, blurred.At(0, 0), 9);
        Assert.Equal(0.0, blurred.At(2, 2), 9);
        // The centre took the average of its 3x3 neighbourhood: eight whites and itself.
        Assert.Equal(1.0 - 255 * 8 / 9.0 / 255.0, blurred.At(1, 1), 6);
    }

    [Fact]
    public void SharpenAndEdgeEnhancePushAnEdgeApartRatherThanSmoothingIt()
    {
        var image = MakeImage(3, 3, 255, 255, 255, 255, 128, 255, 255, 255, 255);
        var plain = ImageProcessor.Process(image, new ImageProcessingOptions()).At(1, 1);

        var sharpened = ImageProcessor.Process(image, new ImageProcessingOptions { Sharpen = 100 }).At(1, 1);
        var edged = ImageProcessor.Process(image, new ImageProcessingOptions { EdgeEnhance = 100 }).At(1, 1);

        // The centre is darker than its surroundings, so both filters have to darken it further.
        Assert.True(sharpened > plain, $"sharpen did not deepen the centre ({sharpened} vs {plain})");
        Assert.True(edged > plain, $"edge enhance did not deepen the centre ({edged} vs {plain})");
    }

    [Theory]
    [InlineData(DitheringAlgorithm.Stucki)]
    [InlineData(DitheringAlgorithm.FloydSteinberg)]
    [InlineData(DitheringAlgorithm.Jarvis)]
    [InlineData(DitheringAlgorithm.Atkinson)]
    [InlineData(DitheringAlgorithm.Sierra)]
    [InlineData(DitheringAlgorithm.Ordered)]
    public void EveryDitherAlgorithmProducesOnlyFullOrNoPower(DitheringAlgorithm algorithm)
    {
        var image = MakeImage(6, 4,
            10, 40, 70, 100, 130, 160,
            190, 220, 250, 5, 35, 65,
            95, 125, 155, 185, 215, 245,
            128, 128, 128, 128, 128, 128);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions
        {
            UseDithering = true,
            DitheringAlgorithm = algorithm,
        });

        // A diode laser is a one-bit device: anything between the two is a value the machine cannot
        // produce, so the dither has to resolve every pixel to one or the other.
        Assert.All(result.PowerFraction, value => Assert.True(value is 0.0 or 1.0, $"got {value}"));
        Assert.Contains(1.0, result.PowerFraction);
        Assert.Contains(0.0, result.PowerFraction);
    }

    [Fact]
    public void OrderedDitherCarriesNoErrorSoAFlatToneRepeatsTheBayerMatrix()
    {
        var image = MakeImage(4, 1, 128, 128, 128, 128);

        var result = ImageProcessor.Process(image, new ImageProcessingOptions
        {
            UseDithering = true,
            DitheringAlgorithm = DitheringAlgorithm.Ordered,
        });

        // Row 0 of the 4x4 Bayer matrix is 0, 8, 2, 10, scaled to 0, 127.5, 31.875, 159.375. A tone of
        // 128 burns only where the threshold is above it.
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, result.PowerFraction);
    }

    [Fact]
    public void AtkinsonDiscardsPartOfTheErrorSoItBurnsLessThanStucki()
    {
        // A light flat tone, which is where the claim actually holds. The discarded error is whatever
        // is left over after a pixel is forced to white, and on a light image that leftover is
        // positive - throwing it away keeps the neighbours light instead of accumulating them into
        // more dots. On a dark image the leftover is negative and the same discarding works the other
        // way, so "Atkinson is lighter" is a statement about the highlights, not about every image.
        var image = MakeImage(8, 8, Enumerable.Repeat((byte)200, 64).ToArray());

        var stucki = ImageProcessor.Process(image, new ImageProcessingOptions
        {
            UseDithering = true, DitheringAlgorithm = DitheringAlgorithm.Stucki,
        });
        var atkinson = ImageProcessor.Process(image, new ImageProcessingOptions
        {
            UseDithering = true, DitheringAlgorithm = DitheringAlgorithm.Atkinson,
        });

        Assert.True(atkinson.PowerFraction.Sum() < stucki.PowerFraction.Sum(),
            $"Atkinson burned {atkinson.PowerFraction.Sum()} against Stucki's {stucki.PowerFraction.Sum()}");
    }
}
