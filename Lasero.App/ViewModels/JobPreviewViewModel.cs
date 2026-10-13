using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;

namespace Lasero.App.ViewModels;

/// <summary>One row of the preview legend: a colour swatch and the operation it stands for.</summary>
public sealed record PreviewLegendItem(string Name, string Detail, Brush Swatch);

/// <summary>
/// State of the "Náhled" window: the display switches, the timeline and playback. It holds a
/// <see cref="JobPreviewModel"/> and nothing else, so it has no way to reach the machine; playback only moves
/// <see cref="Progress"/> along the estimated timeline.
/// </summary>
public sealed partial class JobPreviewViewModel : ObservableObject
{
    public JobPreviewViewModel(JobPreviewModel model)
    {
        Model = model;
    }

    public JobPreviewModel Model { get; }

    /// <summary>"Zobrazit šikmé pohyby": draw the moves made with the laser off.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Legend))] private bool _showRapids = true;

    /// <summary>"Stínovat podle výkonu".</summary>
    [ObservableProperty] private bool _shadeByPower;

    /// <summary>"Invertovat": a dark canvas.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Legend))] private bool _invert;

    /// <summary>"Zobrazit legendu".</summary>
    [ObservableProperty] private bool _showLegend = true;

    /// <summary>Position on the timeline, 0..1. Opens complete, the way the finished job looks.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TimeLabel))] private double _progress = 1;

    [ObservableProperty] private bool _isPlaying;

    /// <summary>1 is normal speed, see <see cref="JobPreviewTimeline.PlaybackSeconds"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpeedLabel))] private double _speedMultiplier = 1;

    public bool HasNotes => Model.Notes.Count > 0;
    public string NotesText => string.Join(" ", Model.Notes);
    public string CutLabel => Model.Stats.CutLabel;
    public string RapidLabel => Model.Stats.RapidLabel;
    public string TotalLabel => Model.Stats.TotalLabel;
    public string SpeedLabel => SpeedMultiplier.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + "×";

    public string TimeLabel =>
        $"{JobPreviewStats.FormatMinutes(JobPreviewTimeline.TimeAt(Model.Estimate, Progress))} / {JobPreviewStats.FormatMinutes(Model.Estimate.Duration)}";

    public IReadOnlyList<PreviewLegendItem> Legend => BuildLegend();

    private IReadOnlyList<PreviewLegendItem> BuildLegend()
    {
        var items = new List<PreviewLegendItem>();
        for (var i = 0; i < Model.Operations.Count; i++)
        {
            if (!Model.OperationUsed[i]) continue;
            var operation = Model.Operations[i];
            items.Add(new PreviewLegendItem(operation.Name, operation.Detail, Freeze(PreviewPalette.Visible(operation.Color, Invert))));
        }

        if (ShowRapids && Model.HasTravel)
            items.Add(new PreviewLegendItem("Přesunout", "Rychlé pohyby bez laseru", Freeze(PreviewPalette.Travel)));
        return items;
    }

    private static Brush Freeze(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            return;
        }

        if (Progress >= 1) Progress = 0;
        IsPlaying = true;
    }

    /// <summary>Moves the timeline on by <paramref name="seconds"/> of wall-clock time; stops at the end.</summary>
    public void Advance(double seconds)
    {
        if (!IsPlaying) return;
        Progress = JobPreviewTimeline.Advance(Progress, seconds, Model.Estimate.Duration, SpeedMultiplier);
        if (Progress >= 1) IsPlaying = false;
    }
}
