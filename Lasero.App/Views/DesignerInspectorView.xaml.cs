using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lasero.App.ViewModels;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.App.Views;

public partial class DesignerInspectorView : UserControl
{
    public DesignerInspectorView()
    {
        InitializeComponent();
    }

    public void ShowMachineTab() => MachineTabRadio.IsChecked = true;

    private void OnTransformFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox field) return;

        // TextBox bindings normally commit only after focus leaves the field. Dimension editing in
        // a desktop design tool is keyboard-driven, so Enter must commit immediately and keep the
        // field ready for another precise value.
        field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        field.SelectAll();
        e.Handled = true;
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

    private void OnApplyMaterialClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            sender is not Button { Tag: LayerSettings layer } button)
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

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
