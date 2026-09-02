using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lasero.App.ViewModels;
using Lasero.Core.Layers;

namespace Lasero.App.Views;

public partial class DesignerInspectorView : UserControl
{
    /// <summary>More passes than this is not a setting, it is a typo — and every pass is another run
    /// of the laser over the same path.</summary>
    private const int MaxPasses = 50;

    public DesignerInspectorView()
    {
        InitializeComponent();
    }

    // A right-click has to land on the row it was aimed at before the menu opens: Duplikovat and
    // Odstranit act on Scene.SelectedLayer, and WPF does not select a ListBoxItem on the right button.
    // Without this, right-clicking row three and choosing Odstranit deletes row one.
    private void OnLayerRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not ListBoxItem row) continue;
            row.IsSelected = true;
            return;
        }
    }

    /// <summary>
    /// Opens the manual machine panel beside the canvas. This is the Designer's route to everything
    /// that used to sit behind the inspector's "Stroj" tab — jogging, homing, unlock, origin, the
    /// positioning beam, the job status card with its time estimate, and the framing and placement
    /// settings. Dropping the tab without this left all of it reachable only from the Device screen.
    /// </summary>
    private void OnMachineControlClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenMachineControl();
    }

    private void OnPassesIncrementClick(object sender, RoutedEventArgs e) => StepPasses(sender, +1);

    private void OnPassesDecrementClick(object sender, RoutedEventArgs e) => StepPasses(sender, -1);

    /// <summary>The stepper writes the same property the field does, clamped to at least one pass —
    /// a zero-pass operation would be silently skipped by the machine.</summary>
    private static void StepPasses(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: LayerSettings layer }) return;
        layer.Passes = Math.Clamp(layer.Passes + delta, 1, MaxPasses);
    }

    private void OnApplyMaterialClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            sender is not FrameworkElement { Tag: LayerSettings layer } source)
        {
            return;
        }

        var menu = new ContextMenu();
        var recommended = new MenuItem
        {
            Header = $"Doporučené · {viewModel.Materials.ActiveProfileLabel}"
        };
        foreach (var recipe in viewModel.Materials.RecipesFor(layer.Mode))
        {
            var item = new MenuItem
            {
                Header = $"{recipe.MaterialName} · {recipe.SpeedMmPerMinute:0} mm/min · {recipe.PowerPercent:0} %"
            };
            item.Click += (_, _) => layer.ApplyRecipe(recipe.Mode, recipe.SpeedMmPerMinute,
                recipe.PowerPercent, recipe.Passes, recipe.FillLineIntervalMm, recipe.MaterialName);
            recommended.Items.Add(item);
        }
        menu.Items.Add(recommended);

        var personal = new MenuItem { Header = "Moje recepty", IsEnabled = viewModel.Materials.Presets.Count > 0 };
        foreach (var preset in viewModel.Materials.Presets.Where(preset => preset.Mode == layer.Mode))
        {
            var item = new MenuItem
            {
                Header = $"{preset.Name} · {preset.Speed:0} mm/min · {preset.Power:0} %"
            };
            item.Click += (_, _) => layer.ApplyRecipe(preset.Mode, preset.Speed, preset.Power,
                preset.Passes, preset.FillLineIntervalMm, preset.Name);
            personal.Items.Add(item);
        }
        menu.Items.Add(personal);

        menu.PlacementTarget = source;
        menu.IsOpen = true;
    }
}
