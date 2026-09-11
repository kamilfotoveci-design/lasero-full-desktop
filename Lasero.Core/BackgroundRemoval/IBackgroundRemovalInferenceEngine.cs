namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Isolates the actual model call (ONNX Runtime / DirectML today) behind a seam so
/// BackgroundRemovalService's file caching, download and compositing logic can be unit-tested with a
/// fake implementation, without needing a real model file or a live inference run — see
/// OnnxBackgroundRemovalInferenceEngine for the concrete implementation and Lasero.Tests for the fake.
/// </summary>
public interface IBackgroundRemovalInferenceEngine : IDisposable
{
    /// <summary>Runs segmentation on a decoded RGB image (row-major, one byte per channel, R,G,B per
    /// pixel) and returns a foreground-probability mask (0..1) at the same width/height, row-major,
    /// one value per pixel.</summary>
    float[] Segment(byte[] rgb, int width, int height);
}
