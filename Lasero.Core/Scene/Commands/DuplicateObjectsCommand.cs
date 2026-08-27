using Lasero.Core.Grbl;

namespace Lasero.Core.Scene.Commands;

/// <summary>Duplicates one or more objects with a cascade offset (so copies don't land exactly on top
/// of their source). Exposes the created clones so the caller can select them after Execute.</summary>
public sealed class DuplicateObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly IReadOnlyList<SceneObject> _sources;
    private readonly Position _cascadeOffset;
    private List<SceneObject> _clones = [];

    public DuplicateObjectsCommand(SceneDocument scene, IReadOnlyList<SceneObject> sources, Position cascadeOffset)
    {
        _scene = scene;
        _sources = sources;
        _cascadeOffset = cascadeOffset;
    }

    public IReadOnlyList<SceneObject> Clones => _clones;

    public void Do()
    {
        _clones = _sources.Select(source =>
        {
            var clone = source.Clone();
            clone.Transform = clone.Transform with
            {
                X = clone.Transform.X + _cascadeOffset.X,
                Y = clone.Transform.Y + _cascadeOffset.Y,
            };
            return clone;
        }).ToList();

        foreach (var clone in _clones)
            _scene.Objects.Add(clone);
    }

    public void Undo()
    {
        foreach (var clone in _clones)
            _scene.Objects.Remove(clone);
    }
}
