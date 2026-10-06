using System.IO;
using System.Text.Json;
using Lasero.App;
using Lasero.App.Tour;

namespace Lasero.Tests;

/// <summary>Persistence and rules of first-run guidance: round trip through settings.json, per-account
/// scoping, the existing-user decision, tips shown once and one at a time.</summary>
public sealed class GuidanceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-guidance-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public GuidanceServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private (AppSettingsStore Store, GuidanceService Service) Create()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Load();
        return (store, new GuidanceService(store));
    }

    private static (AppSettingsStore Store, GuidanceService Service) Reload(string path)
    {
        var store = new AppSettingsStore(path);
        store.Load();
        return (store, new GuidanceService(store));
    }

    [Fact]
    public void NewAccountIsOwedTheWelcomeUntilItIsAnswered()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-new", hasPriorWork: false);
        Assert.True(service.ShouldOfferWelcome);
        Assert.Equal(WelcomeChoice.Pending, service.Welcome);

        service.RecordWelcome(WelcomeChoice.StartedTour);
        Assert.False(service.ShouldOfferWelcome);
    }

    [Fact]
    public void ChoicesSurviveARestartThroughSettingsJson()
    {
        var (_, first) = Create();
        first.SwitchAccount("uid-1", hasPriorWork: false);
        first.RecordWelcome(WelcomeChoice.StartedTour);
        first.RecordTour(TourOutcome.Finished);
        first.Suspended = false;
        first.TryOfferTip(TipCatalog.Import);

        var (_, second) = Reload(SettingsPath);
        second.SwitchAccount("uid-1", hasPriorWork: false);
        Assert.False(second.ShouldOfferWelcome);
        Assert.Equal(WelcomeChoice.StartedTour, second.Welcome);
        Assert.Equal(TourOutcome.Finished, second.Tour);
        Assert.True(second.HasSeenTip(TipCatalog.Import));
        Assert.False(second.HasSeenTip(TipCatalog.Connect));
    }

    [Fact]
    public void SkippedWelcomeNeverComesBackOnItsOwn()
    {
        var (_, first) = Create();
        first.SwitchAccount("uid-1", false);
        first.RecordWelcome(WelcomeChoice.Skipped);

        var (_, second) = Reload(SettingsPath);
        second.SwitchAccount("uid-1", false);
        Assert.False(second.ShouldOfferWelcome);
        Assert.Equal(TourOutcome.Skipped, second.Tour);
    }

    [Fact]
    public void TourOutcomeIsRememberedPerAccountAndAccountsDoNotLeakIntoEachOther()
    {
        var (store, service) = Create();
        service.SwitchAccount("anna@example.cz", false);
        service.RecordWelcome(WelcomeChoice.StartedTour);
        service.RecordTour(TourOutcome.Finished);

        service.SwitchAccount("petr@example.cz", false);
        Assert.True(service.ShouldOfferWelcome);
        Assert.Equal(TourOutcome.NotStarted, service.Tour);

        service.SwitchAccount("anna@example.cz", false);
        Assert.False(service.ShouldOfferWelcome);
        Assert.Equal(TourOutcome.Finished, service.Tour);
        Assert.Equal(2, store.Current.Guidance.Accounts.Count);
    }

    [Fact]
    public void SettingsFileNeverContainsTheAccountIdentifierInTheClear()
    {
        var (_, service) = Create();
        service.SwitchAccount("anna@example.cz", false);
        service.RecordWelcome(WelcomeChoice.Skipped);

        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("anna@example.cz", text);
        Assert.Contains(AccountScopedStorage.KeyFor("anna@example.cz"), text);
    }

    [Fact]
    public void SettingsFileFromBeforeTheTourExistedLoadsWithEmptyGuidance()
    {
        File.WriteAllText(SettingsPath, "{\"Workspace\":{\"InspectorWidth\":400},\"Version\":1}");
        var (store, service) = Reload(SettingsPath);
        Assert.NotNull(store.Current.Guidance);
        Assert.Empty(store.Current.Guidance.Accounts);

        service.SwitchAccount("uid-1", hasPriorWork: false);
        Assert.True(service.ShouldOfferWelcome);
    }

    [Fact]
    public void GuidanceWithNullsInTheFileIsRepairedOnLoad()
    {
        File.WriteAllText(SettingsPath, "{\"Guidance\":{\"Accounts\":{\"abc\":{\"SeenTips\":null}}}}");
        var (store, _) = Reload(SettingsPath);
        Assert.NotNull(store.Current.Guidance.Accounts["abc"].SeenTips);

        File.WriteAllText(SettingsPath, "{\"Guidance\":null}");
        var (store2, _) = Reload(SettingsPath);
        Assert.NotNull(store2.Current.Guidance);
    }

    [Fact]
    public void ExistingUserWithHistoryGetsNoWelcomeNoTourAndNoTips()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-old", hasPriorWork: true);

        Assert.False(service.ShouldOfferWelcome);
        Assert.True(service.IsExistingUser);
        Assert.False(service.TryOfferTip(TipCatalog.Import));
        Assert.Null(service.CurrentTip);

        // The decision is stored: it does not flip when the history later disappears.
        var (_, again) = Reload(SettingsPath);
        again.SwitchAccount("uid-old", hasPriorWork: false);
        Assert.False(again.ShouldOfferWelcome);
        Assert.True(again.IsExistingUser);
    }

    [Fact]
    public void ResetTipsReEnablesTipsForAnExistingUserAndForgetsWhatWasShown()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-old", true);
        service.ResetTips();

        Assert.True(service.TryOfferTip(TipCatalog.Selection));
        service.DismissTip();
        Assert.False(service.TryOfferTip(TipCatalog.Selection));

        service.ResetTips();
        Assert.True(service.TryOfferTip(TipCatalog.Selection));
    }

    [Fact]
    public void ResetIntroOwesTheWelcomeAgainWithoutTouchingTips()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-1", false);
        service.RecordWelcome(WelcomeChoice.Skipped);
        service.TryOfferTip(TipCatalog.Kamil);
        service.DismissTip();

        service.ResetIntro();
        Assert.True(service.ShouldOfferWelcome);
        Assert.True(service.HasSeenTip(TipCatalog.Kamil));
        Assert.Equal(TourOutcome.NotStarted, service.Tour);
    }

    [Fact]
    public void NoTipBeforeTheWelcomeIsAnswered()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-1", false);
        Assert.False(service.TryOfferTip(TipCatalog.Import));
        service.RecordWelcome(WelcomeChoice.Skipped);
        Assert.True(service.TryOfferTip(TipCatalog.Import));
    }

    [Fact]
    public void TipIsShownAtMostOnceAndOnlyOneAtATime()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-1", false);
        service.RecordWelcome(WelcomeChoice.Skipped);

        Assert.True(service.TryOfferTip(TipCatalog.Import));
        Assert.Equal(TipCatalog.Import, service.CurrentTip!.Id);

        // A second tip while one is visible is refused and not recorded as seen.
        Assert.False(service.TryOfferTip(TipCatalog.Selection));
        Assert.False(service.HasSeenTip(TipCatalog.Selection));

        service.DismissTip();
        Assert.Null(service.CurrentTip);
        Assert.False(service.TryOfferTip(TipCatalog.Import));
        Assert.True(service.TryOfferTip(TipCatalog.Selection));
    }

    [Fact]
    public void ShownTipIsRecordedImmediatelySoAnUnexpectedExitCannotShowItTwice()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-1", false);
        service.RecordWelcome(WelcomeChoice.Skipped);
        service.TryOfferTip(TipCatalog.Frame);

        var (_, second) = Reload(SettingsPath);
        second.SwitchAccount("uid-1", false);
        Assert.True(second.HasSeenTip(TipCatalog.Frame));
        Assert.False(second.TryOfferTip(TipCatalog.Frame));
    }

    [Fact]
    public void SuspendedClearsAVisibleTipAndDefersTheNextUntilItEnds()
    {
        var (_, service) = Create();
        service.SwitchAccount("uid-1", false);
        service.RecordWelcome(WelcomeChoice.Skipped);
        service.TryOfferTip(TipCatalog.Selection);
        var changes = 0;
        service.TipChanged += () => changes++;

        service.Suspended = true;
        Assert.Null(service.CurrentTip);

        // The first connect made inside the wizard is not lost: it appears once the wizard is gone.
        Assert.False(service.TryOfferTip(TipCatalog.Connect));
        Assert.False(service.HasSeenTip(TipCatalog.Connect));
        service.Suspended = false;
        Assert.Equal(TipCatalog.Connect, service.CurrentTip!.Id);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void NoAccountMeansNoGuidanceAtAll()
    {
        var (_, service) = Create();
        service.SwitchAccount(null, false);
        Assert.False(service.ShouldOfferWelcome);
        Assert.False(service.TryOfferTip(TipCatalog.Import));
        service.RecordWelcome(WelcomeChoice.Skipped);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void SwitchingAccountDropsTheTipThatWasOnScreen()
    {
        var (_, service) = Create();
        service.SwitchAccount("a", false);
        service.RecordWelcome(WelcomeChoice.Skipped);
        service.TryOfferTip(TipCatalog.Import);
        service.SwitchAccount("b", false);
        Assert.Null(service.CurrentTip);
    }

    [Fact]
    public void EveryCatalogTipHasUniqueIdAndText()
    {
        Assert.Equal(8, TipCatalog.All.Count);
        Assert.Equal(TipCatalog.All.Count, TipCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.All(TipCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Text)));
    }

    [Fact]
    public void TipOfDayIsStableForADayAndRotatesWithTheOffsetAndTheDate()
    {
        var day = new DateTime(2026, 10, 5);
        Assert.Equal(TipOfDay.For(day), TipOfDay.For(day));
        Assert.NotEqual(TipOfDay.For(day), TipOfDay.For(day, 1));
        Assert.NotEqual(TipOfDay.For(day), TipOfDay.For(day.AddDays(1)));
        Assert.Contains(TipOfDay.For(day, -5), TipOfDay.All);
        var seen = Enumerable.Range(0, TipOfDay.All.Count).Select(i => TipOfDay.For(day, i)).Distinct().Count();
        Assert.Equal(TipOfDay.All.Count, seen);
    }

    [Fact]
    public void SavedGuidanceJsonRoundTripsThroughTheSerializer()
    {
        var (store, service) = Create();
        service.SwitchAccount("uid-1", false);
        service.RecordWelcome(WelcomeChoice.StartedTour);
        service.RecordTour(TourOutcome.Skipped);

        var json = File.ReadAllText(SettingsPath);
        using var doc = JsonDocument.Parse(json);
        var accounts = doc.RootElement.GetProperty("Guidance").GetProperty("Accounts");
        Assert.Equal(1, accounts.EnumerateObject().Count());
        Assert.Equal(store.Current.Guidance.Accounts.Single().Key, accounts.EnumerateObject().Single().Name);
    }
}

public sealed class EmptyCanvasTipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-empty-tip", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private (GuidanceService Service, GuidanceTriggers Triggers) Create()
    {
        Directory.CreateDirectory(_directory);
        var store = new Lasero.App.AppSettingsStore(Path.Combine(_directory, "settings.json"));
        store.Load();
        var service = new GuidanceService(store);
        service.SwitchAccount("u", hasPriorWork: false);
        service.RecordWelcome(WelcomeChoice.Skipped);
        return (service, new GuidanceTriggers(service));
    }

    [Fact]
    public void ShownOnceOnEmptyCanvasAndNeverAgainUntilTipsAreReset()
    {
        var (service, triggers) = Create();
        triggers.OnEmptyCanvasChanged(true);
        Assert.Equal(TipCatalog.EmptyCanvas, service.CurrentTip?.Id);

        service.DismissTip();
        triggers.OnEmptyCanvasChanged(true);
        Assert.Null(service.CurrentTip);

        service.ResetTips();
        triggers.OnEmptyCanvasChanged(true);
        Assert.Equal(TipCatalog.EmptyCanvas, service.CurrentTip?.Id);
    }

    [Fact]
    public void FirstInteractionWithdrawsItWithoutTouchingOtherTips()
    {
        var (service, triggers) = Create();
        triggers.OnEmptyCanvasChanged(true);
        triggers.OnCanvasInteraction();
        Assert.Null(service.CurrentTip);

        Assert.True(service.TryOfferTip(TipCatalog.Import));
        triggers.OnEmptyCanvasChanged(false);
        Assert.Equal(TipCatalog.Import, service.CurrentTip?.Id);
    }

    [Fact]
    public void NotOfferedWhenTheCanvasIsNotEmptyOrTheAccountIsAnExistingUser()
    {
        var (service, triggers) = Create();
        triggers.OnEmptyCanvasChanged(false);
        Assert.Null(service.CurrentTip);

        var store = new Lasero.App.AppSettingsStore(Path.Combine(_directory, "other.json"));
        store.Load();
        var existing = new GuidanceService(store);
        existing.SwitchAccount("old", hasPriorWork: true);
        new GuidanceTriggers(existing).OnEmptyCanvasChanged(true);
        Assert.Null(existing.CurrentTip);
    }

    [Fact]
    public void CopyIsShortNeutralAndFreeOfQuestionAndExclamationMarks()
    {
        var text = TipCatalog.Find(TipCatalog.EmptyCanvas)!.Text;
        Assert.DoesNotMatch(@"[?!]", text);
        Assert.True(text.Count(c => c == '.') <= 2);
        Assert.DoesNotContain("tlačítko", text);
    }
}
