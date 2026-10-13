using System.Windows;
using System.Collections.Specialized;
using Lasero.Core.Layers;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class MaterialsWindow : Window
{
    public static readonly DependencyProperty SwatchCardWidthProperty = DependencyProperty.Register(
        nameof(SwatchCardWidth), typeof(double), typeof(MaterialsWindow), new PropertyMetadata(360d));

    public double SwatchCardWidth
    {
        get => (double)GetValue(SwatchCardWidthProperty);
        private set => SetValue(SwatchCardWidthProperty, value);
    }

    private readonly MainViewModel _viewModel;
    private readonly NotifyCollectionChangedEventHandler _presetsCollectionChanged;

    /// <summary>The standalone Vzorník (opened from the Materiály screen) has no target and keeps its
    /// "Hotovo" bar. With a <paramref name="pickerTarget"/> the window is the picker for that one
    /// operation: the bottom bar becomes Zrušit + Aplikovat and nothing is written before Aplikovat.</summary>
    public MaterialsWindow(MainViewModel viewModel, LayerSettings? pickerTarget = null)
    {
        _viewModel = viewModel;
        // Set before InitializeComponent: the XAML reads both once, with ElementName bindings.
        if (pickerTarget is not null) Picker = new MaterialPickerSession(viewModel.Materials, pickerTarget);
        InitializeComponent();
        DataContext = viewModel.Materials;
        if (Picker is not null) Picker.Finished += (_, _) => Close();

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, Math.Max(1, workArea.Width - 16));
            MinHeight = Math.Min(MinHeight, Math.Max(1, workArea.Height - 16));
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            Width = Math.Min(Width, workArea.Width - 16);
            Height = Math.Min(Height, workArea.Height - 16);
            Left = Math.Clamp(Left, workArea.Left + 8, workArea.Right - Width - 8);
            Top = Math.Clamp(Top, workArea.Top + 8, workArea.Bottom - Height - 8);
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

    /// <summary>Non-null only in picker mode.</summary>
    public MaterialPickerSession? Picker { get; }

    public bool IsPickerMode => Picker is not null;

    /// <summary>What the empty detail panel says; in picker mode nothing is written by the click itself.</summary>
    public string SelectionHintText => IsPickerMode
        ? "Kliknutím na políčko ve vzorníku se zobrazí rychlost a výkon. Do vrstvy se zapíšou až po stisknutí tlačítka Aplikovat."
        : "Kliknutím na políčko ve vzorníku se rychlost a výkon nastaví automaticky.";

    private void OnSwatchViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewportWidth = e.NewSize.Width;
        if (viewportWidth <= 0) return;

        // Reserve room for the vertical scrollbar and each card's trailing margin. The card
        // count follows the actual pane width, including when the window is narrower than its
        // design size on a smaller monitor.
        var columns = viewportWidth >= 760 ? 2 : 1;
        SwatchCardWidth = Math.Max(180, Math.Min(520, Math.Floor((viewportWidth - 20 - columns * 12) / columns)));
    }

    private void OnPresetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) FocusLastRecipeName();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Materials.Presets.CollectionChanged -= _presetsCollectionChanged;
        Picker?.Dispose();
    }

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
