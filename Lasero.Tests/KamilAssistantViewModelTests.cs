using System.IO;
using System.Net.Http;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;

namespace Lasero.Tests;

/// <summary>
/// Phase 3 repair pass: the real "no way back" bug was Close leaving nothing bound to reopen it
/// (ShowCommand had zero call sites), and Esc (StepBack) taking two presses to reach what the
/// Minimize button reaches in one. These tests pin the state machine so both stay fixed, plus the
/// invariants the Phase 1 audits called out as untested: minimizing/closing preserve the
/// conversation, and switching screens must not force the assistant back down.
///
/// Conversation fixtures add <see cref="ChatMessageItem"/> straight to Chat.Messages rather than
/// going through SendCommand — Send needs a signed-in account and a live Lasero Chat endpoint, and
/// none of that is what this file is testing.
/// </summary>
public sealed class KamilAssistantViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "lasero-kamil-vm-tests", Guid.NewGuid().ToString("N"));

    public KamilAssistantViewModelTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MinimizedOpensIntoQuickAsk()
    {
        var kamil = CreateViewModel();

        kamil.OpenQuickAskCommand.Execute(null);

        Assert.Equal(KamilAssistantState.QuickAsk, kamil.State);
    }

    [Fact]
    public void QuickAskMinimizesBackToThePill()
    {
        var kamil = CreateViewModel();
        kamil.OpenQuickAskCommand.Execute(null);

        kamil.MinimizeCommand.Execute(null);

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void QuickAskExpandsIntoTheFullPanel()
    {
        var kamil = CreateViewModel();
        kamil.OpenQuickAskCommand.Execute(null);

        kamil.ExpandCommand.Execute(null);

        Assert.Equal(KamilAssistantState.Expanded, kamil.State);
    }

    [Fact]
    public void ExpandedStepsBackToQuickAskExplicitly()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);

        kamil.OpenQuickAskCommand.Execute(null);

        Assert.Equal(KamilAssistantState.QuickAsk, kamil.State);
    }

    [Fact]
    public void ExpandedMinimizesDirectlyInOneHop()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);

        kamil.MinimizeCommand.Execute(null);

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void StepBackFromExpandedJumpsStraightToMinimizedInOnePress()
    {
        // F2 fix: StepBack used to land on QuickAsk first, requiring a second Esc to fully minimize,
        // even though the Minimize button already jumped there in one click. Esc now matches the
        // button — see the reasoning on KamilAssistantViewModel.StepBack.
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);

        kamil.StepBack();

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void StepBackFromQuickAskGoesToMinimized()
    {
        var kamil = CreateViewModel();
        kamil.OpenQuickAskCommand.Execute(null);

        kamil.StepBack();

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void StepBackFromMinimizedDoesNothing()
    {
        var kamil = CreateViewModel();

        kamil.StepBack();

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void MinimizingPreservesTheConversation()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);
        AddMessages(kamil, "Jaké parametry pro překližku?", "Zkuste 3500 mm/min a 35 %.");

        kamil.MinimizeCommand.Execute(null);

        Assert.Equal(2, kamil.Chat.Messages.Count);
        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
    }

    [Fact]
    public void ClosingHidesTheSurfaceButKeepsTheConversationAndReopeningRestoresIt()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);
        AddMessages(kamil, "Jaké parametry pro překližku?", "Zkuste 3500 mm/min a 35 %.");

        kamil.CloseCommand.Execute(null);
        Assert.Equal(KamilAssistantState.Hidden, kamil.State);
        Assert.False(kamil.IsVisible);
        Assert.Equal(2, kamil.Chat.Messages.Count);

        // F1 fix: ShowCommand existed but was never bound to anything in a view, so a user who
        // clicked Close had no way back short of restarting the app.
        Assert.True(kamil.ShowCommand.CanExecute(null));
        kamil.ShowCommand.Execute(null);

        Assert.Equal(KamilAssistantState.Minimized, kamil.State);
        Assert.True(kamil.IsVisible);
        Assert.Equal(2, kamil.Chat.Messages.Count);
    }

    [Fact]
    public void ShowIsANoOpWhileAlreadyVisible()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);

        Assert.False(kamil.ShowCommand.CanExecute(null));
        kamil.ShowCommand.Execute(null);

        // Guarded to Hidden only — firing Show while already visible must never downgrade an
        // Expanded panel back to a pill.
        Assert.Equal(KamilAssistantState.Expanded, kamil.State);
    }

    [Fact]
    public void SwitchingScreenLabelDoesNotForceTheAssistantBackDown()
    {
        var kamil = CreateViewModel();
        kamil.ExpandCommand.Execute(null);

        kamil.ScreenLabel = "Zařízení";
        kamil.ScreenLabel = "Domů";

        Assert.Equal(KamilAssistantState.Expanded, kamil.State);
    }

    [Fact]
    public void ApplyRecommendationIsDisabledWithNoOperationSelected()
    {
        var kamil = CreateViewModel(new SceneViewModel());
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %");

        Assert.False(kamil.ApplyRecommendationCommand.CanExecute(recommendation));
    }

    [Fact]
    public void ApplyRecommendationWritesOntoTheSelectedLayer()
    {
        var scene = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(new RgbColor(0, 0, 0), LayerMode.Fill, "Vrstva 1");
        scene.Layers.Add(layer);
        scene.SelectedLayer = layer;
        var kamil = CreateViewModel(scene);
        var recommendation = ParameterRecommendation.TryParse("rychlost 4000 mm/min, výkon 40 %, průchody 2");

        Assert.NotNull(recommendation);
        Assert.True(kamil.ApplyRecommendationCommand.CanExecute(recommendation));
        kamil.ApplyRecommendationCommand.Execute(recommendation);

        Assert.Equal(4000, layer.Speed);
        Assert.Equal(40, layer.Power);
        Assert.Equal(2, layer.Passes);
        Assert.NotNull(kamil.LastAppliedMessage);
    }

    [Fact]
    public void RecommendationStaysApplicableWhileItsOriginalLayerIsStillSelected()
    {
        var scene = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(new RgbColor(0, 0, 0), LayerMode.Fill, "Gravírování");
        scene.Layers.Add(layer);
        scene.SelectedLayer = layer;
        var kamil = CreateViewModel(scene);

        var recommendation = ParameterRecommendation.TryParse("rychlost 3500 mm/min, výkon 35 %");
        Assert.NotNull(recommendation);
        recommendation!.OriginLayerId = layer.Id;

        Assert.False(kamil.IsRecommendationStale(recommendation));
        Assert.True(kamil.ApplyRecommendationCommand.CanExecute(recommendation));
    }

    [Fact]
    public void RecommendationBecomesStaleAfterTheSelectionChangesToAnotherLayer()
    {
        var scene = new SceneViewModel();
        var originalLayer = LayerSettings.CreateDefault(new RgbColor(0, 0, 0), LayerMode.Fill, "Gravírování");
        var otherLayer = LayerSettings.CreateDefault(new RgbColor(255, 0, 0), LayerMode.Cut, "Řez");
        scene.Layers.Add(originalLayer);
        scene.Layers.Add(otherLayer);
        scene.SelectedLayer = originalLayer;
        var kamil = CreateViewModel(scene);

        var recommendation = ParameterRecommendation.TryParse("rychlost 3500 mm/min, výkon 35 %");
        Assert.NotNull(recommendation);
        recommendation!.OriginLayerId = originalLayer.Id;
        Assert.False(kamil.IsRecommendationStale(recommendation));

        scene.SelectedLayer = otherLayer;

        Assert.True(kamil.IsRecommendationStale(recommendation));
        Assert.False(kamil.ApplyRecommendationCommand.CanExecute(recommendation));

        var speedBefore = otherLayer.Speed;
        kamil.ApplyRecommendationCommand.Execute(recommendation);
        Assert.Equal(speedBefore, otherLayer.Speed);
    }

    [Fact]
    public void RecommendationWithNoCapturedOriginIsNeverTreatedAsStale()
    {
        var scene = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(new RgbColor(0, 0, 0), LayerMode.Fill, "Vrstva 1");
        scene.Layers.Add(layer);
        scene.SelectedLayer = layer;
        var kamil = CreateViewModel(scene);

        var recommendation = ParameterRecommendation.TryParse("rychlost 3500 mm/min, výkon 35 %");
        Assert.NotNull(recommendation);
        Assert.Null(recommendation!.OriginLayerId);

        Assert.False(kamil.IsRecommendationStale(recommendation));
        Assert.True(kamil.ApplyRecommendationCommand.CanExecute(recommendation));
    }

    [Fact]
    public void SelectedLayerIdProviderWiresChatToTheCurrentlySelectedLayer()
    {
        // The capture happens inside ChatViewModel.AddMessage, which is private, so the wiring this
        // constructor sets up is verified through the provider delegate itself rather than through a
        // live Send() call (Send needs a signed-in account and a reachable endpoint).
        var scene = new SceneViewModel();
        var kamil = CreateViewModel(scene);
        Assert.Null(kamil.Chat.SelectedLayerIdProvider?.Invoke());

        var layer = LayerSettings.CreateDefault(new RgbColor(0, 0, 0), LayerMode.Fill, "Vrstva 1");
        scene.Layers.Add(layer);
        scene.SelectedLayer = layer;

        Assert.Equal(layer.Id, kamil.Chat.SelectedLayerIdProvider?.Invoke());
    }

    private static void AddMessages(KamilAssistantViewModel kamil, params string[] texts)
    {
        foreach (var text in texts)
            kamil.Chat.Messages.Add(new ChatMessageItem(Guid.NewGuid(), LaseroChatRole.User, text, DateTimeOffset.Now));
    }

    private KamilAssistantViewModel CreateViewModel(SceneViewModel? scene = null)
    {
        var http = new HttpClient();
        var account = new AccountViewModel(
            new LaseroAuthClient(http),
            new LaseroAccountClient(http),
            new SessionStore(Path.Combine(_directory, $"session-{Guid.NewGuid():N}.json")),
            new DeviceIdStore(Path.Combine(_directory, $"device-id-{Guid.NewGuid():N}.txt")),
            new DeviceActivationClient(http));
        var chat = new ChatViewModel(
            new LaseroChatClient(http),
            account,
            new ChatStore(Path.Combine(_directory, $"chat-{Guid.NewGuid():N}")));

        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"));
        var connection = new ConnectionViewModel(machine, settings);

        return new KamilAssistantViewModel(chat, scene ?? new SceneViewModel(), connection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
