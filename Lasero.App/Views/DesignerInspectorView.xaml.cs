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
        nameof(IsMachineControlMode), typeof(bool), typeof(DesignerInspectorView),
        new PropertyMetadata(false, (d, _) => ((DesignerInspectorView)d).SyncModeTabs()));

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
    private void OnOperationsTabClick(object sender, RoutedEventArgs e) => IsMachineControlMode = false;

    private void OnMachineTabClick(object sender, RoutedEventArgs e) => IsMachineControlMode = true;

    /// <summary>Keeps the top switch in step when the mode is changed from anywhere else.</summary>
    private void SyncModeTabs()
    {
        if (OperationsTab is null || MachineTab is null) return;
        OperationsTab.IsChecked = !IsMachineControlMode;
        MachineTab.IsChecked = IsMachineControlMode;
    }

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

    /// <summary>
    /// "Vybrat" on the Materiál row: opens the Vzorník materiálů as a modal picker for this one
    /// operation. Nothing is written here; the picker applies through LayerSettings.ApplyRecipe only
    /// when Aplikovat is pressed, and Zrušit or closing the window leaves the operation untouched.
    /// Saved personal recipes are offered in the same window (Moje recepty).
    /// </summary>
    private void OnPickMaterialClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            sender is not FrameworkElement { Tag: LayerSettings layer })
        {
            return;
        }

        try
        {
            new MaterialsWindow(viewModel, layer) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open the material picker");
            LaseroDialogWindow.Show(Window.GetWindow(this), new LaseroDialogOptions(
                "Vzorník nelze otevřít",
                "Vzorník materiálů se nepodařilo načíst. Podrobnosti byly uloženy do diagnostického protokolu.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }

    // ------------------------------------------------------------------
    // Drag and drop of layers. The order of the list is the order the machine runs the layers, top first,
    // so dragging a layer up runs it earlier. The row's own buttons (visibility, enable) keep working:
    // a drag only starts from the row body, after the pointer has moved a few pixels.
    // ------------------------------------------------------------------

    private Point _layerDragStart;
    private LayerSettings? _layerDragCandidate;
    private int _layerDropIndex = -1;

    private void OnLayerDragStart(object sender, MouseButtonEventArgs e)
    {
        _layerDragCandidate = null;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(LayersList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        _layerDragCandidate = item.DataContext as LayerSettings;
        _layerDragStart = e.GetPosition(LayersList);
    }

    private void OnLayerDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _layerDragCandidate is null) return;
        var delta = e.GetPosition(LayersList) - _layerDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var layer = _layerDragCandidate;
        _layerDragCandidate = null;
        DragDrop.DoDragDrop(LayersList, new DataObject(typeof(LayerSettings), layer), DragDropEffects.Move);
        HideLayerDropIndicator();
    }

    private void OnLayerDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LayerSettings)))
        {
            e.Effects = DragDropEffects.None;
            HideLayerDropIndicator();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var point = e.GetPosition(LayersList);
        var target = ItemsControl.ContainerFromElement(LayersList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var layers = (DataContext as MainViewModel)?.Scene.Layers;
        if (layers is null) return;

        var insertionIndex = layers.Count;
        var lineY = LayersList.ActualHeight;
        if (target?.DataContext is LayerSettings over)
        {
            var index = layers.IndexOf(over);
            var topLeft = target.TranslatePoint(new Point(0, 0), LayersList);
            var before = point.Y < topLeft.Y + target.ActualHeight / 2;
            insertionIndex = index + (before ? 0 : 1);
            lineY = topLeft.Y + (before ? 0 : target.ActualHeight);
        }

        _layerDropIndex = insertionIndex;
        LayerDropIndicator.Width = Math.Max(0, LayersList.ActualWidth - 12);
        Canvas.SetLeft(LayerDropIndicator, 6);
        Canvas.SetTop(LayerDropIndicator, lineY);
        LayerDropIndicator.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnLayerDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(LayersList);
        if (point.X < 0 || point.Y < 0 || point.X > LayersList.ActualWidth || point.Y > LayersList.ActualHeight)
            HideLayerDropIndicator();
    }

    private void OnLayerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayerSettings)) is not LayerSettings layer) return;
        if (DataContext is not MainViewModel main) return;

        var layers = main.Scene.Layers;
        var insertionIndex = _layerDropIndex >= 0 ? _layerDropIndex : layers.Count;
        var oldIndex = layers.IndexOf(layer);
        var newIndex = insertionIndex - (oldIndex >= 0 && oldIndex < insertionIndex ? 1 : 0);
        main.Scene.MoveLayerTo(layer, newIndex);
        HideLayerDropIndicator();
        e.Handled = true;
    }

    private void HideLayerDropIndicator()
    {
        _layerDropIndex = -1;
        LayerDropIndicator.Visibility = Visibility.Collapsed;
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        for (; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }
}
