using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
/// Pan/zoom must not churn visuals (grid, bed, rulers are reused) and a multi-object command must
/// notify the inspector once, not once per object.
/// </summary>
[Collection("WpfUi")]
public sealed class CanvasViewChromeTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static SceneViewModel SceneWith(int count, out List<SceneObject> objects)
    {
        var vm = new SceneViewModel();
        var color = new RgbColor(255, 0, 0);
        objects = [];
        for (var i = 0; i < count; i++)
        {
            var obj = ScenePrimitiveFactory.CreateRectangle(
                new Position(i * 12, 10, 0), new Position(i * 12 + 8, 18, 0), color, $"r{i}");
            vm.Execute(new AddObjectCommand(vm.Scene, obj, [LayerSettings.CreateDefault(color, LayerMode.Cut, "Rez")]));
            objects.Add(obj);
        }

        vm.UndoCommand.NotifyCanExecuteChanged();
        return vm;
    }

    [Fact]
    public void MultiObjectCommandsNotifyTheInspectorOnceForExecuteUndoAndRedo()
    {
        var vm = SceneWith(25, out var objects);
        foreach (var obj in objects) vm.SelectedObjects.Add(obj);
        var xNotifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneViewModel.SelectedX)) xNotifications++; };

        var beforeX = vm.SelectedX;
        vm.Execute(new CompositeSceneCommand(objects
            .Select(o => (ISceneCommand)new TransformObjectCommand(o, o.Transform, o.Transform with { X = o.Transform.X + 5 }))
            .ToList()));
        Assert.Equal(1, xNotifications);
        Assert.Equal(beforeX + 5, vm.SelectedX, 6);

        xNotifications = 0;
        vm.UndoCommand.Execute(null);
        Assert.Equal(1, xNotifications);
        Assert.Equal(beforeX, vm.SelectedX, 6);

        xNotifications = 0;
        vm.RedoCommand.Execute(null);
        Assert.Equal(1, xNotifications);
        Assert.Equal(beforeX + 5, vm.SelectedX, 6);
        Assert.False(vm.IsApplyingCommand);
    }

    [Fact]
    public void ADirectTransformOutsideACommandStillNotifiesImmediately()
    {
        var vm = SceneWith(2, out var objects);
        vm.SelectedObjects.Add(objects[0]);
        var xNotifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneViewModel.SelectedX)) xNotifications++; };

        objects[0].Transform = objects[0].Transform with { X = 9 }; // a live drag preview, not a command

        Assert.Equal(1, xNotifications);
    }

    [Fact]
    public void RepeatedPansAndZoomsReuseTheGridBedAndRulerVisuals()
    {
        Ui.Invoke(() =>
        {
            var vm = SceneWith(5, out _);
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
                var draw = (Canvas)canvas.FindName("DrawCanvas");
                var top = (Canvas)canvas.FindName("TopRuler");
                var left = (Canvas)canvas.FindName("LeftRuler");
                var reposition = typeof(SceneCanvas).GetMethod("RepositionAll", Private)!;
                var offset = typeof(SceneCanvas).GetField("_offsetXMm", Private)!;

                // Bed first, then the grid and axis paths, behind every object.
                Assert.IsType<Rectangle>(draw.Children[0]);
                Assert.IsType<Path>(draw.Children[1]);
                Assert.IsType<Path>(draw.Children[2]);

                var drawBefore = draw.Children.Count;
                for (var i = 0; i < 60; i++)
                {
                    offset.SetValue(canvas, (double)offset.GetValue(canvas)! + (i % 2 == 0 ? 7.3 : -7.3));
                    reposition.Invoke(canvas, null);
                    canvas.UpdateLayout();
                }

                Assert.Equal(drawBefore, draw.Children.Count);
                Assert.InRange(top.Children.Count, 2, 120);
                Assert.InRange(left.Children.Count, 2, 120);
                Assert.IsType<Rectangle>(draw.Children[0]);

                // Zooming changes the tick density; the visuals still do not accumulate.
                for (var i = 0; i < 20; i++) { if (i % 2 == 0) canvas.ZoomIn(); else canvas.ZoomOut(); }
                Assert.InRange(top.Children.Count, 2, 450);
                Assert.Equal(drawBefore, draw.Children.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
