using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.Controls;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class SelectionPropertiesBar : UserControl
{
    private bool _fontPickerSession;
    private Guid? _fontPickerObjectId;
    /// <summary>Mirrors CanvasViewControls/NodeEditToolbar's own TargetCanvas precedent — needed so
    /// the "Upravit uzly" button can call SceneCanvas.EnterNodeEditMode directly, the one-click
    /// mouse-driven entry point into Node Edit mode (the double-click-a-thin-stroke gesture stays
    /// available too, but is no longer the only way in).</summary>
    public static readonly DependencyProperty TargetCanvasProperty = DependencyProperty.Register(
        nameof(TargetCanvas), typeof(SceneCanvas), typeof(SelectionPropertiesBar),
        new PropertyMetadata(null, OnTargetCanvasChanged));

    private static void OnTargetCanvasChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SelectionPropertiesBar)d;
        if (e.OldValue is SceneCanvas old) old.PropertiesRequested -= bar.OnPropertiesRequested;
        if (e.NewValue is SceneCanvas current) current.PropertiesRequested += bar.OnPropertiesRequested;
    }

    /// <summary>"Zobrazit vlastnosti" on the canvas context menu. The bar is already on screen whenever
    /// something is selected, so showing the properties means putting the keyboard in them: the wording
    /// for text, otherwise the position. A field that is disabled (a locked selection) cannot take
    /// focus, so the first one that can is used. Deferred past the menu closing, which hands focus back
    /// to the canvas and would otherwise win.</summary>
    private void OnPropertiesRequested()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            foreach (var box in new[] { PositionXBox })
            {
                if (!box.IsVisible || !box.IsEnabled || !box.Focusable) continue;
                if (!box.Focus()) continue;
                box.SelectAll();
                return;
            }
        });
    }

    public SceneCanvas? TargetCanvas
    {
        get => (SceneCanvas?)GetValue(TargetCanvasProperty);
        set => SetValue(TargetCanvasProperty, value);
    }

    public SelectionPropertiesBar()
    {
        InitializeComponent();
    }

    private void OnEnterNodeEditClick(object sender, RoutedEventArgs e)
    {
        if (TargetCanvas is null || DataContext is not MainViewModel viewModel) return;
        if (viewModel.Scene.Selected is { IsVectorPath: true } selected)
            TargetCanvas.EnterNodeEditMode(selected);
    }

    // Every value field on this bar defers its binding to LostFocus, so a half-typed number never
    // reaches the scene. Enter commits it, Esc reverts it and a click selects it all: that is the
    // app-wide behaviour of InteractionBehaviors, not something this bar has to carry itself.

    private void OnFontDropDownOpened(object? sender, EventArgs e)
    {
        _fontPickerObjectId = (DataContext as MainViewModel)?.Scene.Selected?.Id;
        _fontPickerSession = _fontPickerObjectId is not null;
    }

    /// <summary>Pointing at a font in the open list shows it on the selected text at once. Nothing is
    /// committed until a font is explicitly chosen.</summary>
    private void OnFontItemMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_fontPickerSession && sender is ComboBoxItem { DataContext: string family } &&
            DataContext is MainViewModel main && main.Scene.Selected?.Id == _fontPickerObjectId)
            main.Scene.PreviewTextFontFamily(family);
    }

    private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fontPickerSession && sender is ComboBox { SelectedItem: string family } &&
            DataContext is MainViewModel main && main.Scene.Selected?.Id == _fontPickerObjectId)
            main.Scene.PreviewTextFontFamily(family);
    }

    private void OnFontItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_fontPickerSession || sender is not ComboBoxItem { DataContext: string family }) return;
        CommitFontChoice(family);
        e.Handled = true;
    }

    private void OnFontPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_fontPickerSession || sender is not ComboBox combo) return;
        if (e.Key == Key.Enter && combo.SelectedItem is string family)
        {
            CommitFontChoice(family);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            combo.IsDropDownOpen = false;
            e.Handled = true;
        }
    }

    private void CommitFontChoice(string family)
    {
        if (DataContext is MainViewModel main && main.Scene.Selected?.Id == _fontPickerObjectId)
            main.Scene.CommitTextFontPreview(family);
        _fontPickerSession = false;
        _fontPickerObjectId = null;
        FontPicker.IsDropDownOpen = false;
    }

    private void OnFontDropDownClosed(object? sender, EventArgs e)
    {
        _fontPickerSession = false;
        _fontPickerObjectId = null;
        if (DataContext is MainViewModel main) main.Scene.EndTextFontPreview();
    }
}
