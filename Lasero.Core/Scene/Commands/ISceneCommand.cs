namespace Lasero.Core.Scene.Commands;

/// <summary>One reversible edit to a SceneDocument — the unit the undo/redo stack operates on.</summary>
public interface ISceneCommand
{
    void Do();
    void Undo();
}
