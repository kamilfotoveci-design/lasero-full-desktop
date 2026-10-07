using System.IO;
using System.Net.Http;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

/// <summary>Proves the Phase 2 session-identity-driven wiring: ChatViewModel, MaterialsViewModel and
/// HomeViewModel each react to AccountViewModel.UserId changing by reloading their own account-scoped
/// store, with no dependency on any window being shown, hidden, or reopened. Every store-level
/// isolation guarantee (no legacy-file migration, no cross-account bleed) is already proven by
/// ChatStoreTests/MaterialPresetStoreTests/RecentProjectsStoreTests — these tests exist to prove the
/// ViewModel layer actually calls into that store behavior at the right moment, which is the part
/// Phase 2 changed.
///
/// "Signed in as X" is simulated by setting AccountViewModel.UserId directly (matching what
/// TryResumeSessionAsync/SignIn do structurally) rather than performing a real network sign-in, which
/// these tests have no server to talk to. "Sign out" uses the real SignOutCommand wherever the
/// distinction matters, since that's the actual production code path and does more than just clear
/// UserId.</summary>
public sealed class AccountScopedViewModelLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-lifecycle-tests", Guid.NewGuid().ToString("N"));

    public AccountScopedViewModelLifecycleTests() => Directory.CreateDirectory(_directory);

    private AccountViewModel CreateAccount() => new(
        new LaseroAuthClient(new HttpClient()),
        new LaseroAccountClient(new HttpClient()),
        new SessionStore(Path.Combine(_directory, $"session-{Guid.NewGuid():N}.dat")),
        new DeviceIdStore(Path.Combine(_directory, $"device-id-{Guid.NewGuid():N}.txt")),
        new DeviceActivationClient(new HttpClient()));

    // ------------------------------------------------------------------
    // ChatViewModel
    // ------------------------------------------------------------------

    private static StoredChatSession Session(string title, string text) => new(
        Guid.NewGuid(), title, DateTimeOffset.UtcNow,
        [new StoredChatMessage(Guid.NewGuid(), LaseroChatRole.User, text, DateTimeOffset.UtcNow)]);

    private (ChatViewModel chat, ChatStore store, AccountViewModel account) CreateChat()
    {
        var store = new ChatStore(Path.Combine(_directory, $"chat-{Guid.NewGuid():N}"));
        var account = CreateAccount();
        var chat = new ChatViewModel(new LaseroChatClient(new HttpClient()), account, store);
        return (chat, store, account);
    }

    [Fact]
    public void Chat_SigningInLoadsThatAccountsSessionsAndSignOutClearsThem()
    {
        var (chat, store, account) = CreateChat();
        store.Save("account-a", [Session("A's konverzace", "Ahoj")]);

        account.UserId = "account-a";
        Assert.Single(chat.Sessions);
        Assert.True(chat.HasMessages);

        account.SignOutCommand.Execute(null);
        Assert.Empty(chat.Sessions);
        Assert.False(chat.HasMessages);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public void Chat_SignOutThenSignInAsDifferentAccount_NoBleed()
    {
        var (chat, store, account) = CreateChat();
        store.Save("account-a", [Session("A", "A msg")]);
        store.Save("account-b", [Session("B", "B msg")]);

        account.UserId = "account-a";
        Assert.Equal("A", chat.Sessions[0].Title);

        account.SignOutCommand.Execute(null);
        Assert.Empty(chat.Sessions);

        account.UserId = "account-b";
        Assert.Equal("B", chat.Sessions[0].Title);
        Assert.DoesNotContain(chat.Sessions, s => s.Title == "A");
    }

    [Fact]
    public void Chat_SwitchesDirectlyBetweenAccountsWithoutExplicitSignOut()
    {
        var (chat, store, account) = CreateChat();
        store.Save("account-a", [Session("A", "A msg")]);
        store.Save("account-b", [Session("B", "B msg")]);

        account.UserId = "account-a";
        Assert.Equal("A", chat.Sessions[0].Title);

        account.UserId = "account-b";
        Assert.Single(chat.Sessions);
        Assert.Equal("B", chat.Sessions[0].Title);
    }

    [Fact]
    public void Chat_RepeatedSignOut_StaysEmptyAndDoesNotThrow()
    {
        var (chat, store, account) = CreateChat();
        store.Save("account-a", [Session("A", "A msg")]);
        account.UserId = "account-a";

        account.SignOutCommand.Execute(null);
        Assert.Empty(chat.Sessions);

        account.SignOutCommand.Execute(null);
        Assert.Empty(chat.Sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Chat_EmptyOrWhitespaceUserId_TreatedAsSignedOut(string userId)
    {
        var (chat, store, account) = CreateChat();
        store.Save("account-a", [Session("A", "A msg")]);
        account.UserId = "account-a";
        Assert.Single(chat.Sessions);

        account.UserId = userId;
        Assert.Empty(chat.Sessions);
    }

    // ------------------------------------------------------------------
    // MaterialsViewModel
    // ------------------------------------------------------------------

    private (MaterialsViewModel materials, MaterialPresetStore store, AccountViewModel account) CreateMaterials()
    {
        var store = new MaterialPresetStore(Path.Combine(_directory, $"materials-{Guid.NewGuid():N}.json"));
        var settings = new AppSettingsStore(Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"));
        var account = CreateAccount();
        var materials = new MaterialsViewModel(store, settings, new MaterialSyncClient(new HttpClient()), account);
        return (materials, store, account);
    }

    private static void Seed(MaterialPresetStore store, string userId, string presetName)
    {
        store.SwitchAccount(userId);
        store.Add(MaterialPreset.Create(presetName, LayerMode.Cut, speed: 100, power: 50, passes: 1));
        store.SwitchAccount(null);
    }

    [Fact]
    public void Materials_SigningInLoadsThatAccountsPresetsAndSignOutClearsThem()
    {
        var (materials, store, account) = CreateMaterials();
        Seed(store, "account-a", "A's materiál");

        account.UserId = "account-a";
        Assert.Single(materials.Presets);
        Assert.Equal("A's materiál", materials.Presets[0].Name);

        account.SignOutCommand.Execute(null);
        Assert.Empty(materials.Presets);
    }

    [Fact]
    public void Materials_SignOutThenSignInAsDifferentAccount_NoBleed()
    {
        var (materials, store, account) = CreateMaterials();
        Seed(store, "account-a", "A's materiál");
        Seed(store, "account-b", "B's materiál");

        account.UserId = "account-a";
        Assert.Equal("A's materiál", materials.Presets[0].Name);

        account.SignOutCommand.Execute(null);
        Assert.Empty(materials.Presets);

        account.UserId = "account-b";
        Assert.Single(materials.Presets);
        Assert.Equal("B's materiál", materials.Presets[0].Name);
    }

    [Fact]
    public void Materials_SwitchesDirectlyBetweenAccountsWithoutExplicitSignOut()
    {
        var (materials, store, account) = CreateMaterials();
        Seed(store, "account-a", "A's materiál");
        Seed(store, "account-b", "B's materiál");

        account.UserId = "account-a";
        account.UserId = "account-b";

        Assert.Single(materials.Presets);
        Assert.Equal("B's materiál", materials.Presets[0].Name);
    }

    [Fact]
    public void Materials_RepeatedSignOut_StaysEmptyAndDoesNotThrow()
    {
        var (materials, store, account) = CreateMaterials();
        Seed(store, "account-a", "A's materiál");
        account.UserId = "account-a";

        account.SignOutCommand.Execute(null);
        Assert.Empty(materials.Presets);

        account.SignOutCommand.Execute(null);
        Assert.Empty(materials.Presets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Materials_EmptyOrWhitespaceUserId_TreatedAsSignedOut(string userId)
    {
        var (materials, store, account) = CreateMaterials();
        Seed(store, "account-a", "A's materiál");
        account.UserId = "account-a";
        Assert.Single(materials.Presets);

        account.UserId = userId;
        Assert.Empty(materials.Presets);
    }

    // ------------------------------------------------------------------
    // HomeViewModel (recent projects)
    // ------------------------------------------------------------------

    private (HomeViewModel home, RecentProjectsStore store, AccountViewModel account) CreateHome()
    {
        var store = new RecentProjectsStore(Path.Combine(_directory, $"recent-{Guid.NewGuid():N}.json"));
        var jobHistory = new JobHistoryStore(Path.Combine(_directory, $"jobs-{Guid.NewGuid():N}.json"));
        var settings = new AppSettingsStore(Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"));
        var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var connection = new ConnectionViewModel(machine, settings);
        var machineStatus = new MachineStatusViewModel(machine);
        var gcode = new GCodeViewModel(machine, new SceneViewModel(), settings);
        var account = CreateAccount();
        var home = new HomeViewModel(store, jobHistory, connection, machineStatus, gcode, account);
        return (home, store, account);
    }

    private static void Seed(RecentProjectsStore store, string userId, string projectName)
    {
        store.SwitchAccount(userId);
        store.Touch($@"C:\projects\{projectName}.lasero", projectName, null);
        store.SwitchAccount(null);
    }

    [Fact]
    public void Home_SigningInLoadsThatAccountsRecentProjectsAndSignOutClearsThem()
    {
        var (home, store, account) = CreateHome();
        Seed(store, "account-a", "A's projekt");

        account.UserId = "account-a";
        Assert.Single(home.RecentProjects);
        Assert.Equal("A's projekt", home.RecentProjects[0].Name);

        account.SignOutCommand.Execute(null);
        Assert.Empty(home.RecentProjects);
    }

    [Fact]
    public void Home_FeaturesTheMostRecentProjectAndKeepsTheRemainingProjectsInTheShelf()
    {
        var (home, store, account) = CreateHome();
        store.SwitchAccount("account-a");
        store.Touch(@"C:\projects\older.lasero", "Older", null);
        store.Touch(@"C:\projects\latest.lasero", "Latest", null);

        account.UserId = "account-a";

        Assert.Equal("Latest", home.FeaturedProject?.Name);
        Assert.Single(home.OtherRecentProjectRows);
        Assert.Equal("Older", home.OtherRecentProjectRows[0].Name);
    }

    [Fact]
    public void Home_SignOutThenSignInAsDifferentAccount_NoBleed()
    {
        var (home, store, account) = CreateHome();
        Seed(store, "account-a", "A's projekt");
        Seed(store, "account-b", "B's projekt");

        account.UserId = "account-a";
        Assert.Equal("A's projekt", home.RecentProjects[0].Name);

        account.SignOutCommand.Execute(null);
        Assert.Empty(home.RecentProjects);

        account.UserId = "account-b";
        Assert.Single(home.RecentProjects);
        Assert.Equal("B's projekt", home.RecentProjects[0].Name);
    }

    [Fact]
    public void Home_SwitchesDirectlyBetweenAccountsWithoutExplicitSignOut()
    {
        var (home, store, account) = CreateHome();
        Seed(store, "account-a", "A's projekt");
        Seed(store, "account-b", "B's projekt");

        account.UserId = "account-a";
        account.UserId = "account-b";

        Assert.Single(home.RecentProjects);
        Assert.Equal("B's projekt", home.RecentProjects[0].Name);
    }

    [Fact]
    public void Home_RepeatedSignOut_StaysEmptyAndDoesNotThrow()
    {
        var (home, store, account) = CreateHome();
        Seed(store, "account-a", "A's projekt");
        account.UserId = "account-a";

        account.SignOutCommand.Execute(null);
        Assert.Empty(home.RecentProjects);

        account.SignOutCommand.Execute(null);
        Assert.Empty(home.RecentProjects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Home_EmptyOrWhitespaceUserId_TreatedAsSignedOut(string userId)
    {
        var (home, store, account) = CreateHome();
        Seed(store, "account-a", "A's projekt");
        account.UserId = "account-a";
        Assert.Single(home.RecentProjects);

        account.UserId = userId;
        Assert.Empty(home.RecentProjects);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
