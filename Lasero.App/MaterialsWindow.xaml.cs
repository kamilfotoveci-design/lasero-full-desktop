using System.Windows;
using Lasero.Core.Layers;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class MaterialsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MaterialsWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel.Materials;

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, Math.Max(720, workArea.Width - 32));
            MinHeight = Math.Min(MinHeight, Math.Max(500, workArea.Height - 32));
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            Width = Math.Min(Width, workArea.Width - 16);
            Height = Math.Min(Height, workArea.Height - 16);
            SwatchScrollViewer.ScrollToTop();
        };

        var selectedColors = viewModel.Scene.Selected?.LocalShapes
            .Select(shape => shape.LayerColor)
            .Distinct()
            .ToArray() ?? [];
        TargetLayerComboBox.SelectedItem = selectedColors.Length == 1
            ? viewModel.Scene.Layers.FirstOrDefault(layer => layer.Color.IsApproximately(selectedColors[0]))
            : viewModel.Scene.Layers.FirstOrDefault();
    }

    public IEnumerable<LayerSettings> ProjectLayers => _viewModel.Scene.Layers;

    private void OnApplySelectedRecipeClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Materials.SelectedRecipe is not { } recipe ||
            TargetLayerComboBox.SelectedItem is not LayerSettings layer)
        {
            return;
        }

        layer.Mode = recipe.Mode;
        layer.Speed = recipe.SpeedMmPerMinute;
        layer.Power = recipe.PowerPercent;
        layer.Passes = recipe.Passes;
        layer.FillLineIntervalMm = recipe.FillLineIntervalMm;
        _viewModel.MaterialName = recipe.MaterialName;
        _viewModel.GCode.LastMessage = $"Parametry pro {recipe.MaterialName} byly použity na vrstvu {layer.Name}.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
