using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Jobs;

namespace Lasero.App.Tour;

/// <summary>
/// Turns real events into tip offers. Each handler is a one-line decision so it can be tested without a
/// window; <see cref="Attach"/> is the thin wiring to the live view models and canvas. Nothing here acts
/// on the design or the machine, and a handler never throws into the code that raised the event.
/// </summary>
public sealed class GuidanceTriggers
{
    private readonly GuidanceService _guidance;
    private readonly Dispatcher? _dispatcher;

    public GuidanceTriggers(GuidanceService guidance, Dispatcher? dispatcher = null)
    {
        _guidance = guidance ?? throw new ArgumentNullException(nameof(guidance));
        _dispatcher = dispatcher;
    }

    public void OnFileImported() => _guidance.TryOfferTip(TipCatalog.Import);

    /// <summary>Selection is offered a moment late: importing a file selects the new object, and the
    /// import tip is the one that matters at that instant.</summary>
    public void OnSelectionChanged(int selectedCount)
    {
        if (selectedCount < 1 || _guidance.HasSeenTip(TipCatalog.Selection)) return;
        if (_dispatcher is null)
        {
            _guidance.TryOfferTip(TipCatalog.Selection);
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_guidance.CurrentTip is null) _guidance.TryOfferTip(TipCatalog.Selection);
        }));
    }

    public void OnNodeEditChanged(bool active)
    {
        if (active) _guidance.TryOfferTip(TipCatalog.NodeEdit);
    }

    public void OnConnectedChanged(bool connected)
    {
        if (connected) _guidance.TryOfferTip(TipCatalog.Connect);
    }

    public void OnJobStateChanged(JobRunState state)
    {
        switch (state)
        {
            case JobRunState.Framing:
                _guidance.TryOfferTip(TipCatalog.Frame);
                break;
            case JobRunState.Running:
                _guidance.TryOfferTip(TipCatalog.Start);
                break;
        }
    }

    public void OnAssistantStateChanged(KamilAssistantState state)
    {
        if (state is KamilAssistantState.Expanded or KamilAssistantState.QuickAsk)
            _guidance.TryOfferTip(TipCatalog.Kamil);
    }

    public void Attach(MainViewModel viewModel, SceneCanvas canvas)
    {
        viewModel.Scene.FileImported += OnFileImported;
        viewModel.Scene.SelectedObjects.CollectionChanged += (_, _) => OnSelectionChanged(viewModel.Scene.SelectedObjects.Count);
        viewModel.Connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.IsConnected)) OnConnectedChanged(viewModel.Connection.IsConnected);
        };
        viewModel.GCode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GCodeViewModel.JobState)) OnJobStateChanged(viewModel.GCode.JobState);
        };
        viewModel.Kamil.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KamilAssistantViewModel.State)) OnAssistantStateChanged(viewModel.Kamil.State);
        };
        DependencyPropertyDescriptor.FromProperty(SceneCanvas.IsNodeEditActiveProperty, typeof(SceneCanvas))
            .AddValueChanged(canvas, (_, _) => OnNodeEditChanged(canvas.IsNodeEditActive));
    }
}
