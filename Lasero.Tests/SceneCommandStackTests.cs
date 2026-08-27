using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

public class SceneCommandStackTests
{
    private static SceneObject MakeObject(RgbColor color, string name = "Obj")
    {
        var shape = new ImportedShape
        {
            Points = new[] { new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0) },
            IsClosed = false,
            LayerColor = color,
            PreferredMode = LayerMode.Cut,
        };
        return new SceneObject
        {
            Name = name,
            LocalShapes = [shape],
            LocalPivot = new Position(5, 5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 10, 10),
        };
    }

    [Fact]
    public void ExecuteRunsDoAndPushesOntoUndoStack()
    {
        var stack = new SceneCommandStack();
        var obj = MakeObject(RgbColor.Red);
        var before = obj.Transform;
        var after = before with { X = 42 };

        stack.Execute(new TransformObjectCommand(obj, before, after));

        Assert.Equal(42, obj.Transform.X);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoThenRedoRestoresState()
    {
        var stack = new SceneCommandStack();
        var obj = MakeObject(RgbColor.Red);
        var before = obj.Transform;
        var after = before with { X = 42 };
        stack.Execute(new TransformObjectCommand(obj, before, after));

        stack.Undo();
        Assert.Equal(before.X, obj.Transform.X);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Equal(42, obj.Transform.X);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void ExecutingAfterUndoClearsRedoHistory()
    {
        var stack = new SceneCommandStack();
        var obj = MakeObject(RgbColor.Red);
        stack.Execute(new TransformObjectCommand(obj, obj.Transform, obj.Transform with { X = 1 }));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(new TransformObjectCommand(obj, obj.Transform, obj.Transform with { X = 2 }));

        Assert.False(stack.CanRedo);
        Assert.Equal(2, obj.Transform.X);
    }

    [Fact]
    public void ChangedEventFiresOnExecuteUndoAndRedo()
    {
        var stack = new SceneCommandStack();
        var obj = MakeObject(RgbColor.Red);
        var fireCount = 0;
        stack.Changed += () => fireCount++;

        stack.Execute(new TransformObjectCommand(obj, obj.Transform, obj.Transform with { X = 1 }));
        stack.Undo();
        stack.Redo();

        Assert.Equal(3, fireCount);
    }

    [Fact]
    public void AddObjectCommandUndoRemovesOnlyTheLayersItAdded()
    {
        var scene = new SceneDocument();
        var existingLayer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Existujúca");
        scene.Layers.Add(existingLayer);

        var stack = new SceneCommandStack();
        var newObject = MakeObject(RgbColor.Red);
        var candidateLayers = new[]
        {
            LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Duplicitná čierna"), // color already present -> skipped
            LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez"),                   // new -> added
        };

        stack.Execute(new AddObjectCommand(scene, newObject, candidateLayers));

        Assert.Contains(newObject, scene.Objects);
        Assert.Equal(2, scene.Layers.Count); // existing black + new red, duplicate black skipped
        var redLayer = Assert.Single(scene.Layers, l => l.Color.IsApproximately(RgbColor.Red));
        Assert.All(newObject.LocalShapes, shape => Assert.Equal(redLayer.Id, shape.LayerId));

        stack.Undo();

        Assert.DoesNotContain(newObject, scene.Objects);
        Assert.Single(scene.Layers); // only the red layer (added by this command) is removed
        Assert.Same(existingLayer, scene.Layers[0]);
    }

    [Fact]
    public void ReplaceObjectsCommandUndoRestoresSourceAndRemovesTraceLayer()
    {
        var scene = new SceneDocument();
        var source = MakeObject(RgbColor.Red, "Bitmapa");
        var traced = MakeObject(RgbColor.Black, "Trasování");
        var traceLayer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Trasovaný vektor");
        scene.Objects.Add(source);

        var stack = new SceneCommandStack();
        stack.Execute(new ReplaceObjectsCommand(scene, [source], [traced], [traceLayer]));

        Assert.Equal([traced], scene.Objects.ToList());
        Assert.Contains(traceLayer, scene.Layers);

        stack.Undo();

        Assert.Equal([source], scene.Objects.ToList());
        Assert.DoesNotContain(traceLayer, scene.Layers);

        stack.Redo();

        Assert.Equal([traced], scene.Objects.ToList());
        Assert.Contains(traceLayer, scene.Layers);
    }

    [Fact]
    public void ReplaceObjectsCommandKeepsReplacementAtTopmostSelectedZPosition()
    {
        var scene = new SceneDocument();
        var selectedBottom = MakeObject(RgbColor.Red, "Selected bottom");
        var unselectedMiddle = MakeObject(RgbColor.Black, "Unselected middle");
        var selectedTop = MakeObject(RgbColor.Red, "Selected top");
        var untouchedTop = MakeObject(RgbColor.Black, "Untouched top");
        var replacement = MakeObject(RgbColor.Red, "Replacement");
        scene.Objects.Add(selectedBottom);
        scene.Objects.Add(unselectedMiddle);
        scene.Objects.Add(selectedTop);
        scene.Objects.Add(untouchedTop);

        var stack = new SceneCommandStack();
        stack.Execute(new ReplaceObjectsCommand(scene, [selectedBottom, selectedTop], [replacement]));

        Assert.Equal([unselectedMiddle, replacement, untouchedTop], scene.Objects.ToList());

        stack.Undo();
        Assert.Equal([selectedBottom, unselectedMiddle, selectedTop, untouchedTop], scene.Objects.ToList());
    }

    [Fact]
    public void DeleteObjectsCommandUndoReinsertsAtOriginalZOrder()
    {
        var scene = new SceneDocument();
        var a = MakeObject(RgbColor.Red, "A");
        var b = MakeObject(RgbColor.Red, "B");
        var c = MakeObject(RgbColor.Red, "C");
        scene.Objects.Add(a);
        scene.Objects.Add(b);
        scene.Objects.Add(c);

        var stack = new SceneCommandStack();
        stack.Execute(new DeleteObjectsCommand(scene, [a, c]));

        Assert.Equal([b], scene.Objects.ToList());

        stack.Undo();

        Assert.Equal([a, b, c], scene.Objects.ToList());
    }

    [Fact]
    public void DuplicateObjectsCommandAppliesCascadeOffsetAndUndoRemovesClonesOnly()
    {
        var scene = new SceneDocument();
        var source = MakeObject(RgbColor.Red);
        source.Transform = source.Transform with { X = 100, Y = 200 };
        scene.Objects.Add(source);

        var stack = new SceneCommandStack();
        var command = new DuplicateObjectsCommand(scene, [source], new Position(10, 15, 0));
        stack.Execute(command);

        Assert.Equal(2, scene.Objects.Count);
        var clone = command.Clones.Single();
        Assert.Equal(110, clone.Transform.X);
        Assert.Equal(215, clone.Transform.Y);
        Assert.NotEqual(source.Id, clone.Id);

        stack.Undo();

        Assert.Equal([source], scene.Objects.ToList());
    }

    [Fact]
    public void CompositeSceneCommandDoesAllAndUndoesInReverseOrder()
    {
        var scene = new SceneDocument();
        var a = MakeObject(RgbColor.Red, "A");
        var b = MakeObject(RgbColor.Red, "B");
        scene.Objects.Add(a);
        scene.Objects.Add(b);

        var moveA = new TransformObjectCommand(a, a.Transform, a.Transform with { X = 10 });
        var moveB = new TransformObjectCommand(b, b.Transform, b.Transform with { X = 20 });
        var stack = new SceneCommandStack();

        stack.Execute(new CompositeSceneCommand([moveA, moveB]));

        Assert.Equal(10, a.Transform.X);
        Assert.Equal(20, b.Transform.X);

        stack.Undo();

        Assert.Equal(0, a.Transform.X);
        Assert.Equal(0, b.Transform.X);
    }

    [Fact]
    public void ReorderObjectCommandBringToFrontAndUndoRestoresOrder()
    {
        var scene = new SceneDocument();
        var a = MakeObject(RgbColor.Red, "A");
        var b = MakeObject(RgbColor.Red, "B");
        var c = MakeObject(RgbColor.Red, "C");
        scene.Objects.Add(a);
        scene.Objects.Add(b);
        scene.Objects.Add(c);

        var stack = new SceneCommandStack();
        stack.Execute(new ReorderObjectCommand(scene, a, scene.Objects.Count - 1)); // bring A to front

        Assert.Equal([b, c, a], scene.Objects.ToList());

        stack.Undo();

        Assert.Equal([a, b, c], scene.Objects.ToList());
    }
}
