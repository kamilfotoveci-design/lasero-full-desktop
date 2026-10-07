using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

/// <summary>
/// Editing a layer's power or speed changes the job, not the picture. The canvas must not be told to
/// refresh every object for it, while job generation still hears about the change, and edits that do
/// change the picture (colour, mode, visibility) still refresh everything.
/// </summary>
public sealed class LayerEditNotificationTests
{
    private static (SceneViewModel Vm, SceneObject Obj, LayerSettings Layer) Scene()
    {
        var vm = new SceneViewModel();
        var color = new RgbColor(255, 0, 0);
        var layer = LayerSettings.CreateDefault(color, LayerMode.Cut, "Rez");
        var obj = ScenePrimitiveFactory.CreateRectangle(new Position(0, 0, 0), new Position(10, 10, 0), color, "r");
        vm.Execute(new AddObjectCommand(vm.Scene, obj, [layer]));
        return (vm, obj, vm.Layers.Single());
    }

    [Theory]
    [InlineData(nameof(LayerSettings.Power))]
    [InlineData(nameof(LayerSettings.Speed))]
    [InlineData(nameof(LayerSettings.Passes))]
    [InlineData(nameof(LayerSettings.IsEnabled))]
    public void OutputOnlyEditsKeepJobGenerationInformedButDoNotInvalidateObjectAppearance(string property)
    {
        var (vm, obj, layer) = Scene();
        var appearanceRefreshes = 0;
        obj.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneObject.LocalShapes)) appearanceRefreshes++; };
        var changed = 0;
        var everyNotificationSkippedTheCanvas = true;
        // One edit raises several property notifications (Power, ProcessingSummary, the forgotten
        // material label), each of which reaches Changed; none of them may reach the canvas.
        vm.Changed += () => { changed++; everyNotificationSkippedTheCanvas &= !vm.ChangedAffectsCanvas; };

        switch (property)
        {
            case nameof(LayerSettings.Power): layer.Power = 33; break;
            case nameof(LayerSettings.Speed): layer.Speed = 1234; break;
            case nameof(LayerSettings.Passes): layer.Passes = 3; break;
            default: layer.IsEnabled = !layer.IsEnabled; break;
        }

        Assert.True(changed >= 1, "job generation must still be told");
        Assert.True(everyNotificationSkippedTheCanvas);
        Assert.Equal(0, appearanceRefreshes);
        Assert.True(vm.ChangedAffectsCanvas, "the flag must reset so later changes are never skipped");
    }

    [Theory]
    [InlineData(nameof(LayerSettings.Mode))]
    [InlineData(nameof(LayerSettings.IsVisible))]
    [InlineData(nameof(LayerSettings.Color))]
    public void AppearanceEditsStillRefreshEveryObject(string property)
    {
        var (vm, obj, layer) = Scene();
        var appearanceRefreshes = 0;
        obj.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneObject.LocalShapes)) appearanceRefreshes++; };
        bool? affectedCanvas = null;
        vm.Changed += () => affectedCanvas = vm.ChangedAffectsCanvas;

        switch (property)
        {
            case nameof(LayerSettings.Mode): layer.Mode = LayerMode.Fill; break;
            case nameof(LayerSettings.IsVisible): layer.IsVisible = false; break;
            default: layer.Color = new RgbColor(0, 0, 255); break;
        }

        Assert.True(affectedCanvas);
        Assert.Equal(1, appearanceRefreshes);
    }
}
