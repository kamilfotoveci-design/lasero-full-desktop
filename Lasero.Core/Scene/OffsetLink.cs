using Lasero.Core.Geometry;

namespace Lasero.Core.Scene;

/// <summary>Recipe for a derived offset. The source objects, not the cached outline, own its shape.</summary>
public sealed record OffsetLink
{
    public Guid LinkId { get; init; } = Guid.NewGuid();
    public required IReadOnlyList<Guid> SourceIds { get; init; }
    public required VectorOffsetOptions Options { get; init; }
    public VectorOffsetResultKind Kind { get; init; }
    public bool IncludeInOutput { get; init; } = true;
    public bool IsVisible { get; init; } = true;
    public bool IsSuspended { get; init; }
}
