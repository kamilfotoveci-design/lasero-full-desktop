using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lasero.App.Controls;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

/// <summary>
/// Shows one contextual hint over the design canvas (never a static empty-state card). It only reads
/// state (scene selection and tool from <see cref="MainViewModel"/>, node-edit state from the canvas)
/// and hands it to <see cref="GuidanceText"/>; it changes nothing about how the canvas behaves.
/// </summary>
public partial class CanvasHintOverlay : UserControl
{
    public static readonly DependencyProperty TargetCanvasProperty = DependencyProperty.Register(
        nameof(TargetCanvas), typeof(SceneCanvas), typeof(CanvasHintOverlay),
        new PropertyMetadata(null, (d, e) => ((CanvasHintOverlay)d).OnTargetCanvasChanged((SceneCanvas?)e.OldValue, (SceneCanvas?)e.NewValue)));

    private readonly HashSet<string> _dismissed = new();
    private SceneViewModel? _scene;
    private CanvasHint? _current;

    public SceneCanvas? TargetCanvas
    {
        get => (SceneCanvas?)GetValue(TargetCanvasProperty);
        set => SetValue(TargetCanvasProperty, value);
    }

    public CanvasHintOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachScene();
        Loaded += (_, _) => AttachScene();
    }

    private void AttachScene()
    {
        var scene = (DataContext as MainViewModel)?.Scene;
        if (ReferenceEquals(scene, _scene)) return;

        if (_scene is not null)
        {
            _scene.PropertyChanged -= OnSceneChanged;
            _scene.Objects.CollectionChanged -= OnCollectionChanged;
            _scene.SelectedObjects.CollectionChanged -= OnCollectionChanged;
        }

        _scene = scene;
        if (_scene is not null)
        {
            _scene.PropertyChanged += OnSceneChanged;
            _scene.Objects.CollectionChanged += OnCollectionChanged;
            _scene.SelectedObjects.CollectionChanged += OnCollectionChanged;
        }

        Refresh();
    }

    private void OnTargetCanvasChanged(SceneCanvas? oldCanvas, SceneCanvas? newCanvas)
    {
        if (oldCanvas is not null)
        {
            DependencyPropertyDescriptor.FromProperty(SceneCanvas.IsNodeEditActiveProperty, typeof(SceneCanvas))
                .RemoveValueChanged(oldCanvas, OnCanvasStateChanged);
            DependencyPropertyDescriptor.FromProperty(SceneCanvas.SelectedNodeCountProperty, typeof(SceneCanvas))
                .RemoveValueChanged(oldCanvas, OnCanvasStateChanged);
        }

        if (newCanvas is not null)
        {
            DependencyPropertyDescriptor.FromProperty(SceneCanvas.IsNodeEditActiveProperty, typeof(SceneCanvas))
                .AddValueChanged(newCanvas, OnCanvasStateChanged);
            DependencyPropertyDescriptor.FromProperty(SceneCanvas.SelectedNodeCountProperty, typeof(SceneCanvas))
                .AddValueChanged(newCanvas, OnCanvasStateChanged);
        }

        Refresh();
    }

    private void OnCanvasStateChanged(object? sender, EventArgs e) => Refresh();

    private void OnSceneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneViewModel.ActiveTool) or nameof(SceneViewModel.Selected)
            or nameof(SceneViewModel.HasSelection))
            Refresh();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_scene is null)
        {
            HintChip.Visibility = Visibility.Collapsed;
            return;
        }

        var canvas = TargetCanvas;
        var context = new DesignerHintContext(
            _scene.ActiveTool,
            _scene.Objects.Count,
            _scene.SelectedObjects.Count,
            _scene.SelectedObjects.Count == 1 && _scene.Selected?.IsVectorPath == true,
            canvas?.IsNodeEditActive == true,
            canvas?.SelectedNodeCount ?? 0);

        var hint = GuidanceText.DesignerHint(context);
        _current = hint is not null && !_dismissed.Contains(hint.Key) ? hint : null;

        HintText.Text = _current?.Text;
        HintChip.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _dismissed.Add(_current.Key);
        Refresh();
    }
}
