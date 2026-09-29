using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Layers;

namespace Lasero.App.ViewModels;

/// <summary>
/// The floating assistant's own state — which of the three shapes it is in, what the workspace
/// context line says, and what applying a recommendation does.
///
/// Deliberately thin. Conversation, history, persistence and the network call all stay in the single
/// <see cref="ChatViewModel"/> the app already had, and this class holds a reference to it rather
/// than a copy: the Chat screen and the overlay are two surfaces onto one session, so a message sent
/// from either is the same message.
/// </summary>
public partial class KamilAssistantViewModel : ObservableObject
{
    private readonly SceneViewModel _scene;
    private readonly ConnectionViewModel _connection;

    public ChatViewModel Chat { get; }

    /// <summary>Three chips, no more: enough to start, few enough to stay out of the way.</summary>
    public IReadOnlyList<string> QuickPrompts { get; } =
    [
        "Doporučit parametry",
        "Pomoc s bitmapou",
        "Diagnostika výsledku",
    ];

    [ObservableProperty] private KamilAssistantState _state = KamilAssistantState.Minimized;

    public bool IsMinimized => State == KamilAssistantState.Minimized;
    public bool IsQuickAsk => State == KamilAssistantState.QuickAsk;
    public bool IsExpanded => State == KamilAssistantState.Expanded;
    public bool IsVisible => State != KamilAssistantState.Hidden;

    /// <summary>Raised when the surface should hand the keyboard back to whatever the operator was
    /// doing before. The view owns focus; a view model has no business calling Focus().</summary>
    public event Action? FocusReturnRequested;

    /// <summary>Raised when a state change should put the caret in the composer.</summary>
    public event Action? ComposerFocusRequested;

    public KamilAssistantViewModel(ChatViewModel chat, SceneViewModel scene, ConnectionViewModel connection)
    {
        Chat = chat;
        _scene = scene;
        _connection = connection;

        // The card's Apply button needs to know which operation a recommendation was originally
        // about, and that can only be captured at the moment the message arrives — by the time the
        // operator clicks Apply, SelectedLayer may already point at something else entirely. See
        // IsRecommendationStale below.
        Chat.SelectedLayerIdProvider = () => _scene.SelectedLayer?.Id;

        _scene.PropertyChanged += OnSceneChanged;
        _connection.PropertyChanged += OnConnectionChanged;
        Chat.PropertyChanged += OnChatChanged;
    }

    // ------------------------------------------------------------------
    // Workspace context
    // ------------------------------------------------------------------

    /// <summary>
    /// Which screen the operator is on. Set by MainViewModel rather than read from it, so the
    /// assistant does not need a reference back to the shell that hosts it.
    /// </summary>
    [ObservableProperty] private string _screenLabel = "Návrh";

    /// <summary>The chips under the header: only facts the application actually holds. A machine that
    /// is not connected contributes nothing rather than a placeholder.</summary>
    public string? MaterialContext => _scene.SelectedLayer?.MaterialLabel;

    public string? OperationContext => _scene.SelectedLayer is { } layer ? layer.ModeLabel : null;

    public string? MachineContext => _connection.IsConnected ? _connection.ActiveMachineName : null;

    public bool HasMaterialContext => !string.IsNullOrWhiteSpace(MaterialContext);
    public bool HasOperationContext => !string.IsNullOrWhiteSpace(OperationContext);
    public bool HasMachineContext => !string.IsNullOrWhiteSpace(MachineContext);

    /// <summary>What the endpoint is told about the workspace. Previously every field here was a
    /// hardcoded null or a guess, so Kamil answered without knowing what the operator was looking at.</summary>
    public Lasero.Core.LaseroApi.LaseroChatContext BuildContext()
    {
        var layer = _scene.SelectedLayer;
        return new Lasero.Core.LaseroApi.LaseroChatContext(
            MachineName: _connection.IsConnected ? _connection.ActiveMachineName : null,
            MachineId: null,
            PowerWatts: 20,
            LaserType: "diode",
            LaserDescription: "diodový laser 450 nm",
            Experience: "beginner",
            MaterialName: layer?.MaterialLabel,
            Operation: layer is null ? "engrave" : layer.Mode switch
            {
                LayerMode.Cut => "cut",
                LayerMode.Fill => "engrave",
                LayerMode.FillAndCut => "engrave+cut",
                _ => "engrave",
            });
    }

    // ------------------------------------------------------------------
    // State transitions
    //
    // Four shapes, one enum (KamilAssistantState: Minimized/QuickAsk/Expanded/Hidden). The mapping
    // from a state to on-screen geometry and layer visibility lives in the view
    // (KamilAssistantHost.xaml.cs) as two separate switches today; if a fifth state is ever added,
    // the single source of truth for "what does this state look like" should move onto the enum's
    // side (e.g. a small static lookup keyed by KamilAssistantState) so the view derives geometry
    // instead of restating it. Left as a note rather than a change here — the view does not need that
    // restructuring yet, and it is Phase 4's call to make once it owns that file again.
    // ------------------------------------------------------------------

    [RelayCommand]
    private void OpenQuickAsk()
    {
        State = KamilAssistantState.QuickAsk;
        ComposerFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void Expand()
    {
        State = KamilAssistantState.Expanded;
        ComposerFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void Minimize()
    {
        State = KamilAssistantState.Minimized;
        FocusReturnRequested?.Invoke();
    }

    /// <summary>Dismiss the surface, keep the session. Closing is about the screen being busy, not
    /// about being finished with the conversation, so nothing is discarded here.</summary>
    [RelayCommand]
    private void Close()
    {
        State = KamilAssistantState.Hidden;
        FocusReturnRequested?.Invoke();
    }

    /// <summary>Whether there is anything to reopen. Guarded to Hidden only: firing this while the
    /// assistant is already visible must never downgrade an Expanded panel back to a pill, so a
    /// stray second invocation (e.g. a double click on whatever Phase 4 wires this to) is a no-op
    /// rather than a surprise minimize.</summary>
    public bool CanShow => State == KamilAssistantState.Hidden;

    /// <summary>
    /// Reopens the assistant after <see cref="Close"/>, landing on the pill rather than wherever it
    /// was before closing — Close already promised the conversation was kept, not the panel size, and
    /// popping straight back to Expanded would be a bigger interruption than the reopen itself.
    ///
    /// This is the command Phase 1's audits flagged as dead: it existed but was never bound to
    /// anything in a view, so a user who clicked Close lost Kamil until an app restart. The intended
    /// binding target is the nav rail's existing Kamil-avatar ("Lasero Chat") button — Phase 4 wires
    /// that up; this command is now correct and guarded to be a safe target for it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShow))]
    private void Show()
    {
        // Execute() on an ICommand is not obliged to re-check CanExecute -- that is advisory for
        // whatever UI binds it -- so this guards itself too rather than trusting every future caller
        // to have asked first.
        if (State != KamilAssistantState.Hidden) return;
        State = KamilAssistantState.Expanded;
    }

    /// <summary>
    /// Esc jumps straight to Minimized from either QuickAsk or Expanded, in one press — the same
    /// destination the Minimize button reaches in one click.
    ///
    /// This used to step back one level at a time (Expanded to QuickAsk, then QuickAsk to Minimized),
    /// on the reasoning that Esc should be gentler than closing outright so an accidental press would
    /// not fully dismiss a panel someone was mid-scroll on. That reasoning does not survive contact
    /// with <see cref="Minimize"/> itself: the explicit Minimize button already jumps directly from
    /// Expanded to Minimized in one click, and a mouse click is at least as easy to fire by accident
    /// as Esc is. Two different speeds for reaching the same destination was not a safety net, it was
    /// an inconsistency — Esc being the slower of the two paths taught the operator nothing except
    /// that keyboard and mouse disagreed about what "back" means. Esc now matches the button. Nothing
    /// is lost either way: Minimized keeps the conversation exactly like every other transition here.
    /// </summary>
    public void StepBack()
    {
        if (State is KamilAssistantState.Expanded or KamilAssistantState.QuickAsk) Minimize();
    }

    [RelayCommand]
    private void UseQuickPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        Chat.Input = prompt;
        State = KamilAssistantState.Expanded;
        ComposerFocusRequested?.Invoke();
    }

    // ------------------------------------------------------------------
    // Applying a recommendation
    // ------------------------------------------------------------------

    /// <summary>A recommendation can only be applied onto an operation that exists and is unlocked
    /// from the job's point of view. With nothing selected the button is disabled and says why.</summary>
    public bool CanApplyRecommendation => _scene.SelectedLayer is not null;

    public string ApplyRecommendationTooltip => _scene.SelectedLayer is { } layer
        ? $"Nastaví rychlost, výkon a průchody na operaci „{layer.Name}“"
        : "Nejdřív vyberte operaci v panelu vpravo";

    /// <summary>
    /// Whether a card still describes the operation it was originally about. Every recommendation
    /// remembers which layer was selected when its message arrived (<see cref="ParameterRecommendation.OriginLayerId"/>);
    /// if the operator has since selected something else, applying the card's numbers to the new,
    /// unrelated selection would be silent and wrong — an old card sitting in scrollback should not be
    /// able to reach out and rewrite whatever operation happens to be selected now.
    ///
    /// A recommendation with no captured origin (older persisted sessions from before this existed, or
    /// a message recorded with nothing selected) is never treated as stale — the guard exists to catch
    /// a *known* mismatch, not to punish messages that predate it.
    /// </summary>
    public bool IsRecommendationStale(ParameterRecommendation? recommendation) =>
        recommendation?.OriginLayerId is { } originLayerId && _scene.SelectedLayer?.Id != originLayerId;

    private bool CanApplyThisRecommendation(ParameterRecommendation? recommendation) =>
        _scene.SelectedLayer is not null && !IsRecommendationStale(recommendation);

    /// <summary>
    /// Writes the recommendation onto the selected operation through LayerSettings.ApplyRecipe — the
    /// same entry point the material catalogue uses, so the layer records where its numbers came from
    /// and the undo/dirty behaviour is identical to picking a recipe by hand.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyThisRecommendation))]
    private void ApplyRecommendation(ParameterRecommendation? recommendation)
    {
        if (recommendation is null || _scene.SelectedLayer is not { } layer) return;
        if (IsRecommendationStale(recommendation)) return;

        layer.ApplyRecipe(
            layer.Mode,
            recommendation.SpeedMmPerMinute,
            recommendation.PowerPercent,
            recommendation.Passes,
            recommendation.FillLineIntervalMm,
            "Doporučení Kamila");

        LastAppliedMessage = $"Parametry nastaveny na operaci „{layer.Name}“.";
    }

    /// <summary>Confirmation of the last apply, shown inline on the card. The operator needs to know
    /// the click reached the operation, and the operation panel may be behind the assistant.</summary>
    [ObservableProperty] private string? _lastAppliedMessage;

    // ------------------------------------------------------------------

    private void OnSceneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SceneViewModel.SelectedLayer) or nameof(SceneViewModel.Selected))) return;
        RaiseContextChanged();
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.ActiveMachineName))) return;
        RaiseContextChanged();
    }

    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new answer supersedes the last apply confirmation; leaving it up would attach it to the
        // wrong recommendation.
        if (e.PropertyName == nameof(ChatViewModel.IsBusy) && Chat.IsBusy) LastAppliedMessage = null;
    }

    private void RaiseContextChanged()
    {
        OnPropertyChanged(nameof(MaterialContext));
        OnPropertyChanged(nameof(OperationContext));
        OnPropertyChanged(nameof(MachineContext));
        OnPropertyChanged(nameof(HasMaterialContext));
        OnPropertyChanged(nameof(HasOperationContext));
        OnPropertyChanged(nameof(HasMachineContext));
        OnPropertyChanged(nameof(CanApplyRecommendation));
        OnPropertyChanged(nameof(ApplyRecommendationTooltip));
        ApplyRecommendationCommand.NotifyCanExecuteChanged();
    }

    partial void OnStateChanged(KamilAssistantState value)
    {
        OnPropertyChanged(nameof(IsMinimized));
        OnPropertyChanged(nameof(IsQuickAsk));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(CanShow));
        ShowCommand.NotifyCanExecuteChanged();
    }
}
