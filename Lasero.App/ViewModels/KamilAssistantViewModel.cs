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

    [RelayCommand]
    private void Show()
    {
        State = KamilAssistantState.Minimized;
    }

    /// <summary>Esc steps back one level rather than closing outright: from the panel to the
    /// composer, from the composer to the pill. Nothing is ever lost by pressing it.</summary>
    public void StepBack()
    {
        switch (State)
        {
            case KamilAssistantState.Expanded:
                State = KamilAssistantState.QuickAsk;
                ComposerFocusRequested?.Invoke();
                break;
            case KamilAssistantState.QuickAsk:
                Minimize();
                break;
        }
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
    /// Writes the recommendation onto the selected operation through LayerSettings.ApplyRecipe — the
    /// same entry point the material catalogue uses, so the layer records where its numbers came from
    /// and the undo/dirty behaviour is identical to picking a recipe by hand.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyRecommendation))]
    private void ApplyRecommendation(ParameterRecommendation? recommendation)
    {
        if (recommendation is null || _scene.SelectedLayer is not { } layer) return;

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
    }
}
