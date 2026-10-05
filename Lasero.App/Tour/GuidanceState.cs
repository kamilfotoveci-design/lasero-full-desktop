namespace Lasero.App.Tour;

/// <summary>What the person chose on the welcome screen. Pending means the screen was never answered,
/// so it is still owed; the other two are final until the person asks for the intro again.</summary>
public enum WelcomeChoice
{
    Pending = 0,
    StartedTour = 1,
    Skipped = 2,
}

/// <summary>How the coach-mark tour ended. NotStarted covers both never offered and offered but not
/// begun; Finished and Skipped both mean the tour must never reappear on its own.</summary>
public enum TourOutcome
{
    NotStarted = 0,
    Finished = 1,
    Skipped = 2,
}

/// <summary>
/// Everything the first-run guidance remembers about one account. Plain data so it round-trips through
/// settings.json; the rules that act on it live in <see cref="GuidanceService"/>.
/// </summary>
public sealed class AccountGuidance
{
    public WelcomeChoice Welcome { get; set; } = WelcomeChoice.Pending;
    public TourOutcome Tour { get; set; } = TourOutcome.NotStarted;

    /// <summary>Ids of micro-tips already shown (or deliberately retired). A tip is shown at most once.</summary>
    public List<string> SeenTips { get; set; } = new();

    /// <summary>True when the entry was created for somebody who had already used the app (project
    /// history existed): they get no automatic welcome, tour or tips, only the manual entry points.</summary>
    public bool ExistingUser { get; set; }
}

/// <summary>
/// The additive settings section. Accounts are keyed by the same SHA256(uid) hash every other local
/// cache uses, so one Windows profile with two Lasero accounts keeps two separate progress records and
/// no e-mail address is written into settings.json. A settings file written before this section existed
/// simply deserialises to an empty dictionary.
/// </summary>
public sealed class GuidancePreferences
{
    public Dictionary<string, AccountGuidance> Accounts { get; set; } = new(StringComparer.Ordinal);
}
