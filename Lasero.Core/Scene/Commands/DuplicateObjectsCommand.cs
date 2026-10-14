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
            // A whole group duplicates into a new group. A member duplicated on its own from inside its
            // group (the operator entered the group) stays in that group, like a copy made inside an
            // Inkscape group; a clipboard source is never a live member, so it always gets a new group.
            var partialGroups = _sources
                .Where(source => source.GroupId is not null && _scene.Objects.Contains(source))
                .Select(source => source.GroupId!.Value)
                .Distinct()
                .Where(id => _scene.Objects.Any(item => item.GroupId == id && !_sources.Contains(item)))
                .ToHashSet();
            _clones = _sources.Select(source =>
            {
                var clone = source.Clone();
                if (source.GroupId is { } groupId && !partialGroups.Contains(groupId))
                    clone.GroupId = RemapGroup(groupId);
                clone.NestedGroupIds = source.GroupId is { } kept && partialGroups.Contains(kept)
                    ? source.NestedGroupIds.ToArray()
                    : source.NestedGroupIds.Select(RemapGroup).ToArray();
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

        // A single object's clone goes directly above its source (later in the list = higher in z-order).
        // The clones of a duplicated group go together, in their sources' stacking order, directly above
        // the group's topmost member, so the copy of A,B,C is a contiguous A',B',C' over the original
        // rather than A,A',B,B',C,C' threaded through it. A source that is not in the scene (pasted from
        // the clipboard) has nothing to sit above, so its clone is appended.
        var blocks = Enumerable.Range(0, _clones.Count)
            .Where(i => !_scene.Objects.Contains(_clones[i]))
            .GroupBy(i => _scene.Objects.Contains(_sources[i]) && _clones[i].GroupId is { } copyGroup
                ? (object)copyGroup
                : i)
            .Select(block => block.OrderBy(i => _scene.Objects.IndexOf(_sources[i])).ToList())
            .ToList();
        foreach (var block in blocks)
        {
            var anchorIndex = block.Max(i => _scene.Objects.IndexOf(_sources[i]));
            if (anchorIndex < 0)
            {
                foreach (var i in block) _scene.Objects.Add(_clones[i]);
                continue;
            }
            var insertAt = anchorIndex + 1;
            foreach (var i in block) _scene.Objects.Insert(insertAt++, _clones[i]);
        }
        _insertIndices = _clones.Select(clone => _scene.Objects.IndexOf(clone)).ToArray();
    }

    public void Undo()
    {
        foreach (var clone in _clones)
            _scene.Objects.Remove(clone);
    }
}
