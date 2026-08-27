using Lasero.Core.Grbl;
using Lasero.Core.Layers;

namespace Lasero.Core.Import;

/// <summary>A single flattened shape ready for toolpath generation — already in absolute mm coordinates.</summary>
public sealed record ImportedShape
{
    /// <summary>Identifies contours that together form one compound path (outer contour plus any
    /// holes). Guid.Empty is the legacy value and means all contours within the same SceneObject
    /// belong to one compound path.</summary>
    public Guid GeometrySetId { get; init; }

    /// <summary>Stable processing-layer identity. Guid.Empty is accepted only for legacy/import
    /// geometry and is resolved when the object enters a SceneDocument.</summary>
    public Guid LayerId { get; init; }
    public required IReadOnlyList<Position> Points { get; init; }
    public required bool IsClosed { get; init; }
    public required RgbColor LayerColor { get; init; }
    public required LayerMode PreferredMode { get; init; }
}
