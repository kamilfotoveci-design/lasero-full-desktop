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

    /// <summary>Which content the inspector's single content host shows — the operation editor or the
    /// machine-control panel (previously a separate floating window; see OnMachineControlClick). A
    /// DependencyProperty rather than ViewModel state: which of the two views is on screen is pure
    /// inspector-navigation UI, not domain data, and toggling it never touches Scene/Connection/layer
    /// state — both StackPanels stay alive and bound the whole time, so switching back to Operation
    /// never re-measures or resets anything under it (selection, scroll position, expanded state all
    /// survive because nothing was ever torn down).</summary>
    public static readonly DependencyProperty IsMachineControlModeProperty = DependencyProperty.Register(
        nameof(IsMachineControlMode), typeof(bool), typeof(DesignerInspectorView), new PropertyMetadata(false));

    public bool IsMachineControlMode
    {
        get => (bool)GetValue(IsMachineControlModeProperty);
        set => SetValue(IsMachineControlModeProperty, value);
    }

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
    /// Switches the inspector to the machine-control panel — jogging, homing, unlock, origin, the
    /// positioning beam, the job status card with its time estimate, and the framing and placement
    /// settings, all via the same MachinePanelView/MainViewModel the Device screen uses (one source of
    /// truth; nothing here is a copy). This used to open a separate floating window; it now swaps the
    /// inspector's own content instead, so operating the machine never covers the canvas or loses the
    /// operation editor underneath it.
    /// </summary>
    private void OnMachineControlClick(object sender, RoutedEventArgs e) => IsMachineControlMode = true;

    private void OnMachineControlBackClick(object sender, RoutedEventArgs e) => IsMachineControlMode = false;

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
