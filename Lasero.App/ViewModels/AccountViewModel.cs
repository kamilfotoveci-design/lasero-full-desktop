using System.Net.Http;
using System.Net;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.LaseroApi;
using Serilog;

namespace Lasero.App.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    private readonly LaseroAuthClient _authClient;
    private readonly LaseroAccountClient _accountClient;
    private readonly SessionStore _sessionStore;
    private FirebaseSession? _session;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _emailError;
    [ObservableProperty] private string? _passwordError;
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private int _trialDaysLeft;

    /// <summary>The licence line under the account's e-mail. Says what the entitlement actually is —
    /// a trial that has run out reads as expired rather than quietly as free.</summary>
    public string LicenceLabel => IsPremium
        ? "Plná licence"
        : TrialDaysLeft > 0 ? $"Zkušební verze · {TrialDaysLeft} dní" : "Bez licence";

    partial void OnIsPremiumChanged(bool value) => OnPropertyChanged(nameof(LicenceLabel));
    partial void OnTrialDaysLeftChanged(int value) => OnPropertyChanged(nameof(LicenceLabel));
    [ObservableProperty] private string _licenseCode = string.Empty;
    [ObservableProperty] private string? _userId;

    public AccountViewModel(LaseroAuthClient authClient, LaseroAccountClient accountClient, SessionStore sessionStore)
    {
        _authClient = authClient;
        _accountClient = accountClient;
        _sessionStore = sessionStore;
    }

    public async Task TryResumeSessionAsync()
    {
        var stored = _sessionStore.TryLoad();
        if (stored is null) return;
        UserId = stored.LocalId;

        try
        {
            using var startupRefreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            _session = await _authClient.RefreshAsync(stored.RefreshToken, startupRefreshTimeout.Token);
            UserId = _session.LocalId;
            Email = stored.Email ?? string.Empty;
            IsSignedIn = true;
            IsOffline = false;
            _sessionStore.SaveRefreshToken(_session.RefreshToken, _session.LocalId, Email);
            _ = RefreshEntitlementAsync();
        }
        catch (LaseroAuthException ex)
        {
            Log.Information(ex, "Stored refresh token is no longer valid");
            _sessionStore.Clear();
            _session = null;
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

    private bool CanSignIn() => !IsBusy && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);

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

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignIn()
    {
        IsBusy = true;
        StatusMessage = "Ověřuji přihlášení…";
        try
        {
            _session = await _authClient.SignInAsync(Email.Trim(), Password);
            UserId = _session.LocalId;
            _sessionStore.SaveRefreshToken(_session.RefreshToken, _session.LocalId, Email.Trim());
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

    private bool CanRequestPasswordReset() => !IsBusy && !string.IsNullOrWhiteSpace(Email);

    [RelayCommand(CanExecute = nameof(CanRequestPasswordReset))]
    private async Task RequestPasswordReset()
    {
        IsBusy = true;
        StatusMessage = "Odesílám odkaz pro obnovu hesla…";
        try
        {
            await _authClient.SendPasswordResetAsync(Email.Trim());
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

    [RelayCommand]
    private void SignOut()
    {
        _session = null;
        UserId = null;
        _sessionStore.Clear();
        IsSignedIn = false;
        IsOffline = false;
        IsPremium = false;
        TrialDaysLeft = 0;
        StatusMessage = null;
    }

    private bool CanRedeem() => IsSignedIn && !IsOffline && !IsBusy && !string.IsNullOrWhiteSpace(LicenseCode);

    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private async Task RedeemLicense()
    {
        if (_session is null) return;
        IsBusy = true;
        try
        {
            await EnsureFreshTokenAsync();
            var result = await _accountClient.RedeemLicenseAsync(_session!.IdToken, LicenseCode);
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
        if (_session is null || IsOffline) return;
        try
        {
            var status = await _accountClient.CheckPremiumAsync(_session.LocalId);
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
        if (_session is null || !_session.IsExpired) return;
        _session = await _authClient.RefreshAsync(_session.RefreshToken);
    }

    /// <summary>Returns a current Firebase ID token without exposing refresh-token handling to callers.</summary>
    public async Task<string> GetIdTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn || IsOffline || _session is null)
            throw new InvalidOperationException("Online přihlášení není dostupné.");

        if (_session.IsExpired)
        {
            _session = await _authClient.RefreshAsync(_session.RefreshToken, cancellationToken);
            UserId = _session.LocalId;
            _sessionStore.SaveRefreshToken(_session.RefreshToken, _session.LocalId, Email);
        }

        return _session.IdToken;
    }

    partial void OnEmailChanged(string value)
    {
        EmailError = null;
        if (!IsBusy) StatusMessage = null;
        SignInCommand.NotifyCanExecuteChanged();
        RequestPasswordResetCommand.NotifyCanExecuteChanged();
    }

    partial void OnPasswordChanged(string value)
    {
        PasswordError = null;
        if (!IsBusy) StatusMessage = null;
        SignInCommand.NotifyCanExecuteChanged();
    }
    partial void OnLicenseCodeChanged(string value) => RedeemLicenseCommand.NotifyCanExecuteChanged();
    partial void OnIsSignedInChanged(bool value) => RedeemLicenseCommand.NotifyCanExecuteChanged();
    partial void OnIsOfflineChanged(bool value) => RedeemLicenseCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        SignInCommand.NotifyCanExecuteChanged();
        RequestPasswordResetCommand.NotifyCanExecuteChanged();
        RedeemLicenseCommand.NotifyCanExecuteChanged();
    }
}
