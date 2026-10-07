using System.Reflection;
using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Tests.Perf;

namespace Lasero.Tests;

/// <summary>
/// A dense path must not turn Node Edit into tens of thousands of WPF elements: dots that cannot be
/// told apart are not drawn, while selected, hovered and end nodes always are.
/// </summary>
[Collection("WpfUi")]
public sealed class NodeOverlayDensityTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static int CountNodeTargets(CanvasRigLite rig)
    {
        var visuals = (List<FrameworkElement>)typeof(SceneCanvas).GetField("_selectionVisuals", Private)!.GetValue(rig.Canvas)!;
        return visuals.Count(v => v is Ellipse { Tag: not null } && v.Tag.GetType().Name == "NodeHitTag" && v.Tag.ToString()!.Contains("IsHandle = False"));
    }

    private sealed class CanvasRigLite : IDisposable
    {
        public readonly SceneViewModel Vm = new();
        public readonly SceneCanvas Canvas = new() { Width = 1200, Height = 800, WorkAreaWidthMm = 400, WorkAreaHeightMm = 400 };
        public readonly Window Window;

        public CanvasRigLite()
        {
            Canvas.ViewModel = Vm;
            Window = new Window
            {
                Width = 1200, Height = 800, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                SizeToContent = SizeToContent.WidthAndHeight, Content = Canvas,
            };
            Window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        public void Dispose() => Window.Close();
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(40)]
    public void DenseOverlayIsBoundedAndSparseOverlayShowsEveryNode(int nodes)
    {
        Ui.Invoke(() =>
        {
            using var rig = new CanvasRigLite();
            var obj = VectorPathSceneFactory.Create(
                PerfScenes.BezierPath(nodes, new Position(150, 150, 0), new Random(1), radiusMm: 60), PerfScenes.PathColor, "p");
            rig.Vm.Execute(new Lasero.Core.Scene.Commands.AddObjectCommand(rig.Vm.Scene, obj, []));
            rig.Canvas.UpdateLayout();
            rig.Vm.SelectedObjects.Add(obj);

            rig.Canvas.EnterNodeEditMode(obj);
            rig.Canvas.UpdateLayout();

            var targets = CountNodeTargets(rig);
            if (nodes <= 100)
                Assert.Equal(nodes, targets);
            else
                Assert.InRange(targets, 50, nodes / 3);
        });
    }

    [Fact]
    public void SelectedNodeAlwaysKeepsItsDotEvenWhenNeighboursAreThinnedOut()
    {
        Ui.Invoke(() =>
        {
            using var rig = new CanvasRigLite();
            var obj = VectorPathSceneFactory.Create(
                PerfScenes.BezierPath(3000, new Position(150, 150, 0), new Random(2), radiusMm: 60), PerfScenes.PathColor, "p");
            rig.Vm.Execute(new Lasero.Core.Scene.Commands.AddObjectCommand(rig.Vm.Scene, obj, []));
            rig.Canvas.UpdateLayout();
            rig.Vm.SelectedObjects.Add(obj);
            rig.Canvas.EnterNodeEditMode(obj);

            var keys = (HashSet<(int Subpath, int Node)>)typeof(SceneCanvas).GetField("_selectedNodeKeys", Private)!.GetValue(rig.Canvas)!;
            keys.Add((0, 1501));
            typeof(SceneCanvas).GetMethod("RedrawSelectionOverlay", Private)!.Invoke(rig.Canvas, null);

            var visuals = (List<FrameworkElement>)typeof(SceneCanvas).GetField("_selectionVisuals", Private)!.GetValue(rig.Canvas)!;
            Assert.Contains(visuals, v => v is Ellipse && v.Tag is { } tag && tag.ToString()!.Contains("Node = 1501"));
        });
    }
}
