using System.IO;
using Serilog;

namespace Lasero.App.Tour;

/// <summary>
/// The rules around first-run guidance, kept apart from any control: who is owed the welcome, how the
/// tour ended, which tips were shown, and the single tip chip that may be on screen. State lives in
/// <see cref="AppSettings.Guidance"/>, keyed per account (same hashed key as the other account-scoped
/// caches), so a second account on the same Windows profile starts from its own first run.
///
/// Existing users. A settings file or account entry that predates the tour is treated as a first run
/// only when the account has no work history (no recent projects, no recovery snapshot, no open project,
/// no legacy "onboarding-seen" marker). Anyone who has been using the app gets no automatic welcome, tour
/// or tips, and the decision is stored so it does not flip later; the intro stays one click away from
/// Home and Settings, and "Obnovit tipy" re-enables the tips.
/// </summary>
public sealed class GuidanceService
{
    private readonly AppSettingsStore _store;
    private string? _accountKey;
    private AccountGuidance? _entry;
    private GuidanceTip? _currentTip;
    private bool _suspended;
    private string? _pendingTipId;

    public GuidanceService(AppSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Raised when the visible tip changes (shown, dismissed, replaced by null).</summary>
    public event Action? TipChanged;

    public GuidanceTip? CurrentTip => _currentTip;
    public bool HasAccount => _entry is not null;

    /// <summary>The overlay sets this while the welcome, the tour or the device wizard is open, so no tip
    /// competes with them. Turning it on also clears a tip that is already showing. A tip whose event
    /// happened while suspended (the first connect made inside the wizard) is offered when it ends.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value)
            {
                ClearTip();
                return;
            }

            var pending = _pendingTipId;
            _pendingTipId = null;
            if (pending is not null) TryOfferTip(pending);
        }
    }

    /// <summary>Points the service at an account (null when nobody is signed in). A first look at an
    /// account decides whether it is a new person or an existing one, see the class summary.</summary>
    public void SwitchAccount(string? userId, bool hasPriorWork)
    {
        ClearTip();
        if (string.IsNullOrWhiteSpace(userId))
        {
            _accountKey = null;
            _entry = null;
            return;
        }

        _accountKey = AccountScopedStorage.KeyFor(userId);
        var accounts = _store.Current.Guidance.Accounts;
        if (accounts.TryGetValue(_accountKey, out var existing))
        {
            _entry = existing;
            return;
        }

        _entry = new AccountGuidance();
        if (hasPriorWork)
        {
            _entry.ExistingUser = true;
            _entry.Welcome = WelcomeChoice.Skipped;
            _entry.Tour = TourOutcome.Skipped;
        }
        accounts[_accountKey] = _entry;
        if (hasPriorWork) Persist();
    }

    public WelcomeChoice Welcome => _entry?.Welcome ?? WelcomeChoice.Skipped;
    public TourOutcome Tour => _entry?.Tour ?? TourOutcome.Skipped;
    public bool IsExistingUser => _entry?.ExistingUser == true;

    /// <summary>True when the welcome screen is still owed to this account.</summary>
    public bool ShouldOfferWelcome => _entry is { Welcome: WelcomeChoice.Pending, ExistingUser: false };

    public void RecordWelcome(WelcomeChoice choice)
    {
        if (_entry is null) return;
        _entry.Welcome = choice;
        if (choice == WelcomeChoice.Skipped) _entry.Tour = TourOutcome.Skipped;
        Persist();
    }

    public void RecordTour(TourOutcome outcome)
    {
        if (_entry is null || outcome == TourOutcome.NotStarted) return;
        _entry.Tour = outcome;
        if (_entry.Welcome == WelcomeChoice.Pending) _entry.Welcome = WelcomeChoice.StartedTour;
        Persist();
    }

    /// <summary>"Znovu zobrazit úvod": the next replay starts from the welcome, and the tour is open to
    /// being finished or skipped again. Tips keep their own record.</summary>
    public void ResetIntro()
    {
        if (_entry is null) return;
        _entry.Welcome = WelcomeChoice.Pending;
        _entry.Tour = TourOutcome.NotStarted;
        _entry.ExistingUser = false;
        Persist();
    }

    /// <summary>"Obnovit tipy": every micro-tip may be shown once more, and an existing user is no
    /// longer exempt from them.</summary>
    public void ResetTips()
    {
        if (_entry is null) return;
        _entry.SeenTips.Clear();
        _entry.ExistingUser = false;
        if (_entry.Welcome == WelcomeChoice.Pending) _entry.Welcome = WelcomeChoice.Skipped;
        ClearTip();
        Persist();
    }

    public bool HasSeenTip(string tipId) => _entry?.SeenTips.Contains(tipId) == true;

    /// <summary>
    /// Offers a tip for a real event. It is shown only when tips are enabled for the account, the welcome
    /// has been answered, nothing suspends tips, no other tip is showing and this one has never been
    /// shown. Showing it records it as seen immediately, which is what makes "at most once" hold even if
    /// the app is closed with the chip on screen. A refused offer is not recorded, so the next occurrence
    /// of the event may still show it.
    /// </summary>
    public bool TryOfferTip(string tipId)
    {
        if (_entry is null) return false;
        if (_entry.ExistingUser || _entry.Welcome == WelcomeChoice.Pending) return false;
        if (_entry.SeenTips.Contains(tipId)) return false;
        var tip = TipCatalog.Find(tipId);
        if (tip is null) return false;
        if (_suspended)
        {
            _pendingTipId = tipId;
            return false;
        }

        if (_currentTip is not null) return false;

        _entry.SeenTips.Add(tipId);
        _currentTip = tip;
        Persist();
        TipChanged?.Invoke();
        return true;
    }

    public void DismissTip() => ClearTip();

    /// <summary>Hides the current tip only when it is the given one, so a situational tip can leave the
    /// moment its situation ends without taking an unrelated tip down with it.</summary>
    public void DismissTipIf(string tipId)
    {
        if (_currentTip?.Id == tipId) ClearTip();
        if (_pendingTipId == tipId) _pendingTipId = null;
    }

    private void ClearTip()
    {
        if (_currentTip is null) return;
        _currentTip = null;
        TipChanged?.Invoke();
    }

    private void Persist()
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Guidance progress is a convenience; failing to remember it must never interrupt work.
            Log.Warning(ex, "Failed to persist guidance progress");
        }
    }
}
