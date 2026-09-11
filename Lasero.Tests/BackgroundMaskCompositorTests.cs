using Lasero.Core.BackgroundRemoval;

namespace Lasero.Tests;

public sealed class BackgroundMaskCompositorTests
{
    [Fact]
    public void ApplyMask_FullMaskLeavesAlphaUnchanged()
    {
        byte[] bgra = [10, 20, 30, 255, 40, 50, 60, 200];
        float[] mask = [1f, 1f];

        var result = BackgroundMaskCompositor.ApplyMask(bgra, width: 2, height: 1, mask);

        Assert.Equal(bgra, result);
    }

    [Fact]
    public void ApplyMask_ZeroMaskMakesPixelFullyTransparentButKeepsColor()
    {
        byte[] bgra = [10, 20, 30, 255];
        float[] mask = [0f];

        var result = BackgroundMaskCompositor.ApplyMask(bgra, width: 1, height: 1, mask);

        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
        Assert.Equal(0, result[3]);
    }

    [Fact]
    public void ApplyMask_PartialMaskScalesExistingAlphaProportionally()
    {
        byte[] bgra = [0, 0, 0, 200];
        float[] mask = [0.5f];

        var result = BackgroundMaskCompositor.ApplyMask(bgra, width: 1, height: 1, mask);

        Assert.Equal(100, result[3]);
    }

    [Fact]
    public void ApplyMask_ClampsMaskValuesOutsideZeroToOneRange()
    {
        byte[] bgra = [0, 0, 0, 200];

        var overResult = BackgroundMaskCompositor.ApplyMask(bgra, 1, 1, [2f]);
        var underResult = BackgroundMaskCompositor.ApplyMask(bgra, 1, 1, [-1f]);

        Assert.Equal(200, overResult[3]);
        Assert.Equal(0, underResult[3]);
    }

    [Fact]
    public void ApplyMask_DoesNotMutateTheSourceBuffer()
    {
        byte[] bgra = [0, 0, 0, 200];
        var original = (byte[])bgra.Clone();

        BackgroundMaskCompositor.ApplyMask(bgra, 1, 1, [0f]);

        Assert.Equal(original, bgra);
    }

    [Fact]
    public void ApplyMask_ThrowsWhenPixelBufferSizeDoesNotMatchWidthHeight()
    {
        Assert.Throws<ArgumentException>(() => BackgroundMaskCompositor.ApplyMask([0, 0, 0, 0], 2, 1, [1f, 1f]));
    }

    [Fact]
    public void ApplyMask_ThrowsWhenMaskSizeDoesNotMatchWidthHeight()
    {
        Assert.Throws<ArgumentException>(() => BackgroundMaskCompositor.ApplyMask([0, 0, 0, 0], 1, 1, [1f, 1f]));
    }
}
