using System.Reflection;
using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Lasero.Tests.Perf;

namespace Lasero.Tests;

/// <summary>
/// A live node-drag frame is drawn from the working path and no longer re-flattens LocalShapes. The
/// outline must still follow the node, the pre-drag shapes must stay untouched, and the single commit
/// must produce the same flattened geometry as editing the path directly, with one undo step.
/// </summary>
[Collection("WpfUi")]
public sealed class NodeDragLiveFrameTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void DragFramesMoveTheOutlineLeaveLocalShapesAloneAndCommitOnceWithExactGeometry()
    {
        Ui.Invoke(() =>
        {
            var vm = new SceneViewModel();
            var obj = VectorPathSceneFactory.Create(
                PerfScenes.BezierPath(400, new Position(150, 150, 0), new Random(4), radiusMm: 60), PerfScenes.PathColor, "p");
            vm.Execute(new AddObjectCommand(vm.Scene, obj, []));
            var canvas = new SceneCanvas { Width = 1000, Height = 700, WorkAreaWidthMm = 400, WorkAreaHeightMm = 400, ViewModel = vm };
            var window = new Window
            {
                Width = 1000, Height = 700, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                SizeToContent = SizeToContent.WidthAndHeight, Content = canvas,
            };
            window.Show();
            try
            {
                canvas.UpdateLayout();
                vm.SelectedObjects.Add(obj);
                canvas.EnterNodeEditMode(obj);
                T Field<T>(string name) => (T)typeof(SceneCanvas).GetField(name, Private)!.GetValue(canvas)!;
                void Set(string name, object? value) => typeof(SceneCanvas).GetField(name, Private)!.SetValue(canvas, value);

                var originalShapes = obj.LocalShapes;
                var originalPath = obj.VectorPath!;
                var path = Field<Lasero.Core.Scene.VectorPath>("_nodeEditWorkingPath");
                Set("_nodeDragOriginalPath", path);
                Set("_nodeDragSession", new VectorPathDragSession(obj.LocalShapes));
                Set("_dragMode", Enum.Parse(typeof(SceneCanvas).GetNestedType("DragMode", BindingFlags.NonPublic)!, "NodeEdit"));
                var keys = Field<HashSet<(int Subpath, int Node)>>("_selectedNodeKeys");
                keys.Clear();
                keys.Add((0, 100));
                var anchorWorld = obj.Transform.Apply(path.Subpaths[0].Nodes[100].Anchor, obj.LocalPivot);
                Set("_nodeDragStartWorld", anchorWorld);

                var scale = Field<double>("_scale");
                var x0 = 20 + (anchorWorld.X - Field<double>("_offsetXMm")) * scale;
                var y0 = canvas.ActualHeight - 20 - (anchorWorld.Y - Field<double>("_offsetYMm")) * scale;
                var boundsBefore = ((Path)PathOf(canvas, obj)).Data.Bounds;

                var step = typeof(SceneCanvas).GetMethod("ApplyExpensiveDragUpdate", Private)!;
                for (var i = 1; i <= 5; i++) step.Invoke(canvas, [new Point(x0 + 12 * i, y0 - 9 * i)]);

                Assert.Same(originalShapes, obj.LocalShapes);          // nothing re-flattened per frame
                Assert.Same(originalPath, obj.VectorPath);              // nothing committed yet
                Assert.NotEqual(boundsBefore, PathOf(canvas, obj).Data.Bounds); // but the outline followed the node

                typeof(SceneCanvas).GetMethod("FinishNodeEditDrag", Private)!.Invoke(canvas, null);
                Set("_dragMode", Enum.Parse(typeof(SceneCanvas).GetNestedType("DragMode", BindingFlags.NonPublic)!, "None"));

                var committed = vm.Objects.Single();
                Assert.NotSame(obj, committed);
                Assert.Equal(obj.Id, committed.Id);
                // The committed flattening is exactly what flattening the committed path gives.
                var expected = committed.VectorPath!.FlattenAll();
                Assert.Equal(expected.Count, committed.LocalShapes.Count);
                for (var s = 0; s < expected.Count; s++)
                    Assert.True(expected[s].SequenceEqual(committed.LocalShapes[s].Points));
                Assert.NotEqual(originalPath.Subpaths[0].Nodes[100], committed.VectorPath.Subpaths[0].Nodes[100]);

                vm.UndoCommand.Execute(null);
                Assert.Same(obj, vm.Objects.Single());
                Assert.Same(originalShapes, vm.Objects.Single().LocalShapes);
                Assert.True(vm.CanRedo); // the drag was exactly one undoable step
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Path PathOf(SceneCanvas canvas, SceneObject obj) =>
        ((Dictionary<SceneObject, List<Path>>)typeof(SceneCanvas).GetField("_objectVisuals", Private)!.GetValue(canvas)!)[obj][0];
}
