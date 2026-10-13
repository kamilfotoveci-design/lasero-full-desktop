using System.IO;
using System.Reactive.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using ReactiveUI;

namespace Lasero.Avalonia.Views;

/// <summary>
/// Shape-tool button: a short click activates whichever shape it remembers (Editor.CurrentShapeTool);
/// a press-and-hold or a right-click opens a picker for the other seven. Mirrors Lasero.App's
/// DesignerToolRail.xaml.cs gesture (same 400ms long-press threshold and 6px move-cancel distance) —
/// Avalonia has no PreviewMouseMove/mouse-capture-on-button-press equivalent bound to XAML the way WPF
/// does, so this uses Pointer* events + explicit pointer capture instead, but the resulting workflow
/// (click = current shape, hold/right-click = picker, drag-off-before-release = cancelled) is the same.
/// </summary>
public sealed partial class EditorView : UserControl
{
    private const int LongPressThresholdMs = 400;
    private const double MoveCancelThresholdDips = 6;

    private readonly Button shapeToolButton;
    private readonly Popup shapePickerPopup;
    private DispatcherTimer? longPressTimer;
    private Point pressStartPoint;
    private bool isPressed;
    private bool longPressTriggered;

    private EditorViewModel? subscribedEditor;

    public EditorView()
    {
        AvaloniaXamlLoader.Load(this);
        shapeToolButton = this.FindControl<Button>("ShapeToolButton")!;
        shapePickerPopup = this.FindControl<Popup>("ShapePickerPopup")!;
        shapePickerPopup.PlacementTarget = shapeToolButton;
        DataContextChanged += OnDataContextChanged;
    }

    private EditorViewModel? Editor => (DataContext as ShellViewModel)?.Editor;

    /// <summary>Owns the Trasovat bitmapu/Odstranit pozadí window-opening and off-thread work that
    /// EditorViewModel raises events for — mirrors Lasero.App's MainWindow doing the same for
    /// SceneViewModel.TraceRasterRequested/BackgroundRemovalRequested, keeping that UI-only concern
    /// out of the view model.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedEditor is not null)
        {
            subscribedEditor.TraceRasterRequested -= OnTraceRasterRequested;
            subscribedEditor.BackgroundRemovalRequested -= OnBackgroundRemovalRequested;
        }

        subscribedEditor = Editor;

        if (subscribedEditor is not null)
        {
            subscribedEditor.TraceRasterRequested += OnTraceRasterRequested;
            subscribedEditor.BackgroundRemovalRequested += OnBackgroundRemovalRequested;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Import rail button. Port of Lasero.App's GCodeViewModel.LoadFile's file-type branch for SVG
    // and raster — G-code/.nc/.tap import is NOT wired here: that belongs to the G-code generation
    // phase (not started yet in Avalonia, see HANDOFF.md), which is also what would regenerate the
    // job after an SVG/raster import the way WPF's RegenerateFromScene does. Importing here only
    // places the object on the canvas, same as every other draw/import action in this editor today.
    // ---------------------------------------------------------------------------------------

    private static readonly FilePickerFileType SupportedImportTypes = new("Podporovaná grafika")
    {
        Patterns = ["*.svg", "*.png", "*.jpg", "*.jpeg", "*.bmp"],
    };

    [SupportedOSPlatform("windows")]
    private async void OnImportGraphicsClick(object? sender, RoutedEventArgs e)
    {
        var editor = Editor;
        if (editor is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importovat grafiku",
            AllowMultiple = false,
            FileTypeFilter = [SupportedImportTypes, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        const double importWidthMm = 100;
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".svg":
                    editor.ImportSvgFile(path, importWidthMm);
                    break;
                case ".png" or ".jpg" or ".jpeg" or ".bmp":
                    if (topLevel is not Window owner) return;
                    var importViewModel = editor.CreateRasterImportViewModel(path, importWidthMm);
                    var confirmed = await RasterImportWindow.ShowResultAsync(owner, importViewModel);
                    if (confirmed == true) editor.ImportRasterFile(path, importViewModel.BuildOptions());
                    break;
            }
        }
        catch (Exception ex)
        {
            editor.BackgroundRemovalError = $"Soubor se nepodařilo načíst: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private async void OnTraceRasterRequested(SceneObject source)
    {
        if (string.IsNullOrWhiteSpace(source.RasterFilePath)) return;
        var editor = Editor;
        if (editor is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        try
        {
            var traceViewModel = new BitmapTraceViewModel(source.RasterFilePath, source.LocalBounds.Width);
            var confirmed = await BitmapTraceWindow.ShowResultAsync(owner, traceViewModel);
            if (confirmed != true || traceViewModel.Result is not { } result) return;
            editor.ReplaceRasterWithTrace(source, result);
        }
        catch (Exception ex)
        {
            editor.BackgroundRemovalError = $"Bitmapu se nepodařilo trasovat: {ex.Message}";
        }
    }

    /// <summary>Handles "Odstranit pozadí" from the OBRÁZEK inspector section — port of
    /// Lasero.App/MainWindow.xaml.cs's OnBackgroundRemovalRequested. The one-time model download is
    /// the only step that gets a confirm dialog; the spinner/status/error are plain EditorViewModel
    /// state the OBRÁZEK section binds to directly.</summary>
    [SupportedOSPlatform("windows")]
    private async void OnBackgroundRemovalRequested(SceneObject source)
    {
        if (string.IsNullOrWhiteSpace(source.RasterFilePath)) return;
        var editor = Editor;
        if (editor is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var service = editor.BackgroundRemovalService;

        if (!service.IsModelAvailable)
        {
            var confirmed = await Lasero.Avalonia.Views.ConfirmationDialog.ShowAsync(owner, "Stažení AI modelu",
                "Pro odstranění pozadí je potřeba stáhnout lokální AI model.\nModel se stáhne pouze jednou a potom funguje offline.",
                "Stáhnout");
            if (!confirmed) return;

            editor.BackgroundRemovalError = null;
            editor.IsRemovingBackground = true;
            editor.BackgroundRemovalProgress = 0;
            editor.BackgroundRemovalStatus = "Stahuji AI model… 0 %";
            try
            {
                var progress = new Progress<double>(value =>
                {
                    editor.BackgroundRemovalProgress = Math.Clamp(value, 0, 1);
                    editor.BackgroundRemovalStatus = $"Stahuji AI model… {value:P0}";
                });
                await service.DownloadModelAsync(progress);
            }
            catch (Exception)
            {
                editor.IsRemovingBackground = false;
                editor.BackgroundRemovalError = "Stažení modelu se nezdařilo. Zkontrolujte připojení k internetu a zkuste to znovu.";
                return;
            }
        }

        editor.BackgroundRemovalError = null;
        editor.IsRemovingBackground = true;
        editor.BackgroundRemovalProgress = 1;
        editor.BackgroundRemovalStatus = "Odstraňuji pozadí…";
        try
        {
            var resultPath = await service.RemoveBackgroundAsync(source.RasterFilePath);
            editor.CommitBackgroundRemoval(source, resultPath);
        }
        catch (Exception)
        {
            editor.BackgroundRemovalError = "Odstranění pozadí se nezdařilo. Zkuste to znovu.";
        }
        finally
        {
            editor.IsRemovingBackground = false;
        }
    }

    private void OnShapeButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(shapeToolButton);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            CancelPress();
            OpenPicker();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        isPressed = true;
        longPressTriggered = false;
        pressStartPoint = point.Position;
        e.Pointer.Capture(shapeToolButton);
        StartLongPressTimer();
    }

    private void OnShapeButtonPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isPressed || longPressTriggered) return;
        var delta = e.GetPosition(shapeToolButton) - pressStartPoint;
        if (Math.Abs(delta.X) > MoveCancelThresholdDips || Math.Abs(delta.Y) > MoveCancelThresholdDips)
            CancelPress();
    }

    private void OnShapeButtonPointerExited(object? sender, PointerEventArgs e)
    {
        // Mirrors WPF: the picker is already open by the time the pointer could leave after a long
        // press, so this only ever cancels a pending short click (an accidental drag off the button).
        if (isPressed && !longPressTriggered) CancelPress();
    }

    private void OnShapeButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopLongPressTimer();
        e.Pointer.Capture(null);

        if (longPressTriggered)
        {
            // The picker is already open; releasing the button must not also draw a shape.
            isPressed = false;
            longPressTriggered = false;
            e.Handled = true;
            return;
        }

        if (!isPressed) return; // cancelled by drag or pointer-exit
        isPressed = false;

        Editor?.ShapeTool.Execute().Subscribe();
    }

    private void ActivateShape(ShapeKind shape)
    {
        var editor = Editor;
        if (editor is null) return;
        editor.SelectShape.Execute(shape).Subscribe();
    }

    private void StartLongPressTimer()
    {
        StopLongPressTimer();
        longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressThresholdMs) };
        longPressTimer.Tick += OnLongPressTimerTick;
        longPressTimer.Start();
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        StopLongPressTimer();
        if (!isPressed) return; // already released or cancelled before the threshold

        longPressTriggered = true;
        OpenPicker();
    }

    private void StopLongPressTimer()
    {
        if (longPressTimer is null) return;
        longPressTimer.Stop();
        longPressTimer.Tick -= OnLongPressTimerTick;
        longPressTimer = null;
    }

    private void CancelPress()
    {
        StopLongPressTimer();
        isPressed = false;
        longPressTriggered = false;
    }

    private void OpenPicker()
    {
        if (shapePickerPopup.IsOpen) return;
        shapePickerPopup.IsOpen = true;
    }

    private void OnShapePickerClosed(object? sender, EventArgs e)
    {
    }

    private void OnShapePickerItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: ShapeKind shape })
            ActivateShape(shape);

        shapePickerPopup.IsOpen = false;
    }

    // ---------------------------------------------------------------------------------------
    // NASTAVENÍ OPERACE: Počet průchodů stepper. Mirrors Lasero.App's
    // OnPassesIncrementClick/OnPassesDecrementClick — plain +/- on LayerSettings.Passes, at
    // least 1 (a layer that runs zero times is not a layer that runs, it's an accidental no-op).
    // ---------------------------------------------------------------------------------------

    private void OnPassesIncrementClick(object? sender, RoutedEventArgs e)
    {
        if (Editor?.SelectedLayer is { } layer) layer.Passes += 1;
    }

    private void OnPassesDecrementClick(object? sender, RoutedEventArgs e)
    {
        if (Editor?.SelectedLayer is { } layer) layer.Passes = Math.Max(1, layer.Passes - 1);
    }

    // ---------------------------------------------------------------------------------------
    // NASTAVENÍ OPERACE: inline material picker. Port of Lasero.App's DesignerInspectorView's
    // OnApplyMaterialClick — a grouped flyout menu built fresh on every open (not bound
    // declaratively, matching WPF's own imperative code-behind approach), listing the current
    // profile's recipes for this layer's own Mode. "Moje recepty" isn't included: custom presets
    // aren't ported (see MaterialsView), so this shows only the real, real "Doporučené" group
    // rather than an always-empty second one.
    // ---------------------------------------------------------------------------------------

    private void OnApplyMaterialClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (Editor?.SelectedLayer is not { } layer) return;
        if ((DataContext as ShellViewModel)?.Materials is not { } materials) return;

        var recommended = new MenuItem
        {
            Header = $"Doporučené · {materials.ActiveProfileLabel}",
            ItemsSource = materials.RecipesFor(layer.Mode)
                .Select(recipe =>
                {
                    var item = new MenuItem { Header = $"{recipe.MaterialName} · {recipe.SpeedMmPerMinute:0} mm/min · {recipe.PowerPercent:0} %" };
                    item.Click += (_, _) => layer.ApplyRecipe(recipe.Mode, recipe.SpeedMmPerMinute,
                        recipe.PowerPercent, recipe.Passes, recipe.FillLineIntervalMm, recipe.MaterialName);
                    return item;
                })
                .ToList(),
        };

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Items.Add(recommended);
        flyout.ShowAt(button);
    }
}
