namespace Lasero.Core.Raster;

/// <summary>
/// How a continuous-tone image is turned into the black-and-white dots a diode laser can actually
/// produce. The set matches the web app's, so the same photograph prepared in either place comes out
/// the same on the machine.
/// </summary>
public enum DitheringAlgorithm
{
    /// <summary>Best for photographs: smooth gradients, the widest error kernel here. The default.</summary>
    Stucki,

    /// <summary>The classic. Good contrast, cheapest of the error-diffusion kernels.</summary>
    FloydSteinberg,

    /// <summary>Detail close to Stucki with slightly sharper noise.</summary>
    Jarvis,

    /// <summary>Discards part of the error deliberately, which lightens the result. Suits logos and
    /// flat graphics more than photographs.</summary>
    Atkinson,

    /// <summary>Balanced midtones - between Stucki and Floyd-Steinberg.</summary>
    Sierra,

    /// <summary>A fixed 4x4 Bayer matrix rather than diffused error, so flat areas take on a regular
    /// texture instead of noise. No error is carried between pixels.</summary>
    Ordered,
}
