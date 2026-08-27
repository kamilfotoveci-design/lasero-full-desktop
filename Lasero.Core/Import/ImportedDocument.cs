using Lasero.Core.GCode;
using Lasero.Core.Layers;

namespace Lasero.Core.Import;

/// <summary>The result of importing an SVG or raster file — shapes/layers, not yet turned into G-code (see ToolpathBuilder).</summary>
public sealed class ImportedDocument
{
    public required IReadOnlyList<ImportedShape> Shapes { get; init; }
    public required IReadOnlyList<LayerSettings> Layers { get; init; }
    public required BoundingBox2D BoundingBox { get; init; }
    public string? SourceFileName { get; init; }
}
