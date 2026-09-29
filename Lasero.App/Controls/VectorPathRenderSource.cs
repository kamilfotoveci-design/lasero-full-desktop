using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>
/// Which curve SceneCanvas draws for a vector-path object.
///
/// Normally that is the object's own <see cref="SceneObject.VectorPath"/>. During a node, handle or
/// segment drag the object is deliberately not replaced until mouse-up (one drag is one undo step),
/// so its VectorPath is still the pre-drag curve while the edit lives in the canvas's working path.
/// Drawing the object's own path then leaves the outline where the drag started and only the node
/// and handle dots move: the dragged node floats off the curve and the shape jumps on release.
/// </summary>
internal static class VectorPathRenderSource
{
    /// <summary>The curve to draw for <paramref name="obj"/>, or null when it has no vector model
    /// (rectangles, imports and the like are drawn from their flattened shapes).</summary>
    public static VectorPath? For(SceneObject obj, SceneObject? nodeEditObject, VectorPath? workingPath, bool dragInFlight)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj.VectorPath is null) return null;

        return dragInFlight && workingPath is not null && ReferenceEquals(obj, nodeEditObject)
            ? workingPath
            : obj.VectorPath;
    }
}
