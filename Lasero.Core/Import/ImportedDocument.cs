using Lasero.Core.GCode;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Import;

/// <summary>The result of importing an SVG or raster file — shapes/layers, not yet turned into G-code (see ToolpathBuilder).</summary>
public sealed class ImportedDocument
{
    public required IReadOnlyList<ImportedShape> Shapes { get; init; }
    public required IReadOnlyList<LayerSettings> Layers { get; init; }
    public required BoundingBox2D BoundingBox { get; init; }
    public string? SourceFileName { get; init; }

    /// <summary>Set only when every shape in the document could be faithfully represented as an
    /// editable curve/corner-node path (see SvgImporter's VectorPathValidity) — null means this
    /// import can't safely enter Node Edit mode, and SceneObjectFactory.FromImportedDocument leaves
    /// the resulting SceneObject.VectorPath null too, same as any other non-editable object.</summary>
    public VectorPath? VectorPath { get; init; }
}
