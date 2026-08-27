using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Adds a newly-imported object to the scene. Also owns any LayerSettings that had to be
/// created as a side effect (new colors not seen before) — Undo removes exactly those, not the whole
/// Layers list, so other objects already sharing an existing layer are unaffected.</summary>
public sealed class AddObjectCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly SceneObject _newObject;
    private readonly IReadOnlyList<LayerSettings> _candidateLayers;
    private IReadOnlyList<LayerSettings> _addedLayers = [];

    public AddObjectCommand(SceneDocument scene, SceneObject newObject, IReadOnlyList<LayerSettings> candidateLayers)
    {
        _scene = scene;
        _newObject = newObject;
        _candidateLayers = candidateLayers;
    }

    public void Do()
    {
        _addedLayers = _scene.EnsureLayers(_candidateLayers, [_newObject]);
        _scene.Objects.Add(_newObject);
    }

    public void Undo()
    {
        _scene.Objects.Remove(_newObject);
        foreach (var layer in _addedLayers)
            _scene.Layers.Remove(layer);
        _addedLayers = [];
    }
}
