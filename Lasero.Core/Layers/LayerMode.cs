namespace Lasero.Core.Layers;

public enum LayerMode
{
    /// <summary>Trace the shape's outline/path directly — for cutting or line engraving.</summary>
    Cut,

    /// <summary>Scanline-fill the shape's interior — for solid engraving (logos, filled text).</summary>
    Fill,

    /// <summary>Scanline-fill closed geometry first, then trace its outline.</summary>
    FillAndCut,
}
