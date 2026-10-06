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

    /// <summary>How long an empty Návrh must stay empty before its tip appears.</summary>
    public static readonly TimeSpan EmptyCanvasDelay = TimeSpan.FromSeconds(1.5);

    private DispatcherTimer? _emptyTimer;

    public void OnFileImported() => _guidance.TryOfferTip(TipCatalog.Import);

    /// <summary>
    /// The empty-canvas tip: offered once the empty Návrh has been quiet for <see cref="EmptyCanvasDelay"/>
    /// (immediately without a dispatcher, for tests), withdrawn the moment anything is drawn, imported or
    /// clicked, or the operator leaves the screen. It replaces a permanent card, so it is never kept up.
    /// </summary>
    public void OnEmptyCanvasChanged(bool emptyDesignerWithSelectTool)
    {
        _emptyTimer?.Stop();
        if (!emptyDesignerWithSelectTool)
        {
            _guidance.DismissTipIf(TipCatalog.EmptyCanvas);
            return;
        }

        if (_guidance.HasSeenTip(TipCatalog.EmptyCanvas)) return;
        if (_dispatcher is null)
        {
            _guidance.TryOfferTip(TipCatalog.EmptyCanvas);
            return;
        }

        _emptyTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = EmptyCanvasDelay };
        _emptyTimer.Tick -= OnEmptyTimer;
        _emptyTimer.Tick += OnEmptyTimer;
        _emptyTimer.Start();
    }

    private void OnEmptyTimer(object? sender, EventArgs e)
    {
        _emptyTimer?.Stop();
        _guidance.TryOfferTip(TipCatalog.EmptyCanvas);
    }

    /// <summary>A click on the canvas counts as the first interaction.</summary>
    public void OnCanvasInteraction() => OnEmptyCanvasChanged(false);

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
        void Evaluate() => OnEmptyCanvasChanged(
            viewModel.CurrentScreen == AppScreen.Designer
            && viewModel.Scene.Objects.Count == 0
            && viewModel.Scene.ActiveTool == DesignerTool.Select);
        viewModel.Scene.Objects.CollectionChanged += (_, _) => Evaluate();
        viewModel.Scene.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SceneViewModel.ActiveTool)) Evaluate();
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentScreen)) Evaluate();
        };
        canvas.PreviewMouseDown += (_, _) => OnCanvasInteraction();
        Evaluate();
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
