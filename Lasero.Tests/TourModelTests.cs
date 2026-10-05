using System.Windows.Input;
using Lasero.App.Tour;
using Lasero.App.ViewModels;

namespace Lasero.Tests;

/// <summary>The pure half of the guided tour: which steps exist and in what order, how the session moves
/// through them, and what each key does. No window involved.</summary>
public sealed class TourModelTests
{
    [Fact]
    public void TourHasTheSevenStepsOfTheRealJourneyInOrder()
    {
        var ids = TourSteps.All.Select(s => s.Id).ToArray();
        Assert.Equal(["home", "designer", "operations", "materials", "connection", "run", "kamil"], ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void EachStepNamesTheScreenItNeedsSoTheTourNavigatesThere()
    {
        var screens = TourSteps.All.ToDictionary(s => s.Id, s => s.Screen);
        Assert.Equal(AppScreen.Home, screens["home"]);
        Assert.Equal(AppScreen.Designer, screens["designer"]);
        Assert.Equal(AppScreen.Designer, screens["operations"]);
        Assert.Equal(AppScreen.Designer, screens["materials"]);
        Assert.Equal(AppScreen.Device, screens["connection"]);
        Assert.Equal(AppScreen.Designer, screens["run"]);
        Assert.Equal(AppScreen.Designer, screens["kamil"]);
    }

    [Fact]
    public void EveryStepHasATargetATitleAndABody()
    {
        foreach (var step in TourSteps.All)
        {
            Assert.NotEmpty(step.TargetIds);
            Assert.All(step.TargetIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Body));
        }
    }

    [Fact]
    public void RunStepCarriesTheSafetyLineAndOperationsStepExplainsThePowerSpeedPasses()
    {
        var run = TourSteps.All.Single(s => s.Id == "run");
        Assert.Contains("odřezku", run.SafetyNote);
        Assert.Contains("brýlemi", run.SafetyNote);

        var operations = TourSteps.All.Single(s => s.Id == "operations");
        Assert.Equal(["Výkon", "Rychlost", "Průchody"], operations.Points!.Select(p => p.Term).ToArray());
        Assert.Contains("Použít na operaci", operations.Body);
    }

    [Fact]
    public void SessionWalksForwardThroughTheClosingCardAndFinishes()
    {
        var session = new TourSession();
        Assert.True(session.IsActive);
        Assert.Equal(0, session.Index);
        Assert.Equal("1 z 7", session.Counter);
        Assert.False(session.CanGoBack);

        for (var i = 1; i < 7; i++)
        {
            session.Next();
            Assert.Equal(i, session.Index);
            Assert.Equal($"{i + 1} z 7", session.Counter);
        }

        session.Next();
        Assert.True(session.IsFinishCard);
        Assert.Null(session.CurrentStep);
        Assert.Equal(string.Empty, session.Counter);

        session.Next();
        Assert.Equal(TourState.Finished, session.State);
        Assert.Equal(TourOutcome.Finished, session.Outcome);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void BackStopsAtTheFirstStepAndLeavesTheClosingCard()
    {
        var session = new TourSession();
        session.Back();
        Assert.Equal(0, session.Index);

        for (var i = 0; i < 7; i++) session.Next();
        Assert.True(session.IsFinishCard);
        session.Back();
        Assert.Equal(6, session.Index);
        Assert.False(session.IsFinishCard);
    }

    [Fact]
    public void SkipEndsTheTourAndNothingMovesAfterwards()
    {
        var session = new TourSession();
        session.Next();
        session.Skip();
        Assert.Equal(TourOutcome.Skipped, session.Outcome);

        var index = session.Index;
        session.Next();
        session.Back();
        session.Skip();
        Assert.Equal(index, session.Index);
        Assert.Equal(TourState.Skipped, session.State);
    }

    [Fact]
    public void ChangedFiresOncePerMoveAndNotForRefusedMoves()
    {
        var session = new TourSession();
        var count = 0;
        session.Changed += () => count++;
        session.Back();
        Assert.Equal(0, count);
        session.Next();
        session.Back();
        session.Skip();
        session.Next();
        Assert.Equal(3, count);
    }

    [Theory]
    [InlineData(Key.Enter, false, TourAction.Next)]
    [InlineData(Key.Right, false, TourAction.Next)]
    [InlineData(Key.Left, false, TourAction.Back)]
    [InlineData(Key.Escape, false, TourAction.Skip)]
    [InlineData(Key.Enter, true, TourAction.None)]
    [InlineData(Key.Right, true, TourAction.Next)]
    [InlineData(Key.Escape, true, TourAction.Skip)]
    [InlineData(Key.Space, false, TourAction.None)]
    [InlineData(Key.Delete, false, TourAction.None)]
    [InlineData(Key.V, false, TourAction.None)]
    public void KeysRouteToTourActions(Key key, bool focusOnButton, TourAction expected) =>
        Assert.Equal(expected, TourKeyRouter.Route(key, ModifierKeys.None, focusOnButton));

    [Theory]
    [InlineData(Key.Right, ModifierKeys.Control)]
    [InlineData(Key.Enter, ModifierKeys.Alt)]
    [InlineData(Key.Z, ModifierKeys.Control)]
    public void CtrlAndAltCombinationsAreNeverRoutedAsTourActions(Key key, ModifierKeys modifiers) =>
        Assert.Equal(TourAction.None, TourKeyRouter.Route(key, modifiers, false));

    [Fact]
    public void ApplyDispatchesTheRoutedAction()
    {
        var session = new TourSession();
        session.Apply(TourAction.Next);
        Assert.Equal(1, session.Index);
        session.Apply(TourAction.Back);
        Assert.Equal(0, session.Index);
        session.Apply(TourAction.None);
        Assert.Equal(0, session.Index);
        session.Apply(TourAction.Skip);
        Assert.Equal(TourState.Skipped, session.State);
    }
}
