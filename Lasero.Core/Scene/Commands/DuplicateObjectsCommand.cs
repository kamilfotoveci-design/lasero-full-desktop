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
    private int[]? _insertIndices; // final index of each clone after the first Do

    public DuplicateObjectsCommand(SceneDocument scene, IReadOnlyList<SceneObject> sources, Position cascadeOffset)
    {
        _scene = scene;
        _sources = sources;
        _cascadeOffset = cascadeOffset;
    }

    public IReadOnlyList<SceneObject> Clones => _clones;

    public void Do()
    {
        if (_clones.Count == 0)
        {
            var groupCopies = new Dictionary<Guid, Guid>();
            Guid RemapGroup(Guid id)
            {
                if (!groupCopies.TryGetValue(id, out var replacement))
                    groupCopies[id] = replacement = Guid.NewGuid();
                return replacement;
            }
            _clones = _sources.Select(source =>
            {
                var clone = source.Clone();
                if (source.GroupId is { } groupId)
                    clone.GroupId = RemapGroup(groupId);
                clone.NestedGroupIds = source.NestedGroupIds.Select(RemapGroup).ToArray();
                clone.Transform = clone.Transform with
                {
                    X = clone.Transform.X + _cascadeOffset.X,
                    Y = clone.Transform.Y + _cascadeOffset.Y,
                };
                return clone;
            }).ToList();

            var objectCopies = _sources.Select((source, index) => (source.Id, Clone: _clones[index]))
                .ToDictionary(pair => pair.Id, pair => pair.Clone);
            var linkCopies = new Dictionary<Guid, Guid>();
            foreach (var (source, index) in _sources.Select((source, index) => (source, index)))
            {
                if (source.OffsetLink is not { } link ||
                    !link.SourceIds.All(objectCopies.ContainsKey)) continue;
                if (!linkCopies.TryGetValue(link.LinkId, out var newLinkId))
                    linkCopies[link.LinkId] = newLinkId = Guid.NewGuid();
                _clones[index].OffsetLink = link with
                {
                    LinkId = newLinkId,
                    SourceIds = link.SourceIds.Select(id => objectCopies[id].Id).ToArray(),
                };
                // The copied sources already receive the cascade; a linked result derives that
                // movement from them when its cached outline is refreshed after this command.
                _clones[index].Transform = source.Transform;
            }
        }

        if (_insertIndices is not null)
        {
            // Redo: the scene is back in its pre-Do state, so ascending inserts reproduce the exact z-order.
            foreach (var (clone, index) in _clones.Zip(_insertIndices).OrderBy(pair => pair.Second))
                if (!_scene.Objects.Contains(clone)) _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), clone);
            return;
        }

        // Each clone goes directly above its source (later in the list = higher in z-order). A source that is
        // not in the scene (pasted from the clipboard) has nothing to sit above, so its clone is appended.
        for (var i = 0; i < _clones.Count; i++)
        {
            var clone = _clones[i];
            if (_scene.Objects.Contains(clone)) continue;
            var sourceIndex = _scene.Objects.IndexOf(_sources[i]);
            if (sourceIndex < 0) _scene.Objects.Add(clone);
            else _scene.Objects.Insert(sourceIndex + 1, clone);
        }
        _insertIndices = _clones.Select(clone => _scene.Objects.IndexOf(clone)).ToArray();
    }

    public void Undo()
    {
        foreach (var clone in _clones)
            _scene.Objects.Remove(clone);
    }
}
