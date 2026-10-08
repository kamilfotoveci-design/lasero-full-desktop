using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// Uniformly scaled objects are drawn from local-space geometry through an object matrix; that must put
/// every point exactly where ObjectTransform.Apply puts it, and moving or rotating such an object must
/// not rebuild its geometry. Objects with unequal X/Y scale keep world-space geometry.
/// </summary>
[Collection("WpfUi")]
public sealed class ObjectMatrixGeometryTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly RgbColor Red = new(255, 0, 0);

    [Fact]
    public void LocalToWorldMatrixMatchesObjectTransformApplyForRandomTransforms()
    {
        var random = new Random(77);
        for (var i = 0; i < 2000; i++)
        {
            var scale = (random.Next(4) == 0 ? -1 : 1) * (0.05 + random.NextDouble() * 4);
            var transform = new ObjectTransform(
                random.NextDouble() * 400 - 200, random.NextDouble() * 400 - 200, random.NextDouble() * 720 - 360,
                scale, random.Next(5) == 0 ? -scale : scale);
            var pivot = new Position(random.NextDouble() * 100, random.NextDouble() * 100, 0);
            var local = new Position(random.NextDouble() * 200 - 50, random.NextDouble() * 200 - 50, 0);

            var expected = transform.Apply(local, pivot);
            var actual = SceneCanvas.LocalToWorld(transform, pivot).Transform(new Point(local.X, local.Y));

            Assert.Equal(expected.X, actual.X, 7);
            Assert.Equal(expected.Y, actual.Y, 7);
        }
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, -2, true)]
    [InlineData(-0.5, -0.5, true)]
    [InlineData(1, 1.5, false)]
    [InlineData(3, 1, false)]
    public void OnlyEqualMagnitudeScalesAreTreatedAsUniform(double sx, double sy, bool uniform) =>
        Assert.Equal(uniform, SceneCanvas.HasUniformScale(new ObjectTransform(0, 0, 0, sx, sy)));

    private static (Window Window, SceneCanvas Canvas, SceneViewModel Vm, SceneObject Obj) Build(ObjectTransform transform)
    {
        var vm = new SceneViewModel();
        var obj = ScenePrimitiveFactory.CreateEllipse(new Position(20, 30, 0), new Position(80, 70, 0), Red, "e");
        obj.Transform = transform;
        vm.Execute(new AddObjectCommand(vm.Scene, obj, [LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez")]));
        var canvas = new SceneCanvas { Width = 900, Height = 600, WorkAreaWidthMm = 300, WorkAreaHeightMm = 300, ViewModel = vm };
        var window = new Window
        {
            Width = 900, Height = 600, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            SizeToContent = SizeToContent.WidthAndHeight, Content = canvas,
        };
        window.Show();
        canvas.UpdateLayout();
        return (window, canvas, vm, obj);
    }

    private static Path PathOf(SceneCanvas canvas, SceneObject obj) =>
        ((Dictionary<SceneObject, List<Path>>)typeof(SceneCanvas).GetField("_objectVisuals", Private)!.GetValue(canvas)!)[obj][0];

    private static Point ToCanvas(SceneCanvas canvas, Position world) => new(
        (double)typeof(SceneCanvas).GetMethod("ToCanvasX", Private)!.Invoke(canvas, [world.X])!,
        (double)typeof(SceneCanvas).GetMethod("ToCanvasY", Private)!.Invoke(canvas, [world.Y])!);

    private static void AssertScreenMapping(SceneCanvas canvas, SceneObject obj)
    {
        var path = PathOf(canvas, obj);
        var combined = path.RenderTransform.Value;
        foreach (var local in obj.LocalShapes[0].Points.Take(12))
        {
            // Local-space geometry carries the object matrix in its RenderTransform; world-space geometry
            // does not, so feed each the coordinates its Data is in.
            var inData = (path.Data as StreamGeometry)!.Bounds;
            Assert.False(inData.IsEmpty);
            var world = obj.Transform.Apply(local, obj.LocalPivot);
            var expected = ToCanvas(canvas, world);
            var geometryPoint = HasLocalGeometry(canvas, obj) ? new Point(local.X, local.Y) : new Point(world.X, world.Y);
            var actual = combined.Transform(geometryPoint);
            Assert.Equal(expected.X, actual.X, 5);
            Assert.Equal(expected.Y, actual.Y, 5);
        }
    }

    private static bool HasLocalGeometry(SceneCanvas canvas, SceneObject obj)
    {
        var cache = typeof(SceneCanvas).GetField("_geometryCache", Private)!.GetValue(canvas)!;
        var entry = cache.GetType().GetProperty("Item")!.GetValue(cache, [obj])!;
        return (bool)entry.GetType().GetProperty("LocalSpace")!.GetValue(entry)!;
    }

    [Fact]
    public void MovingRotatingAndResizingAUniformObjectKeepsItsGeometryAndRedrawsAtTheRightPlace()
    {
        Ui.Invoke(() =>
        {
            var (window, canvas, vm, obj) = Build(new ObjectTransform(10, 5, 0, 1, 1));
            try
            {
                var geometry = PathOf(canvas, obj).Data;
                Assert.True(HasLocalGeometry(canvas, obj));
                AssertScreenMapping(canvas, obj);

                foreach (var next in new[]
                         {
                             new ObjectTransform(60, -20, 0, 1, 1),
                             new ObjectTransform(60, -20, 37, 1, 1),
                             new ObjectTransform(-15, 40, 123, 2.5, 2.5),
                             new ObjectTransform(0, 0, 200, -1.5, -1.5),
                             new ObjectTransform(5, 5, 45, 1.2, -1.2),
                         })
                {
                    vm.Execute(new TransformObjectCommand(obj, obj.Transform, next));
                    canvas.UpdateLayout();
                    Assert.Same(geometry, PathOf(canvas, obj).Data);
                    AssertScreenMapping(canvas, obj);
                }

                // Undo restores the earlier placement through the same cached geometry.
                vm.UndoCommand.Execute(null);
                canvas.UpdateLayout();
                Assert.Same(geometry, PathOf(canvas, obj).Data);
                AssertScreenMapping(canvas, obj);

                // Pan and zoom keep it correct too.
                canvas.ZoomIn();
                canvas.UpdateLayout();
                AssertScreenMapping(canvas, obj);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StrokeStaysOnePointFourPixelsWhateverTheObjectScale()
    {
        Ui.Invoke(() =>
        {
            var (window, canvas, vm, obj) = Build(new ObjectTransform(0, 0, 30, 3, 3));
            try
            {
                var path = PathOf(canvas, obj);
                var scale = (double)typeof(SceneCanvas).GetField("_scale", Private)!.GetValue(canvas)!;
                Assert.Equal(1.4, path.StrokeThickness * scale * 3, 6);

                vm.Execute(new TransformObjectCommand(obj, obj.Transform, obj.Transform with { ScaleX = 0.5, ScaleY = 0.5 }));
                canvas.UpdateLayout();
                Assert.Equal(1.4, PathOf(canvas, obj).StrokeThickness * scale * 0.5, 6);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANonUniformObjectKeepsWorldSpaceGeometryAndRebuildsItWhenItsTransformChanges()
    {
        Ui.Invoke(() =>
        {
            var (window, canvas, vm, obj) = Build(new ObjectTransform(10, 5, 20, 2, 1));
            try
            {
                var first = PathOf(canvas, obj).Data;
                Assert.False(HasLocalGeometry(canvas, obj));
                AssertScreenMapping(canvas, obj);

                vm.Execute(new TransformObjectCommand(obj, obj.Transform, obj.Transform with { X = 70, ScaleY = 1.5 }));
                canvas.UpdateLayout();

                Assert.NotSame(first, PathOf(canvas, obj).Data);
                AssertScreenMapping(canvas, obj);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2.5, 2.5, 30)]
    [InlineData(2, 1, 0)]
    public void ObjectsAreActuallyDrawnWhereTheirOutlineSaysTheyAre(double sx, double sy, double rotation)
    {
        Ui.Invoke(() =>
        {
            var vm = new SceneViewModel();
            var obj = ScenePrimitiveFactory.CreateRectangle(new Position(100, 100, 0), new Position(160, 130, 0), Red, "r");
            obj.Transform = new ObjectTransform(-20, 10, rotation, sx, sy);
            vm.Execute(new AddObjectCommand(vm.Scene, obj, [LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez")]));
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
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 700, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                var stride = 1000 * 4;
                var pixels = new byte[stride * 700];
                bitmap.CopyPixels(pixels, stride, 0);

                bool Red_(int x, int y)
                {
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var cx = x + dx; var cy = y + dy;
                            if (cx < 0 || cy < 0 || cx >= 1000 || cy >= 700) continue;
                            var i = cy * stride + cx * 4;
                            if (pixels[i + 2] > 200 && pixels[i + 1] < 80 && pixels[i] < 80) return true;
                        }
                    return false;
                }

                // The four corners of the outline, mapped by the object's own transform, must carry ink.
                foreach (var corner in obj.LocalShapes[0].Points.Take(4))
                {
                    var screen = ToCanvas(canvas, obj.Transform.Apply(corner, obj.LocalPivot));
                    Assert.True(Red_((int)Math.Round(screen.X + 26), (int)Math.Round(screen.Y + 20)), $"no ink at corner {screen}");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
