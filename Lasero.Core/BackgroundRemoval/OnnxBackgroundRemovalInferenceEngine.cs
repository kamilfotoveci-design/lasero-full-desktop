using System.Runtime.Versioning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Runs isnet-general-use.onnx through ONNX Runtime, accelerated by DirectML when a compatible GPU
/// is present. Microsoft.ML.OnnxRuntime.DirectML bundles the CPU execution provider as well, so when
/// AppendExecutionProvider_DML throws (no DirectML-capable adapter, driver too old, ...) an
/// InferenceSession created with no execution providers appended still runs — just on the CPU. That
/// is exactly the "DirectML with automatic CPU fallback" behaviour the product spec asked for; no
/// separate CPU-only package is needed.
///
/// isnet expects a fixed 1024x1024 RGB input normalized to roughly [-1, 1] and produces a
/// single-channel prediction map at the same resolution. The DIS/rembg post-processing normalizes
/// that map per image using its observed minimum and maximum; applying sigmoid here is incorrect
/// because it leaves background values too opaque on this model.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OnnxBackgroundRemovalInferenceEngine : IBackgroundRemovalInferenceEngine
{
    private const int InputSize = 1024;
    private const float NormalizeMean = 0.5f;
    private const float NormalizeStd = 1.0f;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public OnnxBackgroundRemovalInferenceEngine(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_DML(0);
        }
        catch (Exception)
        {
            // No DirectML-capable adapter/driver on this machine — leave the session on the CPU
            // provider that ships in the DirectML package rather than failing to construct at all.
        }

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public float[] Segment(byte[] rgb, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("Pixel buffer size does not match width*height*3.", nameof(rgb));

        var resized = ResizeBilinearRgb(rgb, width, height, InputSize, InputSize);

        // DIS/rembg scales each decoded image by its own brightest channel before applying the
        // mean/std transform. Using a fixed /255 here makes darker photos look materially different
        // to ISNet and can produce a mask that cuts through the subject or leaves the background.
        var imageMaximum = 0;
        for (var i = 0; i < resized.Length; i++)
            imageMaximum = Math.Max(imageMaximum, resized[i]);
        var imageScale = Math.Max(imageMaximum, 1) / 255f;

        var input = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var offset = (y * InputSize + x) * 3;
                input[0, 0, y, x] = (resized[offset + 0] / 255f / imageScale - NormalizeMean) / NormalizeStd;
                input[0, 1, y, x] = (resized[offset + 1] / 255f / imageScale - NormalizeMean) / NormalizeStd;
                input[0, 2, y, x] = (resized[offset + 2] / 255f / imageScale - NormalizeMean) / NormalizeStd;
            }
        }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);
        var output = results.First().AsTensor<float>();

        var maskAtModelSize = NormalizePrediction(output);

        return ResizeMask(maskAtModelSize, InputSize, InputSize, width, height);
    }

    private static float[] NormalizePrediction(Tensor<float> output)
    {
        var mask = new float[InputSize * InputSize];
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;

        for (var y = 0; y < InputSize; y++)
        for (var x = 0; x < InputSize; x++)
        {
            var value = output[0, 0, y, x];
            mask[y * InputSize + x] = value;
            minimum = MathF.Min(minimum, value);
            maximum = MathF.Max(maximum, value);
        }

        var range = maximum - minimum;
        if (!float.IsFinite(range) || range <= 0.000001f)
            return mask.Select(_ => 0f).ToArray();

        for (var i = 0; i < mask.Length; i++)
            mask[i] = Math.Clamp((mask[i] - minimum) / range, 0f, 1f);

        return mask;
    }

    private static byte[] ResizeBilinearRgb(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var result = new byte[dstWidth * dstHeight * 3];
        var scaleX = srcWidth / (double)dstWidth;
        var scaleY = srcHeight / (double)dstHeight;

        for (var y = 0; y < dstHeight; y++)
        {
            var srcYf = Math.Clamp((y + 0.5) * scaleY - 0.5, 0, srcHeight - 1);
            var y0 = (int)srcYf;
            var y1 = Math.Min(y0 + 1, srcHeight - 1);
            var fy = srcYf - y0;

            for (var x = 0; x < dstWidth; x++)
            {
                var srcXf = Math.Clamp((x + 0.5) * scaleX - 0.5, 0, srcWidth - 1);
                var x0 = (int)srcXf;
                var x1 = Math.Min(x0 + 1, srcWidth - 1);
                var fx = srcXf - x0;

                var dstOffset = (y * dstWidth + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var v00 = source[(y0 * srcWidth + x0) * 3 + channel];
                    var v10 = source[(y0 * srcWidth + x1) * 3 + channel];
                    var v01 = source[(y1 * srcWidth + x0) * 3 + channel];
                    var v11 = source[(y1 * srcWidth + x1) * 3 + channel];
                    var top = v00 + (v10 - v00) * fx;
                    var bottom = v01 + (v11 - v01) * fx;
                    result[dstOffset + channel] = (byte)Math.Round(top + (bottom - top) * fy);
                }
            }
        }

        return result;
    }

    private static float[] ResizeMask(float[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var result = new float[dstWidth * dstHeight];
        var scaleX = srcWidth / (double)dstWidth;
        var scaleY = srcHeight / (double)dstHeight;

        for (var y = 0; y < dstHeight; y++)
        {
            var srcYf = Math.Clamp((y + 0.5) * scaleY - 0.5, 0, srcHeight - 1);
            var y0 = (int)srcYf;
            var y1 = Math.Min(y0 + 1, srcHeight - 1);
            var fy = srcYf - y0;

            for (var x = 0; x < dstWidth; x++)
            {
                var srcXf = Math.Clamp((x + 0.5) * scaleX - 0.5, 0, srcWidth - 1);
                var x0 = (int)srcXf;
                var x1 = Math.Min(x0 + 1, srcWidth - 1);
                var fx = srcXf - x0;

                var v00 = source[y0 * srcWidth + x0];
                var v10 = source[y0 * srcWidth + x1];
                var v01 = source[y1 * srcWidth + x0];
                var v11 = source[y1 * srcWidth + x1];
                var top = v00 + (v10 - v00) * fx;
                var bottom = v01 + (v11 - v01) * fx;
                result[y * dstWidth + x] = (float)(top + (bottom - top) * fy);
            }
        }

        return result;
    }

    public void Dispose() => _session.Dispose();
}
