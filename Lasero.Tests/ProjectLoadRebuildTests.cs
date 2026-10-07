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
/// Opening a project replaces the document object by object. The canvas used to rebuild the entire
/// scene for each one (quadratic in the object count); it must rebuild once, after the load.
/// </summary>
[Collection("WpfUi")]
public sealed class ProjectLoadRebuildTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    [Fact]
    public void LoadingAProjectRebuildsTheCanvasOnceAndShowsEveryObject()
    {
        Ui.Invoke(() =>
        {
            var source = new SceneViewModel();
            var color = new RgbColor(255, 0, 0);
            for (var i = 0; i < 60; i++)
                source.Execute(new AddObjectCommand(source.Scene,
                    ScenePrimitiveFactory.CreateRectangle(new Position(i * 5, i * 3, 0), new Position(i * 5 + 4, i * 3 + 4, 0), color, $"r{i}"),
                    [LayerSettings.CreateDefault(color, LayerMode.Cut, "Rez")]));
            var project = source.CreateProject();

            var target = new SceneViewModel();
            var canvas = new SceneCanvas { Width = 900, Height = 600, ViewModel = target };
            var window = new Window
            {
                Width = 900, Height = 600, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                SizeToContent = SizeToContent.WidthAndHeight, Content = canvas,
            };
            window.Show();
            try
            {
                var before = canvas.RebuildAllCount;
                target.LoadProject(project);
                canvas.UpdateLayout();

                Assert.Equal(1, canvas.RebuildAllCount - before);
                var visuals = (Dictionary<SceneObject, List<Path>>)typeof(SceneCanvas)
                    .GetField("_objectVisuals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
                Assert.Equal(60, visuals.Count);
                Assert.All(target.Objects, obj => Assert.Contains(obj, visuals.Keys));
                Assert.False(target.IsLoadingProject);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
