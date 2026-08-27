namespace Lasero.Core.Scene.Commands;

/// <summary>Groups several commands into one undo step — e.g. dragging a multi-object selection produces
/// one TransformObjectCommand per object, but a single Ctrl+Z should undo the whole drag at once.</summary>
public sealed class CompositeSceneCommand : ISceneCommand
{
    private readonly IReadOnlyList<ISceneCommand> _commands;

    public CompositeSceneCommand(IReadOnlyList<ISceneCommand> commands)
    {
        _commands = commands;
    }

    public void Do()
    {
        foreach (var command in _commands)
            command.Do();
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }
}
