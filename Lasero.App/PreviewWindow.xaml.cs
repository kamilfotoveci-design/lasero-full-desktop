using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.App.ViewModels;
using Lasero.Core.Jobs;
using Microsoft.Win32;
using Serilog;

namespace Lasero.App;

/// <summary>
/// "Náhled": the toolpath of the next job, drawn from the generated G-code with a timeline and a playback.
/// It works with no machine attached and is purely visual: its view model holds a <see cref="JobPreviewModel"/>
/// only, and playback just advances a number that the canvas draws.
/// </summary>
public partial class PreviewWindow : Window
{
    private JobPreviewViewModel _viewModel;
    private TimeSpan? _lastFrame;
    private bool _playbackAttached;

    public PreviewWindow(JobPreviewModel model)
    {
        InitializeComponent();
        _viewModel = Attach(model);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => DetachPlayback();
    }

    /// <summary>Shows another job in this window, for a preview requested while it is already open.</summary>
    public void Load(JobPreviewModel model)
    {
        DetachPlayback();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = Attach(model);
    }

    private JobPreviewViewModel Attach(JobPreviewModel model)
    {
        var viewModel = new JobPreviewViewModel(model);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = viewModel;
        return viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ playback

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JobPreviewViewModel.IsPlaying)) return;
        if (_viewModel.IsPlaying) AttachPlayback();
        else DetachPlayback();
    }

    private void AttachPlayback()
    {
        if (_playbackAttached) return;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
        _playbackAttached = true;
    }

    private void DetachPlayback()
    {
        if (!_playbackAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _playbackAttached = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        var previous = _lastFrame;
        _lastFrame = frame.RenderingTime;
        if (previous is not { } before || frame.RenderingTime <= before) return;

        // A stall (window dragged, debugger) must not make the head jump across the job.
        var seconds = Math.Min((frame.RenderingTime - before).TotalSeconds, 0.25);
        _viewModel.Advance(seconds);
    }

    /// <summary>Grabbing the timeline hands control to the hand: playback stops where the thumb is picked up.</summary>
    private void OnTimelineDragStarted(object sender, DragStartedEventArgs e) => _viewModel.IsPlaying = false;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is ButtonBase or Slider or TextBox) return;
        if (_viewModel.TogglePlayCommand.CanExecute(null)) _viewModel.TogglePlayCommand.Execute(null);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ save

    private void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        var image = PreviewCanvas.CaptureImage();
        if (image is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Obrázek PNG (*.png)|*.png",
            FileName = "Náhled úlohy.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Job preview image could not be saved");
            LaseroDialogWindow.Show(this, new LaseroDialogOptions(
                "Obrázek se nepodařilo uložit",
                "Soubor se nepodařilo zapsat. Lze zvolit jiné umístění nebo ověřit, že složka je dostupná a soubor není otevřený jinde.",
                "Rozumím",
                CancelText: null,
                Tone: LaseroDialogTone.Danger));
        }
    }
}
