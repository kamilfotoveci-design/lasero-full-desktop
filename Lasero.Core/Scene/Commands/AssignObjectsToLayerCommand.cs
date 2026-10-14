using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Moves whole objects onto one processing layer as a single undo step. The previous shape
/// lists are captured when the command is built, so Undo restores every object exactly - including
/// objects whose contours used different layers before the assignment. Source layers the move leaves
/// empty leave with it (see EmptiedLayerPruner).</summary>
public sealed class AssignObjectsToLayerCommand : ISceneCommand
{
    private readonly IReadOnlyList<(SceneObject Target, IReadOnlyList<ImportedShape> Before)> _entries;
    private readonly LayerSettings _layer;
    private readonly EmptiedLayerPruner _layers;

    public AssignObjectsToLayerCommand(SceneDocument scene, IEnumerable<SceneObject> targets, LayerSettings layer)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
        _layers = new EmptiedLayerPruner(scene);
        _entries = targets.Select(target => (target, target.LocalShapes)).ToList();
    }

    /// <summary>True when applying the command would change at least one contour.</summary>
    public bool ChangesAnything => _entries.Any(entry => entry.Before.Any(shape =>
        shape.LayerId != _layer.Id || !shape.LayerColor.Equals(_layer.Color)));

    public void Do()
    {
        _layers.CaptureBefore();
        foreach (var (target, _) in _entries) target.AssignToLayer(_layer);
        _layers.PruneAfter();
    }

    public void Undo()
    {
        _layers.Restore();
        foreach (var (target, before) in _entries) target.RestoreLocalShapes(before);
    }
}
