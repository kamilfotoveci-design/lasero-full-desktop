using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia;

/// <summary>Port of Lasero.App/LoginWindow.xaml.cs. Raises SignedIn once AccountViewModel.IsSignedIn
/// flips true (App.axaml.cs closes this window and shows MainWindow in response) — same
/// DialogResult=true-on-sign-in idea as WPF, expressed as an event since Avalonia's ShowDialog return
/// value is awaited by the caller rather than read from a property after the fact.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // AccountViewModel is Windows-only — see its own remarks.
public sealed partial class LoginWindow : Window
{
    private readonly AccountViewModel account;

    // No parameterless constructor: LoginWindow always needs a real AccountViewModel from DI (see
    // App.axaml.cs) — there is no meaningful design-time instance to fall back to. This means the
    // XAML previewer can't load this file (AVLN3001 at build time); that only affects design-time
    // tooling, not the running app.
    public LoginWindow(AccountViewModel account)
    {
        AvaloniaXamlLoader.Load(this);
        this.account = account;
        DataContext = account;
        account.PropertyChanged += OnAccountPropertyChanged;
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            this.FindControl<TextBox>(string.IsNullOrWhiteSpace(account.Email) ? "EmailInput" : "PasswordInput")?.Focus());
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsSignedIn) && account.IsSignedIn)
            Close(true);
    }

    private void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        if (account.IsBusy) return;
        if (!account.ValidateSignInForm())
        {
            this.FindControl<TextBox>(account.EmailError is not null ? "EmailInput" : "PasswordInput")?.Focus();
            return;
        }

        if (account.SignIn.CanExecute.FirstAsync().Wait())
            account.SignIn.Execute().Subscribe();
    }

    protected override void OnClosed(EventArgs e)
    {
        account.PropertyChanged -= OnAccountPropertyChanged;
        base.OnClosed(e);
    }
}
