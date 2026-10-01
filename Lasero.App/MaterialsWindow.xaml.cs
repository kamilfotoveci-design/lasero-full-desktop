using System.Windows;
using System.Collections.Specialized;
using Lasero.Core.Layers;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class MaterialsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NotifyCollectionChangedEventHandler _presetsCollectionChanged;

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

        // A new recipe lands with its name selected, so a beginner can type the material straight away.
        _presetsCollectionChanged = OnPresetsCollectionChanged;
        viewModel.Materials.Presets.CollectionChanged += _presetsCollectionChanged;
        Closed += OnClosed;

        var selectedColors = viewModel.Scene.Selected?.LocalShapes
            .Select(shape => shape.LayerColor)
            .Distinct()
            .ToArray() ?? [];
        TargetLayerComboBox.SelectedItem = selectedColors.Length == 1
            ? viewModel.Scene.Layers.FirstOrDefault(layer => layer.Color.IsApproximately(selectedColors[0]))
            : viewModel.Scene.Layers.FirstOrDefault();
    }

    public IEnumerable<LayerSettings> ProjectLayers => _viewModel.Scene.Layers;

    private void OnPresetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) FocusLastRecipeName();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.Materials.Presets.CollectionChanged -= _presetsCollectionChanged;

    private void FocusLastRecipeName()
    {
        if (RecipeList.ItemContainerGenerator.ContainerFromIndex(RecipeList.Items.Count - 1) is not FrameworkElement row) return;
        row.BringIntoView();
        if (FindFirstTextBox(row) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private static System.Windows.Controls.TextBox? FindFirstTextBox(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.TextBox box) return box;
            if (FindFirstTextBox(child) is { } nested) return nested;
        }
        return null;
    }

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
