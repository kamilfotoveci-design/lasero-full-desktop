using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.LaseroApi;
using Serilog;

namespace Lasero.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private const int MaxInputLength = 4000;
    private readonly LaseroChatClient _client;
    private readonly AccountViewModel _account;
    private readonly ChatStore _store;
    private readonly List<StoredChatSession> _sessions = [];
    private Guid? _currentSessionId;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<ChatSessionItem> Sessions { get; } = [];
    public IReadOnlyList<string> SuggestedQuestions { get; } =
    [
        "Jaké parametry mám použít pro překližku?",
        "Proč jsou okraje gravírování opálené?",
        "Jak bezpečně otestovat nový materiál?",
    ];

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasMessages;
    [ObservableProperty] private bool _hasSessions;

    public bool IsOffline => _account.IsOffline;
    public bool CanUseOnline => _account.IsSignedIn && !_account.IsOffline;
    public string AvailabilityText => IsOffline
        ? "Offline — historii můžete číst, nové zprávy vyžadují internet."
        : "Online — Kamil používá kontext vaší gravírky a projektu.";

    /// <summary>
    /// Supplies the workspace context sent with every question — selected material, operation and
    /// the connected machine. Set by the assistant host once at startup.
    ///
    /// A callback rather than a constructor dependency because this view model is a singleton created
    /// before the shell exists, and because the answer has to reflect the workspace at the moment the
    /// question is asked, not at the moment the chat was constructed.
    /// </summary>
    public Func<LaseroChatContext>? ContextProvider { get; set; }

    /// <summary>
    /// Reports which operation is selected in the workspace at the moment a message is recorded, so a
    /// recommendation parsed out of that message can later tell whether it is still about the
    /// operation it was talking about. Same "set by the host once at startup" shape as
    /// <see cref="ContextProvider"/>, and optional for the same reason: a chat opened before a host
    /// attaches must still work, just without staleness tracking.
    /// </summary>
    public Func<Guid?>? SelectedLayerIdProvider { get; set; }

    public ChatViewModel(LaseroChatClient client, AccountViewModel account, ChatStore store)
    {
        _client = client;
        _account = account;
        _store = store;
        _account.PropertyChanged += OnAccountPropertyChanged;
    }

    /// <summary>The neutral context used before a host attaches, and if a provider ever throws — a
    /// question must still be answerable when the shell cannot describe itself.</summary>
    private static LaseroChatContext DefaultContext => new(
        MachineName: null,
        MachineId: null,
        PowerWatts: 20,
        LaserType: "diode",
        LaserDescription: "diodový laser 450 nm",
        Experience: "beginner",
        MaterialName: null,
        Operation: "engrave");

    public void InitializeForCurrentAccount()
    {
        _sessions.Clear();
        Messages.Clear();
        Sessions.Clear();
        _currentSessionId = null;

        if (string.IsNullOrWhiteSpace(_account.UserId))
        {
            RefreshState();
            return;
        }

        _sessions.AddRange(_store.Load(_account.UserId)
            .OrderByDescending(session => session.UpdatedAt)
            .Take(30));
        RefreshSessionList();
        if (_sessions.Count > 0)
            OpenSession(_sessions[0].Id);
        else
            RefreshState();
    }

    private bool CanSend() => CanUseOnline
        && !IsBusy
        && !string.IsNullOrWhiteSpace(Input)
        && Input.Trim().Length <= MaxInputLength;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input.Trim();
        if (text.Length == 0 || text.Length > MaxInputLength) return;

        Input = string.Empty;
        StatusMessage = null;
        EnsureCurrentSession(text);
        var pendingMessage = AddMessage(LaseroChatRole.User, text);
        Persist();
        IsBusy = true;

        try
        {
            var idToken = await _account.GetIdTokenAsync();
            var history = Messages
                .Select(message => new LaseroChatTurn(message.Role, message.Text))
                .ToArray();
            var response = await _client.SendAsync(idToken, history, ResolveContext());
            AddMessage(LaseroChatRole.Assistant, response);
            Persist();
        }
        catch (LaseroChatException exception)
        {
            RestoreFailedMessage(pendingMessage, text);
            StatusMessage = exception.Message;
        }
        catch (TaskCanceledException)
        {
            RestoreFailedMessage(pendingMessage, text);
            StatusMessage = "Odpověď trvala příliš dlouho. Zkontrolujte připojení a zkuste to znovu.";
        }
        catch (HttpRequestException)
        {
            RestoreFailedMessage(pendingMessage, text);
            StatusMessage = "Lasero Chat je offline. Zpráva zůstala připravená; odešlete ji po připojení.";
        }
        catch (Exception exception)
        {
            RestoreFailedMessage(pendingMessage, text);
            Log.Warning(exception, "Lasero Chat request failed");
            StatusMessage = "Zprávu se nepodařilo zpracovat. Zkuste to prosím znovu.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LaseroChatContext ResolveContext()
    {
        if (ContextProvider is null) return DefaultContext;
        try
        {
            return ContextProvider() ?? DefaultContext;
        }
        catch (Exception exception)
        {
            // Describing the workspace is an enhancement to the answer, never a precondition for
            // getting one.
            Log.Warning(exception, "Could not read workspace context for Kamil; sending defaults");
            return DefaultContext;
        }
    }

    [RelayCommand]
    private void UseSuggestion(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion)) return;
        Input = suggestion;
    }

    [RelayCommand]
    private void NewChat()
    {
        _currentSessionId = null;
        Messages.Clear();
        StatusMessage = null;
        RefreshState();
    }

    [RelayCommand]
    private void OpenChat(ChatSessionItem? session)
    {
        if (session is not null) OpenSession(session.Id);
    }

    [RelayCommand]
    private void DeleteChat(ChatSessionItem? session)
    {
        if (session is null) return;
        _sessions.RemoveAll(item => item.Id == session.Id);
        if (_currentSessionId == session.Id) NewChat();
        PersistAll();
        RefreshSessionList();
    }

    private void EnsureCurrentSession(string firstMessage)
    {
        if (_currentSessionId is not null) return;
        var session = new StoredChatSession(
            Guid.NewGuid(),
            CreateTitle(firstMessage),
            DateTimeOffset.UtcNow,
            []);
        _sessions.Insert(0, session);
        _currentSessionId = session.Id;
        RefreshSessionList();
    }

    private ChatMessageItem AddMessage(LaseroChatRole role, string text)
    {
        var message = new ChatMessageItem(Guid.NewGuid(), role, text, DateTimeOffset.Now)
        {
            OriginLayerId = SelectedLayerIdProvider?.Invoke(),
        };
        Messages.Add(message);
        RefreshState();
        return message;
    }

    private void RestoreFailedMessage(ChatMessageItem pendingMessage, string text)
    {
        Messages.Remove(pendingMessage);
        Input = text;
        if (Messages.Count == 0 && _currentSessionId is { } emptySessionId)
        {
            _sessions.RemoveAll(session => session.Id == emptySessionId);
            _currentSessionId = null;
            PersistAll();
            RefreshSessionList();
        }
        else
        {
            Persist();
        }
        RefreshState();
    }

    private void OpenSession(Guid id)
    {
        var session = _sessions.FirstOrDefault(item => item.Id == id);
        if (session is null) return;
        _currentSessionId = id;
        Messages.Clear();
        foreach (var message in session.Messages)
            Messages.Add(new ChatMessageItem(message.Id, message.Role, message.Text, message.CreatedAt.ToLocalTime()));
        StatusMessage = null;
        RefreshState();
    }

    private void Persist()
    {
        if (_currentSessionId is null) return;
        var index = _sessions.FindIndex(session => session.Id == _currentSessionId.Value);
        if (index < 0) return;
        var current = _sessions[index];
        _sessions[index] = current with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = Messages.Select(message => new StoredChatMessage(
                message.Id, message.Role, message.Text, message.CreatedAt.ToUniversalTime())).ToArray(),
        };
        _sessions.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        PersistAll();
        RefreshSessionList();
    }

    private void PersistAll()
    {
        if (!string.IsNullOrWhiteSpace(_account.UserId))
            _store.Save(_account.UserId, _sessions.Take(30).ToArray());
    }

    private void RefreshSessionList()
    {
        Sessions.Clear();
        foreach (var session in _sessions.OrderByDescending(item => item.UpdatedAt))
            Sessions.Add(new ChatSessionItem(session.Id, session.Title, session.UpdatedAt.ToLocalTime()));
        RefreshState();
    }

    private void RefreshState()
    {
        HasMessages = Messages.Count > 0;
        HasSessions = Sessions.Count > 0;
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AccountViewModel.IsOffline) or nameof(AccountViewModel.IsSignedIn))
        {
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(CanUseOnline));
            OnPropertyChanged(nameof(AvailabilityText));
            SendCommand.NotifyCanExecuteChanged();
        }
        else if (eventArgs.PropertyName == nameof(AccountViewModel.UserId))
        {
            // Session-identity-driven, not dependent on any window being reopened or shown — this
            // is what makes InitializeForCurrentAccount fire reliably on every sign-in, sign-out,
            // and account switch, instead of relying on callers to remember to call it.
            InitializeForCurrentAccount();
        }
    }

    partial void OnInputChanged(string value)
    {
        if (value.Length > MaxInputLength)
            StatusMessage = $"Zpráva může mít nejvýše {MaxInputLength} znaků.";
        else if (StatusMessage?.StartsWith("Zpráva může") == true)
            StatusMessage = null;
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    private static string CreateTitle(string text) => text.Length <= 44 ? text : text[..43].TrimEnd() + "…";
}

public sealed record ChatSessionItem(Guid Id, string Title, DateTimeOffset UpdatedAt)
{
    public string UpdatedLabel => UpdatedAt.Date == DateTime.Today
        ? UpdatedAt.ToString("HH:mm")
        : UpdatedAt.ToString("d. M.");
}

public sealed record ChatMessageItem(Guid Id, LaseroChatRole Role, string Text, DateTimeOffset CreatedAt)
{
    private bool _recommendationResolved;
    private ParameterRecommendation? _recommendation;

    public bool IsUser => Role == LaseroChatRole.User;
    public string Author => IsUser ? "Vy" : "Kamil";
    public string TimeLabel => CreatedAt.ToString("HH:mm");
    public string DisplayText => IsUser ? Text : ChatResponseNormalizer.Normalize(Text);

    /// <summary>
    /// Which operation was selected in the workspace when this message was recorded, if known. Null
    /// for messages loaded from a session persisted before this was tracked, or recorded with nothing
    /// selected — a recommendation with no origin is never treated as stale (see
    /// <see cref="KamilAssistantViewModel.IsRecommendationStale"/>).
    /// </summary>
    public Guid? OriginLayerId { get; init; }

    /// <summary>
    /// Machine settings named in this answer, if it named any. Parsed once and remembered — the
    /// message text never changes, and the list re-templates on every scroll.
    /// </summary>
    public ParameterRecommendation? Recommendation
    {
        get
        {
            if (_recommendationResolved) return _recommendation;
            _recommendationResolved = true;
            _recommendation = IsUser ? null : ParameterRecommendation.TryParse(Text);
            if (_recommendation is not null) _recommendation.OriginLayerId = OriginLayerId;
            return _recommendation;
        }
    }

    public bool HasRecommendation => Recommendation is not null;
}
