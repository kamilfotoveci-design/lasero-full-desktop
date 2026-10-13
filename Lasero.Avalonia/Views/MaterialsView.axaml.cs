using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Layers;

namespace Lasero.Avalonia.Views;

public sealed partial class MaterialsView : UserControl
{
    public MaterialsView() => AvaloniaXamlLoader.Load(this);

    private MaterialCatalogViewModel? Materials => DataContext as MaterialCatalogViewModel;

    /// <summary>Port of Lasero.App's MaterialsWindow.xaml.cs OnApplySelectedRecipeClick — same manual
    /// field assignment (not LayerSettings.ApplyRecipe; WPF's standalone-window path and its
    /// Inspector-flyout path genuinely differ here, and this keeps that exact WPF behavior rather than
    /// "fixing" the inconsistency).</summary>
    private void OnApplySelectedRecipeClick(object? sender, RoutedEventArgs e)
    {
        if (Materials?.SelectedRecipe is not { } recipe) return;
        if (this.FindControl<ComboBox>("TargetLayerComboBox")?.SelectedItem is not LayerSettings layer) return;

        layer.Mode = recipe.Mode;
        layer.Speed = recipe.SpeedMmPerMinute;
        layer.Power = recipe.PowerPercent;
        layer.Passes = recipe.Passes;
        layer.FillLineIntervalMm = recipe.FillLineIntervalMm;
    }
}
