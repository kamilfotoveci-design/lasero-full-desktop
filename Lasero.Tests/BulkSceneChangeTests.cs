using System.Collections.Specialized;
using System.Reflection;
using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

/// <summary>
/// Changes that touch many objects (select all, a marquee, replacing a bitmap with hundreds of traced
/// objects) must announce themselves once, not once per object.
/// </summary>
[Collection("WpfUi")]
public sealed class BulkSceneChangeTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly RgbColor Red = new(255, 0, 0);

    private static SceneObject Rect(int i) =>
        ScenePrimitiveFactory.CreateRectangle(new Position(i * 3 % 300, i * 7 % 300, 0), new Position(i * 3 % 300 + 2, i * 7 % 300 + 2, 0), Red, $"r{i}");

    [Fact]
    public void ReplaceWithAnnouncesOnceAndKeepsOrderAndIdentity()
    {
        var selection = new SelectionCollection();
        var objects = Enumerable.Range(0, 300).Select(Rect).ToList();
        var events = new List<NotifyCollectionChangedAction>();
        selection.CollectionChanged += (_, e) => events.Add(e.Action);

        selection.ReplaceWith(objects);

        Assert.Single(events);
        Assert.Equal(300, selection.Count);
        Assert.True(objects.SequenceEqual(selection));

        events.Clear();
        selection.ReplaceWith(objects); // unchanged: no notification at all
        Assert.Empty(events);

        selection.ReplaceWith(objects.Take(3));
        Assert.Single(events);
        Assert.Equal([objects[0], objects[1], objects[2]], selection.ToArray());
    }

    [Fact]
    public void SelectAllLeavesTheSameStateAsSelectingEachObjectButNotifiesOnce()
    {
        var vm = new SceneViewModel();
        for (var i = 0; i < 40; i++)
            vm.Execute(new AddObjectCommand(vm.Scene, Rect(i), [LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez")]));
        var changes = 0;
        vm.SelectedObjects.CollectionChanged += (_, _) => changes++;

        vm.SelectAllCommand.Execute(null);

        Assert.Equal(1, changes);
        Assert.Equal(40, vm.SelectionCount);
        Assert.True(vm.HasMultipleSelection);
        Assert.Null(vm.Selected);
        Assert.True(vm.Objects.SequenceEqual(vm.SelectedObjects));
    }

    [Fact]
    public void ReplacingOneObjectWithManyRebuildsTheCanvasOnceAndUndoRestoresTheOriginal()
    {
        Ui.Invoke(() =>
        {
            var vm = new SceneViewModel();
            var layer = LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez");
            var source = Rect(0);
            vm.Execute(new AddObjectCommand(vm.Scene, source, [layer]));
            var canvas = new SceneCanvas { Width = 900, Height = 600, ViewModel = vm };
            var window = new Window
            {
                Width = 900, Height = 600, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                SizeToContent = SizeToContent.WidthAndHeight, Content = canvas,
            };
            window.Show();
            try
            {
                var visuals = (Dictionary<SceneObject, List<Path>>)typeof(SceneCanvas)
                    .GetField("_objectVisuals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
                var replacements = Enumerable.Range(1, 200).Select(Rect).ToList();

                var before = canvas.RebuildAllCount;
                vm.Execute(new ReplaceObjectsCommand(vm.Scene, [source], replacements));
                Assert.Equal(1, canvas.RebuildAllCount - before);
                Assert.Equal(200, visuals.Count);
                Assert.All(replacements, obj => Assert.Contains(obj, visuals.Keys));
                Assert.DoesNotContain(source, visuals.Keys);

                before = canvas.RebuildAllCount;
                vm.UndoCommand.Execute(null);
                Assert.Equal(1, canvas.RebuildAllCount - before);
                Assert.Single(visuals);
                Assert.Contains(source, visuals.Keys);

                vm.RedoCommand.Execute(null);
                Assert.Equal(200, visuals.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
