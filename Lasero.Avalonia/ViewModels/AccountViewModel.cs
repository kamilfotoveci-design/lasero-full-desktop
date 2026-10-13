using System.Net.Http;
using System.Net.Mail;
using System.Reactive;
using System.Reactive.Linq;
using Lasero.Core.LaseroApi;
using ReactiveUI;
using Serilog;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// Port of Lasero.App/ViewModels/AccountViewModel.cs. This is a real client for lasero.net's live
/// Firebase auth + Netlify entitlement/licensing backend (Lasero.Core.LaseroApi) — the same one WPF
/// calls, not a placeholder. Only the MVVM plumbing changed (ReactiveCommand instead of RelayCommand).
/// Session persistence reuses Lasero.Core.LaseroApi.SessionStore verbatim (DPAPI-protected refresh
/// token in LocalApplicationData) — same file, same format, so a session created by one app resumes
/// in the other.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // SessionStore (Lasero.Core) is DPAPI-based,
// same as Lasero.App's own net8.0-windows TFM implicitly declares; Lasero.Avalonia stays plain
// net10.0 for future cross-platform work (see MIGRATION.md's cross-platform blockers), so this
// class documents the same Windows-only reality explicitly instead of suppressing the warning blindly.
public sealed class AccountViewModel : ReactiveObject
{
    private readonly LaseroAuthClient authClient;
    private readonly LaseroAccountClient accountClient;
    private readonly SessionStore sessionStore;
    private FirebaseSession? session;

    private string email = string.Empty;
    public string Email
    {
        get => email;
        set
        {
            this.RaiseAndSetIfChanged(ref email, value);
            EmailError = null;
            if (!IsBusy) StatusMessage = null;
            RaiseStateChanged();
        }
    }

    private string password = string.Empty;
    public string Password
    {
        get => password;
        set
        {
            this.RaiseAndSetIfChanged(ref password, value);
            PasswordError = null;
            if (!IsBusy) StatusMessage = null;
            RaiseStateChanged();
        }
    }

    private bool isSignedIn;
    public bool IsSignedIn { get => isSignedIn; private set { this.RaiseAndSetIfChanged(ref isSignedIn, value); RaiseStateChanged(); } }

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set { this.RaiseAndSetIfChanged(ref isBusy, value); RaiseStateChanged(); } }

    private bool isOffline;
    public bool IsOffline { get => isOffline; private set { this.RaiseAndSetIfChanged(ref isOffline, value); RaiseStateChanged(); } }

    private string? statusMessage;
    public string? StatusMessage { get => statusMessage; private set => this.RaiseAndSetIfChanged(ref statusMessage, value); }

    private string? emailError;
    public string? EmailError { get => emailError; private set => this.RaiseAndSetIfChanged(ref emailError, value); }

    private string? passwordError;
    public string? PasswordError { get => passwordError; private set => this.RaiseAndSetIfChanged(ref passwordError, value); }

    private bool isPremium;
    public bool IsPremium { get => isPremium; private set { this.RaiseAndSetIfChanged(ref isPremium, value); this.RaisePropertyChanged(nameof(LicenceLabel)); } }

    private int trialDaysLeft;
    public int TrialDaysLeft { get => trialDaysLeft; private set { this.RaiseAndSetIfChanged(ref trialDaysLeft, value); this.RaisePropertyChanged(nameof(LicenceLabel)); } }

    /// <summary>The licence line under the account's e-mail. Says what the entitlement actually is —
    /// a trial that has run out reads as expired rather than quietly as free.</summary>
    public string LicenceLabel => IsPremium
        ? "Plná licence"
        : TrialDaysLeft > 0 ? $"Zkušební verze · {TrialDaysLeft} dní" : "Bez licence";

    private string licenseCode = string.Empty;
    public string LicenseCode { get => licenseCode; set { this.RaiseAndSetIfChanged(ref licenseCode, value); RaiseStateChanged(); } }

    private string? userId;
    public string? UserId { get => userId; private set => this.RaiseAndSetIfChanged(ref userId, value); }

    public ReactiveCommand<Unit, Unit> SignIn { get; }
    public ReactiveCommand<Unit, Unit> RequestPasswordReset { get; }
    public ReactiveCommand<Unit, Unit> SignOut { get; }
    public ReactiveCommand<Unit, Unit> RedeemLicense { get; }

    private readonly System.Reactive.Subjects.Subject<Unit> stateChanged = new();
    private void RaiseStateChanged() => stateChanged.OnNext(Unit.Default);

    public AccountViewModel(LaseroAuthClient authClient, LaseroAccountClient accountClient, SessionStore sessionStore)
    {
        this.authClient = authClient;
        this.accountClient = accountClient;
        this.sessionStore = sessionStore;

        var canSignIn = stateChanged.StartWith(Unit.Default).Select(_ => !IsBusy && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password));
        var canRequestPasswordReset = stateChanged.StartWith(Unit.Default).Select(_ => !IsBusy && !string.IsNullOrWhiteSpace(Email));
        var canRedeem = stateChanged.StartWith(Unit.Default).Select(_ => IsSignedIn && !IsOffline && !IsBusy && !string.IsNullOrWhiteSpace(LicenseCode));

        SignIn = ReactiveCommand.CreateFromTask(DoSignInAsync, canSignIn);
        RequestPasswordReset = ReactiveCommand.CreateFromTask(DoRequestPasswordResetAsync, canRequestPasswordReset);
        SignOut = ReactiveCommand.Create(DoSignOut);
        RedeemLicense = ReactiveCommand.CreateFromTask(DoRedeemLicenseAsync, canRedeem);
    }

    /// <summary>Called once at startup, before any window shows — matches WPF's App.xaml.cs awaiting
    /// this ahead of deciding whether to show LoginWindow. Never touches the network if there is no
    /// stored session; a network failure with a cached session degrades to IsOffline rather than
    /// signing the operator out (editor and device connection still work offline).</summary>
    public async Task TryResumeSessionAsync()
    {
        var stored = sessionStore.TryLoad();
        if (stored is null) return;
        UserId = stored.LocalId;

        try
        {
            using var startupRefreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            session = await authClient.RefreshAsync(stored.RefreshToken, startupRefreshTimeout.Token);
            UserId = session.LocalId;
            Email = stored.Email ?? string.Empty;
            IsSignedIn = true;
            IsOffline = false;
            sessionStore.SaveRefreshToken(session.RefreshToken, session.LocalId, Email);
            _ = RefreshEntitlementAsync();
        }
        catch (LaseroAuthException ex)
        {
            Log.Information(ex, "Stored refresh token is no longer valid");
            sessionStore.Clear();
            session = null;
            UserId = null;
            IsSignedIn = false;
            IsOffline = false;
            StatusMessage = "Platnost přihlášení skončila. Přihlaste se prosím znovu.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Information(ex, "Session refresh failed because the network is unavailable; using cached offline session");
            Email = stored.Email ?? string.Empty;
            IsSignedIn = true;
            IsOffline = true;
            StatusMessage = "Offline režim: editor a připojení zařízení fungují. Online funkce jsou dočasně nedostupné.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stored session could not be restored");
            IsSignedIn = false;
            IsOffline = false;
            StatusMessage = "Přihlášení se nepodařilo obnovit. Přihlaste se prosím znovu.";
        }
    }

    public bool ValidateSignInForm()
    {
        EmailError = null;
        PasswordError = null;

        if (string.IsNullOrWhiteSpace(Email))
            EmailError = "Zadejte e-mailovou adresu.";
        else if (!MailAddress.TryCreate(Email.Trim(), out _))
            EmailError = "Zadejte platnou e-mailovou adresu.";

        if (string.IsNullOrWhiteSpace(Password))
            PasswordError = "Zadejte heslo.";

        return EmailError is null && PasswordError is null;
    }

    private async Task DoSignInAsync()
    {
        IsBusy = true;
        StatusMessage = "Ověřuji přihlášení…";
        try
        {
            session = await authClient.SignInAsync(Email.Trim(), Password);
            UserId = session.LocalId;
            sessionStore.SaveRefreshToken(session.RefreshToken, session.LocalId, Email.Trim());
            IsSignedIn = true;
            IsOffline = false;
            Password = string.Empty;
            _ = RefreshEntitlementAsync();
        }
        catch (LaseroAuthException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (TaskCanceledException)
        {
            StatusMessage = "Připojení trvalo příliš dlouho. Zkontrolujte internet a zkuste to znovu.";
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Nelze se připojit k Lasero účtu. Zkontrolujte internetové připojení.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sign-in request failed");
            StatusMessage = "Přihlášení se nezdařilo. Zkontrolujte údaje a zkuste to znovu.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DoRequestPasswordResetAsync()
    {
        IsBusy = true;
        StatusMessage = "Odesílám odkaz pro obnovu hesla…";
        try
        {
            await authClient.SendPasswordResetAsync(Email.Trim());
            StatusMessage = "Odkaz pro obnovu hesla byl odeslán na váš e-mail.";
        }
        catch (LaseroAuthException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Password reset request failed");
            StatusMessage = "Odkaz pro obnovu se nepodařilo odeslat. Zkuste to znovu později.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DoSignOut()
    {
        session = null;
        UserId = null;
        sessionStore.Clear();
        IsSignedIn = false;
        IsOffline = false;
        IsPremium = false;
        TrialDaysLeft = 0;
        StatusMessage = null;
    }

    private async Task DoRedeemLicenseAsync()
    {
        if (session is null) return;
        IsBusy = true;
        try
        {
            await EnsureFreshTokenAsync();
            var result = await accountClient.RedeemLicenseAsync(session!.IdToken, LicenseCode);
            StatusMessage = result.Ok
                ? $"Licence aktivována ({result.DaysLeft} dní zbývá)."
                : result.Error ?? "Kód se nepodařilo uplatnit.";
            if (result.Ok)
            {
                LicenseCode = string.Empty;
                await RefreshEntitlementAsync();
            }
        }
        catch (LaseroAuthException)
        {
            StatusMessage = "Platnost přihlášení skončila. Přihlaste se prosím znovu.";
        }
        catch (HttpRequestException)
        {
            StatusMessage = "Licenci nyní nelze ověřit. Zkontrolujte internetové připojení.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "License redemption failed");
            StatusMessage = "Licenci se nepodařilo aktivovat. Zkuste to později.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshEntitlementAsync()
    {
        if (session is null || IsOffline) return;
        try
        {
            var status = await accountClient.CheckPremiumAsync(session.LocalId);
            IsPremium = status.IsPremium;
            TrialDaysLeft = status.TrialDaysLeft;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh entitlement status");
        }
    }

    private async Task EnsureFreshTokenAsync()
    {
        if (session is null || !session.IsExpired) return;
        session = await authClient.RefreshAsync(session.RefreshToken);
    }

    /// <summary>Returns a current Firebase ID token without exposing refresh-token handling to callers.</summary>
    public async Task<string> GetIdTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn || IsOffline || session is null)
            throw new InvalidOperationException("Online přihlášení není dostupné.");

        if (session.IsExpired)
        {
            session = await authClient.RefreshAsync(session.RefreshToken, cancellationToken);
            UserId = session.LocalId;
            sessionStore.SaveRefreshToken(session.RefreshToken, session.LocalId, Email);
        }

        return session.IdToken;
    }
}
