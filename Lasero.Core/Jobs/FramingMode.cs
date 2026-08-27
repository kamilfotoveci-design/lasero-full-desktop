namespace Lasero.Core.Jobs;

public enum FramingMode
{
    /// <summary>Trace all four edges of the bounding box.</summary>
    FullOutline,

    /// <summary>Trace only short crosses at each of the 4 corners — faster to repeat, matches LightBurn's "Frame".</summary>
    CornersOnly,
}
