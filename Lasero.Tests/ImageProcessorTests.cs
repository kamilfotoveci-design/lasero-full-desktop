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
}
