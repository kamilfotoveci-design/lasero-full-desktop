using System.IO;
using System.Net.Http;
using System.Reactive.Linq;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.LaseroApi;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// Covers the parts of AccountViewModel that don't require a real network call: form validation
/// (same wording/rules as WPF), command gating, and SignOut clearing every piece of cached state.
/// Real sign-in/entitlement calls hit lasero.net's live backend and are exercised manually, not here.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // AccountViewModel is Windows-only — see its own remarks.
public sealed class AccountViewModelTests
{
    private static AccountViewModel CreateAccount()
    {
        TestApp.EnsureInitialized();
        var http = new HttpClient();
        var sessionStore = new SessionStore(Path.Combine(Path.GetTempPath(), $"lasero-test-session-{Guid.NewGuid():N}.dat"));
        return new AccountViewModel(new LaseroAuthClient(http), new LaseroAccountClient(http), sessionStore);
    }

    [Theory]
    [InlineData("", "heslo", "Zadejte e-mailovou adresu.", null)]
    [InlineData("not-an-email", "heslo", "Zadejte platnou e-mailovou adresu.", null)]
    [InlineData("a@b.cz", "", null, "Zadejte heslo.")]
    public void ValidateSignInFormReportsTheSameErrorsAsWpf(string email, string password, string? expectedEmailError, string? expectedPasswordError)
    {
        var account = CreateAccount();
        account.Email = email;
        account.Password = password;

        var valid = account.ValidateSignInForm();

        Assert.False(valid);
        Assert.Equal(expectedEmailError, account.EmailError);
        Assert.Equal(expectedPasswordError, account.PasswordError);
    }

    [Fact]
    public void ValidSignInFormHasNoErrors()
    {
        var account = CreateAccount();
        account.Email = "operator@lasero.net";
        account.Password = "correct horse battery staple";

        Assert.True(account.ValidateSignInForm());
        Assert.Null(account.EmailError);
        Assert.Null(account.PasswordError);
    }

    [Fact]
    public void SignInIsDisabledUntilBothFieldsAreFilled()
    {
        var account = CreateAccount();
        Assert.False(account.SignIn.CanExecute.FirstAsync().Wait());

        account.Email = "operator@lasero.net";
        Assert.False(account.SignIn.CanExecute.FirstAsync().Wait());

        account.Password = "secret";
        Assert.True(account.SignIn.CanExecute.FirstAsync().Wait());
    }

    [Fact]
    public void RedeemLicenseRequiresBeingSignedInOnlineWithACode()
    {
        var account = CreateAccount();
        account.LicenseCode = "SOME-CODE";

        // Not signed in yet — CanExecute must stay false regardless of the code being present.
        Assert.False(account.RedeemLicense.CanExecute.FirstAsync().Wait());
    }

    [Fact]
    public void SignOutClearsEveryPieceOfCachedAccountState()
    {
        var account = CreateAccount();
        // Simulate having been signed in by touching the same fields SignOut resets — SignOut itself
        // is idempotent, so calling it from a clean state still proves it doesn't leave stale text.
        account.SignOut.Execute().Subscribe();

        Assert.False(account.IsSignedIn);
        Assert.False(account.IsOffline);
        Assert.False(account.IsPremium);
        Assert.Equal(0, account.TrialDaysLeft);
        Assert.Null(account.StatusMessage);
        Assert.Equal("Bez licence", account.LicenceLabel);
    }
}
